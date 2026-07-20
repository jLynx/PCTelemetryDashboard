using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using FanControl.IPC;
using Grpc.Core;
using LibreHardwareMonitor.Hardware;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<TelemetryStore>();
builder.Services.AddHostedService<HardwareTelemetryWorker>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/status", (TelemetryStore store) => store.GetStatus());

app.MapGet("/api/current", (TelemetryStore store) => new
{
    generatedUtc = DateTimeOffset.UtcNow,
    readings = store.GetLatest()
});

app.MapPost("/api/log/new", (TelemetryStore store) => Results.Ok(store.StartNewLog()));

app.MapPost("/api/log/reset", (TelemetryStore store) =>
{
    try
    {
        return Results.Ok(store.ResetCurrentLog());
    }
    catch (IOException ex)
    {
        return Results.Problem($"Could not reset the current log: {ex.Message}");
    }
    catch (UnauthorizedAccessException ex)
    {
        return Results.Problem($"Could not reset the current log: {ex.Message}");
    }
});

app.MapPost("/api/log/pause", (TelemetryStore store) => Results.Ok(store.SetCsvLoggingPaused(true)));

app.MapPost("/api/log/resume", (TelemetryStore store) => Results.Ok(store.SetCsvLoggingPaused(false)));

app.MapGet("/api/logs", (TelemetryStore store, IHostEnvironment environment) =>
{
    var logDirectory = EnsureLogDirectory(store, environment);
    var activeLogFileName = store.GetActiveLogFileName();

    var logs = Directory.Exists(logDirectory)
        ? Directory.GetFiles(logDirectory, "telemetry-*.csv")
            .Select(path =>
            {
                var info = new FileInfo(path);
                return new LogFileSummary(
                    info.Name,
                    info.FullName,
                    info.Length,
                    info.LastWriteTimeUtc,
                    string.Equals(info.Name, activeLogFileName, StringComparison.OrdinalIgnoreCase));
            })
            .OrderByDescending(log => log.LastWriteTimeUtc)
            .ThenBy(log => log.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList()
        : [];

    return new LogListResponse(DateTimeOffset.UtcNow, logDirectory, logs);
});

app.MapGet("/api/logs/{fileName}/data", (TelemetryStore store, IHostEnvironment environment, string fileName, int? minutes, string? type, string? sensorIds) =>
{
    try
    {
        var logDirectory = EnsureLogDirectory(store, environment);
        var logFilePath = ResolveLogFilePath(logDirectory, fileName);
        var readings = ReadCsvLog(logFilePath);
        var windowMinutes = Math.Clamp(minutes ?? 30, 1, 360);
        var requestedTypes = ParseCsv(type);
        var requestedSensorIds = ParseCsv(sensorIds);
        DateTimeOffset? latestTimestampUtc = readings.Count == 0 ? null : readings.Max(reading => reading.TimestampUtc);
        var cutoff = latestTimestampUtc?.AddMinutes(-windowMinutes) ?? DateTimeOffset.MinValue;

        var latestReadings = readings
            .GroupBy(reading => reading.SensorId)
            .Select(group => group.OrderByDescending(reading => reading.TimestampUtc).First())
            .OrderBy(reading => TypeRank(reading.SensorType))
            .ThenBy(reading => reading.Hardware, StringComparer.OrdinalIgnoreCase)
            .ThenBy(reading => reading.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var series = readings
            .Where(reading => reading.TimestampUtc >= cutoff)
            .Where(reading => requestedTypes.Count == 0 || requestedTypes.Contains(reading.SensorType))
            .Where(reading => requestedSensorIds.Count == 0 || requestedSensorIds.Contains(reading.SensorId))
            .GroupBy(reading => reading.SensorId)
            .Select(group =>
            {
                var ordered = group.OrderBy(reading => reading.TimestampUtc).ToList();
                var first = ordered[0];
                return new SensorSeries(
                    first.SensorId,
                    first.Hardware,
                    first.HardwareType,
                    first.Name,
                    first.SensorType,
                    first.Unit,
                    Downsample(ordered, 420));
            })
            .OrderBy(item => TypeRank(item.SensorType))
            .ThenBy(item => item.Hardware, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Results.Ok(new LogFileDataResponse(
            DateTimeOffset.UtcNow,
            fileName,
            logFilePath,
            windowMinutes,
            latestTimestampUtc,
            latestReadings.Count,
            readings.Count,
            latestReadings,
            series));
    }
    catch (FileNotFoundException)
    {
        return Results.NotFound($"Log file '{fileName}' was not found.");
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(ex.Message);
    }
    catch (IOException ex)
    {
        return Results.Problem($"Could not read log file '{fileName}': {ex.Message}");
    }
});

app.MapGet("/api/series", (TelemetryStore store, int? minutes, string? type, string? sensorIds) =>
{
    var windowMinutes = Math.Clamp(minutes ?? 30, 1, 360);
    var requestedTypes = ParseCsv(type);
    var requestedSensorIds = ParseCsv(sensorIds);
    var cutoff = DateTimeOffset.UtcNow.AddMinutes(-windowMinutes);

    var readings = store.GetHistory(cutoff)
        .Where(reading => requestedTypes.Count == 0 || requestedTypes.Contains(reading.SensorType))
        .Where(reading => requestedSensorIds.Count == 0 || requestedSensorIds.Contains(reading.SensorId))
        .GroupBy(reading => reading.SensorId)
        .Select(group =>
        {
            var ordered = group.OrderBy(reading => reading.TimestampUtc).ToList();
            var first = ordered[0];
            return new SensorSeries(
                first.SensorId,
                first.Hardware,
                first.HardwareType,
                first.Name,
                first.SensorType,
                first.Unit,
                Downsample(ordered, 420));
        })
        .OrderBy(series => TypeRank(series.SensorType))
        .ThenBy(series => series.Hardware, StringComparer.OrdinalIgnoreCase)
        .ThenBy(series => series.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    return new SeriesResponse(DateTimeOffset.UtcNow, windowMinutes, readings);
});

app.MapGet("/api/export", (IHostEnvironment environment) =>
{
    var logDirectory = Path.Combine(environment.ContentRootPath, "logs");
    if (!Directory.Exists(logDirectory))
    {
        return Results.NotFound("No telemetry logs have been written yet.");
    }

    var logFiles = Directory.GetFiles(logDirectory, "telemetry-*.csv")
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    if (logFiles.Length == 0)
    {
        return Results.NotFound("No telemetry logs have been written yet.");
    }

    var builder = new StringBuilder();
    var wroteHeader = false;

    foreach (var logFile in logFiles)
    {
        var lines = File.ReadLines(logFile);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith("timestamp_local,", StringComparison.OrdinalIgnoreCase))
            {
                if (wroteHeader)
                {
                    continue;
                }

                wroteHeader = true;
            }

            builder.AppendLine(line);
        }
    }

    var fileName = $"pc-telemetry-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
    return Results.File(Encoding.UTF8.GetBytes(builder.ToString()), "text/csv", fileName);
});

app.MapFallbackToFile("index.html");

app.Run();

static HashSet<string> ParseCsv(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return [];
    }

    return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

static string EnsureLogDirectory(TelemetryStore store, IHostEnvironment environment)
{
    if (string.IsNullOrWhiteSpace(store.LogDirectory))
    {
        store.LogDirectory = Path.Combine(environment.ContentRootPath, "logs");
    }

    Directory.CreateDirectory(store.LogDirectory);
    return store.LogDirectory;
}

static string ResolveLogFilePath(string logDirectory, string fileName)
{
    if (string.IsNullOrWhiteSpace(fileName)
        || !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)
        || !fileName.StartsWith("telemetry-", StringComparison.OrdinalIgnoreCase)
        || !fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("Invalid telemetry log file name.");
    }

    var fullDirectory = Path.GetFullPath(logDirectory);
    var fullPath = Path.GetFullPath(Path.Combine(fullDirectory, fileName));
    var directoryPrefix = fullDirectory.EndsWith(Path.DirectorySeparatorChar)
        ? fullDirectory
        : fullDirectory + Path.DirectorySeparatorChar;

    if (!fullPath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("Invalid telemetry log file path.");
    }

    if (!File.Exists(fullPath))
    {
        throw new FileNotFoundException("Telemetry log was not found.", fullPath);
    }

    return fullPath;
}

static IReadOnlyList<SensorReading> ReadCsvLog(string filePath)
{
    var readings = new List<SensorReading>();

    using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

    string? line;
    while ((line = reader.ReadLine()) is not null)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            continue;
        }

        if (line.StartsWith("timestamp_local,", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var columns = ParseCsvLine(line);
        if (columns.Count < 9)
        {
            continue;
        }

        if (!DateTimeOffset.TryParse(
                columns[1],
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var timestampUtc))
        {
            continue;
        }

        if (!double.TryParse(columns[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            continue;
        }

        readings.Add(new SensorReading(
            timestampUtc,
            columns[2],
            columns[4],
            columns[3],
            columns[5],
            columns[6],
            value,
            columns[8]));
    }

    return readings;
}

static IReadOnlyList<string> ParseCsvLine(string line)
{
    var values = new List<string>();
    var field = new StringBuilder();
    var inQuotes = false;

    for (var index = 0; index < line.Length; index++)
    {
        var character = line[index];
        if (inQuotes)
        {
            if (character == '"')
            {
                if (index + 1 < line.Length && line[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else
                {
                    inQuotes = false;
                }
            }
            else
            {
                field.Append(character);
            }

            continue;
        }

        if (character == '"')
        {
            inQuotes = true;
        }
        else if (character == ',')
        {
            values.Add(field.ToString());
            field.Clear();
        }
        else
        {
            field.Append(character);
        }
    }

    values.Add(field.ToString());
    return values;
}

static IReadOnlyList<SeriesPoint> Downsample(IReadOnlyList<SensorReading> readings, int maxPoints)
{
    if (readings.Count <= maxPoints)
    {
        return readings.Select(reading => new SeriesPoint(reading.TimestampUtc, reading.Value)).ToList();
    }

    var bucketSize = (int)Math.Ceiling(readings.Count / (double)maxPoints);
    var points = new List<SeriesPoint>(maxPoints);

    for (var start = 0; start < readings.Count; start += bucketSize)
    {
        var end = Math.Min(start + bucketSize, readings.Count);
        var total = 0d;
        for (var i = start; i < end; i++)
        {
            total += readings[i].Value;
        }

        points.Add(new SeriesPoint(readings[end - 1].TimestampUtc, total / (end - start)));
    }

    return points;
}

static int TypeRank(string sensorType) => sensorType switch
{
    "Temperature" => 0,
    "Control" => 1,
    "Fan" => 2,
    "Load" => 3,
    "Power" => 4,
    "Voltage" => 5,
    "Clock" => 6,
    _ => 99
};

public sealed record SensorReading(
    DateTimeOffset TimestampUtc,
    string SensorId,
    string Hardware,
    string HardwareType,
    string Name,
    string SensorType,
    double Value,
    string Unit);

public sealed record SeriesPoint(DateTimeOffset TimestampUtc, double Value);

public sealed record SensorSeries(
    string SensorId,
    string Hardware,
    string HardwareType,
    string Name,
    string SensorType,
    string Unit,
    IReadOnlyList<SeriesPoint> Points);

public sealed record SeriesResponse(
    DateTimeOffset GeneratedUtc,
    int WindowMinutes,
    IReadOnlyList<SensorSeries> Series);

public sealed record LogFileSummary(
    string FileName,
    string FilePath,
    long SizeBytes,
    DateTimeOffset LastWriteTimeUtc,
    bool IsActive);

public sealed record LogListResponse(
    DateTimeOffset GeneratedUtc,
    string LogDirectory,
    IReadOnlyList<LogFileSummary> Logs);

public sealed record LogFileDataResponse(
    DateTimeOffset GeneratedUtc,
    string FileName,
    string FilePath,
    int WindowMinutes,
    DateTimeOffset? LatestTimestampUtc,
    int LatestReadingCount,
    int HistoryReadingCount,
    IReadOnlyList<SensorReading> Readings,
    IReadOnlyList<SensorSeries> Series);

public sealed record TelemetryStatus(
    bool IsRunning,
    int LatestReadingCount,
    int HistoryReadingCount,
    DateTimeOffset? LatestTimestampUtc,
    string LogDirectory,
    string ActiveLogFileName,
    string ActiveLogPath,
    double PollIntervalSeconds,
    double LogIntervalSeconds,
    bool IsCsvLoggingPaused,
    string? LastError,
    string? Note);

public sealed record LogActionResult(
    DateTimeOffset TimestampUtc,
    string ActiveLogFileName,
    string ActiveLogPath,
    bool IsCsvLoggingPaused,
    string Message);

public sealed class TelemetryStore
{
    private const int MaxHistoryRows = 2_000_000;
    private static readonly TimeSpan MaxHistoryAge = TimeSpan.FromHours(6);

    private readonly object _gate = new();
    private readonly List<SensorReading> _history = [];
    private IReadOnlyList<SensorReading> _latest = [];
    private string _activeLogFileName = "";
    private bool _forceNextLogWrite;
    private bool _isCsvLoggingPaused;
    private string? _lastError;
    private string? _note = "Waiting for first hardware sample.";

    public object LogFileGate { get; } = new();

    public TimeSpan PollInterval { get; } = TimeSpan.FromSeconds(1);

    public TimeSpan LogInterval { get; } = TimeSpan.FromSeconds(5);

    public string LogDirectory { get; set; } = "";

    public void EnsureActiveLog()
    {
        lock (_gate)
        {
            EnsureActiveLogUnlocked();
        }
    }

    public void AddSnapshot(IReadOnlyList<SensorReading> readings)
    {
        lock (_gate)
        {
            _latest = readings;
            _history.AddRange(readings);

            var cutoff = DateTimeOffset.UtcNow.Subtract(MaxHistoryAge);
            _history.RemoveAll(reading => reading.TimestampUtc < cutoff);

            if (_history.Count > MaxHistoryRows)
            {
                _history.RemoveRange(0, _history.Count - MaxHistoryRows);
            }

            _lastError = null;
            _note = readings.Count == 0
                ? "No numeric sensors were returned. Try running the dashboard as Administrator if fan or motherboard sensors are missing."
                : null;
        }
    }

    public LogActionResult StartNewLog()
    {
        lock (_gate)
        {
            EnsureActiveLogUnlocked();
            _activeLogFileName = CreateUniqueLogFileNameUnlocked();
            _history.Clear();
            _forceNextLogWrite = true;
            _note = _isCsvLoggingPaused
                ? $"Started new log {_activeLogFileName}. CSV logging is paused."
                : $"Started new log {_activeLogFileName}. The next sample will be written immediately.";

            return CreateLogActionResultUnlocked("Started a new log and cleared the chart history.");
        }
    }

    public LogActionResult ResetCurrentLog()
    {
        string activeLogPath;
        LogActionResult result;
        lock (_gate)
        {
            EnsureActiveLogUnlocked();
            activeLogPath = GetActiveLogPathUnlocked();
            _history.Clear();
            _forceNextLogWrite = true;
            _note = _isCsvLoggingPaused
                ? $"Reset current log {_activeLogFileName}. CSV logging is paused."
                : $"Reset current log {_activeLogFileName}. The next sample will be written immediately.";
            result = CreateLogActionResultUnlocked("Reset the current log and cleared the chart history.");
        }

        lock (LogFileGate)
        {
            if (File.Exists(activeLogPath))
            {
                File.Delete(activeLogPath);
            }
        }

        return result;
    }

    public LogActionResult SetCsvLoggingPaused(bool isPaused)
    {
        lock (_gate)
        {
            EnsureActiveLogUnlocked();

            if (_isCsvLoggingPaused == isPaused)
            {
                _note = isPaused
                    ? "CSV logging is already paused. Live telemetry is still updating."
                    : $"CSV logging is already writing to {_activeLogFileName}.";

                return CreateLogActionResultUnlocked(_note);
            }

            _isCsvLoggingPaused = isPaused;
            if (isPaused)
            {
                _note = "CSV logging is paused. Live telemetry is still updating.";
                return CreateLogActionResultUnlocked("CSV logging paused.");
            }

            _forceNextLogWrite = true;
            _note = $"CSV logging resumed for {_activeLogFileName}. The next sample will be written immediately.";
            return CreateLogActionResultUnlocked("CSV logging resumed.");
        }
    }

    public bool ShouldWriteCsvSnapshot(DateTimeOffset timestampUtc, DateTimeOffset lastCsvWriteUtc)
    {
        lock (_gate)
        {
            if (_isCsvLoggingPaused)
            {
                return false;
            }

            if (!_forceNextLogWrite && timestampUtc - lastCsvWriteUtc < LogInterval)
            {
                return false;
            }

            _forceNextLogWrite = false;
            return true;
        }
    }

    public string GetActiveLogFilePath()
    {
        lock (_gate)
        {
            EnsureActiveLogUnlocked();
            return GetActiveLogPathUnlocked();
        }
    }

    public string GetActiveLogFileName()
    {
        lock (_gate)
        {
            EnsureActiveLogUnlocked();
            return _activeLogFileName;
        }
    }

    public void SetError(string message)
    {
        lock (_gate)
        {
            _lastError = message;
        }
    }

    public void SetNote(string? message)
    {
        lock (_gate)
        {
            _note = message;
        }
    }

    public IReadOnlyList<SensorReading> GetLatest()
    {
        lock (_gate)
        {
            return _latest.ToList();
        }
    }

    public IReadOnlyList<SensorReading> GetHistory(DateTimeOffset cutoffUtc)
    {
        lock (_gate)
        {
            return _history
                .Where(reading => reading.TimestampUtc >= cutoffUtc)
                .ToList();
        }
    }

    public TelemetryStatus GetStatus()
    {
        lock (_gate)
        {
            EnsureActiveLogUnlocked();
            return new TelemetryStatus(
                IsRunning: _latest.Count > 0 && _lastError is null,
                LatestReadingCount: _latest.Count,
                HistoryReadingCount: _history.Count,
                LatestTimestampUtc: _latest.Count == 0 ? null : _latest.Max(reading => reading.TimestampUtc),
                LogDirectory: LogDirectory,
                ActiveLogFileName: _activeLogFileName,
                ActiveLogPath: GetActiveLogPathUnlocked(),
                PollIntervalSeconds: PollInterval.TotalSeconds,
                LogIntervalSeconds: LogInterval.TotalSeconds,
                IsCsvLoggingPaused: _isCsvLoggingPaused,
                LastError: _lastError,
                Note: _note);
        }
    }

    private void EnsureActiveLogUnlocked()
    {
        if (string.IsNullOrWhiteSpace(_activeLogFileName))
        {
            _activeLogFileName = $"telemetry-{DateTime.Now:yyyyMMdd}.csv";
        }
    }

    private string GetActiveLogPathUnlocked() => string.IsNullOrWhiteSpace(LogDirectory)
        ? _activeLogFileName
        : Path.Combine(LogDirectory, _activeLogFileName);

    private string CreateUniqueLogFileNameUnlocked()
    {
        var timestamp = DateTime.Now;
        for (var index = 0; index < 100; index++)
        {
            var suffix = index == 0 ? "" : $"-{index + 1}";
            var fileName = $"telemetry-{timestamp:yyyyMMdd-HHmmss}{suffix}.csv";
            if (string.IsNullOrWhiteSpace(LogDirectory) || !File.Exists(Path.Combine(LogDirectory, fileName)))
            {
                return fileName;
            }
        }

        return $"telemetry-{timestamp:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.csv";
    }

    private LogActionResult CreateLogActionResultUnlocked(string message) => new(
        TimestampUtc: DateTimeOffset.UtcNow,
        ActiveLogFileName: _activeLogFileName,
        ActiveLogPath: GetActiveLogPathUnlocked(),
        IsCsvLoggingPaused: _isCsvLoggingPaused,
        Message: message);
}

public sealed class HardwareTelemetryWorker(
    TelemetryStore store,
    IHostEnvironment environment,
    ILogger<HardwareTelemetryWorker> logger) : BackgroundService
{
    private sealed record FocusedLogSensor(string Label, string Hardware, string SensorType, string[] Names);

    private static readonly IReadOnlyList<FocusedLogSensor> FocusedLogSensors =
    [
        new("Core (Tctl/Tdie)", "AMD Ryzen 9 9950X", "Temperature", ["Core (Tctl/Tdie)", "CPU Package", "Package"]),
        new("CCD1 (Tdie)", "AMD Ryzen 9 9950X", "Temperature", ["CCD1 (Tdie)", "CCD1"]),
        new("CCD2 (Tdie)", "AMD Ryzen 9 9950X", "Temperature", ["CCD2 (Tdie)", "CCD2"]),
        new("CCDs Max (Tdie)", "AMD Ryzen 9 9950X", "Temperature", ["CCDs Max (Tdie)", "CCD Max"]),
        new("CCDs Average (Tdie)", "AMD Ryzen 9 9950X", "Temperature", ["CCDs Average (Tdie)", "CCD Average"]),
        new("GPU VR SoC", "AMD Radeon(TM) Graphics", "Temperature", ["GPU VR SoC"]),
        new("System #1", "Gigabyte X870 AORUS ELITE WIFI7", "Temperature", ["System #1", "System 1", "Temperature #1"]),
        new("PCH", "Gigabyte X870 AORUS ELITE WIFI7", "Temperature", ["PCH"]),
        new("CPU", "Gigabyte X870 AORUS ELITE WIFI7", "Temperature", ["CPU"]),
        new("PCIe x16", "Gigabyte X870 AORUS ELITE WIFI7", "Temperature", ["PCIe x16", "PCIEX16"]),
        new("VRM MOS", "Gigabyte X870 AORUS ELITE WIFI7", "Temperature", ["VRM MOS", "VRM"]),
        new("PCIe x4", "Gigabyte X870 AORUS ELITE WIFI7", "Temperature", ["PCIe x4", "PCIEX4"]),
        new("System #2", "Gigabyte X870 AORUS ELITE WIFI7", "Temperature", ["System #2", "System 2", "Temperature #2"]),
        new("GPU", "NVIDIA GeForce RTX 5070 Ti", "Temperature", ["GPU", "GPU Core"]),
        new("CPU Total", "AMD Ryzen 9 9950X", "Load", ["CPU Total"]),
        new("CPU Core Max", "AMD Ryzen 9 9950X", "Load", ["CPU Core Max"]),
        new("GPU Core", "NVIDIA GeForce RTX 5070 Ti", "Load", ["GPU Core", "GPU"]),
        new("GPU Memory Controller", "NVIDIA GeForce RTX 5070 Ti", "Load", ["GPU Memory Controller", "Memory Controller"]),
        new("CPU Package Power", "AMD Ryzen 9 9950X", "Power", ["Package", "CPU Package", "CPU Package Power"]),
        new("GPU Power", "NVIDIA GeForce RTX 5070 Ti", "Power", ["GPU Package", "GPU Power", "Total Board Power", "Board Power"]),
        new("Rad Fan 1", "Gigabyte X870 AORUS ELITE WIFI7", "Control", ["System Fan #3", "Fan #3", "Fan 3", "SYS Fan 3"]),
        new("Rad Fan 1", "Gigabyte X870 AORUS ELITE WIFI7", "Fan", ["System Fan #3", "Fan #3", "Fan 3", "SYS Fan 3"]),
        new("Rad Fan 2", "Gigabyte X870 AORUS ELITE WIFI7", "Control", ["System Fan #2", "Fan #2", "Fan 2", "SYS Fan 2"]),
        new("Rad Fan 2", "Gigabyte X870 AORUS ELITE WIFI7", "Fan", ["System Fan #2", "Fan #2", "Fan 2", "SYS Fan 2"]),
        new("IO Fan", "Gigabyte X870 AORUS ELITE WIFI7", "Control", ["CPU Optional Fan", "CPU OPT Fan", "CPU_OPT"]),
        new("IO Fan", "Gigabyte X870 AORUS ELITE WIFI7", "Fan", ["CPU Optional Fan", "CPU OPT Fan", "CPU_OPT"]),
        new("PCIe Fan", "Gigabyte X870 AORUS ELITE WIFI7", "Control", ["CPU Fan", "CPU_FAN"]),
        new("PCIe Fan", "Gigabyte X870 AORUS ELITE WIFI7", "Fan", ["CPU Fan", "CPU_FAN"]),
        new("Right Fan Exhaust", "Gigabyte X870 AORUS ELITE WIFI7", "Control", ["System Fan #1", "Fan #1", "Fan 1", "SYS Fan 1"]),
        new("Right Fan Exhaust", "Gigabyte X870 AORUS ELITE WIFI7", "Fan", ["System Fan #1", "Fan #1", "Fan 1", "SYS Fan 1"])
    ];

    private Computer? _computer;
    private string? _fanControlLastError;
    private DateTimeOffset _lastCsvWriteUtc = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        store.LogDirectory = Path.Combine(environment.ContentRootPath, "logs");
        Directory.CreateDirectory(store.LogDirectory);
        store.EnsureActiveLog();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                EnsureComputerOpen();
                var snapshot = ReadSnapshot(DateTimeOffset.UtcNow);
                store.AddSnapshot(snapshot);
                if (_fanControlLastError is not null)
                {
                    store.SetNote(_fanControlLastError.Contains("denied", StringComparison.OrdinalIgnoreCase)
                        ? "FanControl is running but denied sensor access. Run this dashboard as Administrator, or run it at the same elevation as FanControl, to read FanControl's sensor values."
                        : $"FanControl sensor source unavailable: {_fanControlLastError}");
                }

                var sampleCompletedUtc = DateTimeOffset.UtcNow;
                if (store.ShouldWriteCsvSnapshot(sampleCompletedUtc, _lastCsvWriteUtc))
                {
                    await WriteCsvSnapshot(snapshot, stoppingToken);
                    _lastCsvWriteUtc = sampleCompletedUtc;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Hardware telemetry sample failed.");
                store.SetError(ex.Message);
            }

            await Task.Delay(store.PollInterval, stoppingToken);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _computer?.Close();
        return base.StopAsync(cancellationToken);
    }

    private void EnsureComputerOpen()
    {
        if (_computer is not null)
        {
            return;
        }

        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsStorageEnabled = true,
            IsNetworkEnabled = true
        };

        _computer.Open();
        store.SetNote("Hardware monitor opened. First sample is being collected.");
    }

    private IReadOnlyList<SensorReading> ReadSnapshot(DateTimeOffset timestampUtc)
    {
        if (_computer is null)
        {
            return [];
        }

        var readings = new List<SensorReading>();

        readings.AddRange(ReadFanControlSnapshot(timestampUtc));

        foreach (var hardware in _computer.Hardware)
        {
            ReadHardware(hardware, timestampUtc, readings);
        }

        return readings;
    }

    private IReadOnlyList<SensorReading> ReadFanControlSnapshot(DateTimeOffset timestampUtc)
    {
        try
        {
            var client = IPCFactory.GetSensorClient();
            var allSensors = client.GetAllSensors(new GetAllSensorsRequest());
            var ids = allSensors.Sensors
                .Where(sensor => ToLocalSensorType(sensor.Type) is not null)
                .Select(sensor => sensor.Identifier)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (ids.Count == 0)
            {
                _fanControlLastError = null;
                return [];
            }

            var valuesRequest = new ReadSensorValuesRequest();
            valuesRequest.Ids.AddRange(ids);
            var values = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var item in client.ReadSensorValues(valuesRequest).Values)
                {
                    values[item.Key] = item.Value;
                }
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
            {
                // FanControl V271 exposes current values through GetAllSensors but does not implement ReadSensorValues.
            }

            var readings = new List<SensorReading>();
            foreach (var sensor in allSensors.Sensors)
            {
                var sensorType = ToLocalSensorType(sensor.Type);
                if (sensorType is null || string.IsNullOrWhiteSpace(sensor.Identifier))
                {
                    continue;
                }

                var hasLiveValue = values.TryGetValue(sensor.Identifier, out var value);
                if (!hasLiveValue)
                {
                    value = sensor.Value;
                    hasLiveValue = sensor.HasValue;
                }

                if (!hasLiveValue || float.IsNaN(value) || float.IsInfinity(value))
                {
                    continue;
                }

                if (sensorType == "Temperature" && IsInvalidTemperature(sensor.Name, value))
                {
                    continue;
                }

                readings.Add(new SensorReading(
                    timestampUtc,
                    $"fancontrol:{sensor.Identifier}",
                    string.IsNullOrWhiteSpace(sensor.Origin) ? "FanControl" : sensor.Origin,
                    "FanControl",
                    sensor.Name,
                    sensorType,
                    Math.Round(value, 3),
                    UnitForFanControlType(sensor.Type)));
            }

            _fanControlLastError = null;
            return readings;
        }
        catch (Exception ex)
        {
            if (_fanControlLastError != ex.Message)
            {
                logger.LogInformation(ex, "FanControl IPC sensor read failed. Falling back to local hardware sensors.");
            }

            _fanControlLastError = ex.Message;
            return [];
        }
    }

    private static void ReadHardware(IHardware hardware, DateTimeOffset timestampUtc, List<SensorReading> readings)
    {
        hardware.Update();

        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.Value is null || float.IsNaN(sensor.Value.Value) || float.IsInfinity(sensor.Value.Value))
            {
                continue;
            }

            var value = sensor.Value.Value;
            if (sensor.SensorType == SensorType.Temperature && IsInvalidTemperature(sensor.Name, value))
            {
                continue;
            }

            readings.Add(new SensorReading(
                timestampUtc,
                sensor.Identifier.ToString(),
                hardware.Name,
                hardware.HardwareType.ToString(),
                sensor.Name,
                sensor.SensorType.ToString(),
                Math.Round(value, 3),
                UnitFor(sensor.SensorType)));
        }

        foreach (var subHardware in hardware.SubHardware)
        {
            ReadHardware(subHardware, timestampUtc, readings);
        }
    }

    private async Task WriteCsvSnapshot(IReadOnlyList<SensorReading> snapshot, CancellationToken cancellationToken)
    {
        var focusedSnapshot = BuildFocusedLogSnapshot(snapshot);
        if (focusedSnapshot.Count == 0)
        {
            return;
        }

        var filePath = store.GetActiveLogFilePath();
        lock (store.LogFileGate)
        {
            var writeHeader = !File.Exists(filePath);

            using var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, Encoding.UTF8);

            if (writeHeader)
            {
                writer.WriteLine("timestamp_local,timestamp_utc,sensor_id,hardware_type,hardware,name,sensor_type,value,unit");
            }

            foreach (var reading in focusedSnapshot)
            {
                var line = string.Join(',', new[]
                {
                    CsvEscape(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)),
                    CsvEscape(reading.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)),
                    CsvEscape(reading.SensorId),
                    CsvEscape(reading.HardwareType),
                    CsvEscape(reading.Hardware),
                    CsvEscape(reading.Name),
                    CsvEscape(reading.SensorType),
                    CsvEscape(reading.Value.ToString("0.###", CultureInfo.InvariantCulture)),
                    CsvEscape(reading.Unit)
                });

                writer.WriteLine(line);
            }
        }

        await Task.CompletedTask;
    }

    private static IReadOnlyList<SensorReading> BuildFocusedLogSnapshot(IReadOnlyList<SensorReading> snapshot)
    {
        if (snapshot.Count == 0)
        {
            return [];
        }

        var focused = new List<SensorReading>(FocusedLogSensors.Count);
        foreach (var spec in FocusedLogSensors)
        {
            var reading = FindFocusedReading(snapshot, spec);
            if (reading is null)
            {
                continue;
            }

            focused.Add(reading with { Name = spec.Label });
        }

        return focused;
    }

    private static SensorReading? FindFocusedReading(IReadOnlyList<SensorReading> snapshot, FocusedLogSensor spec)
    {
        var candidates = snapshot
            .Where(reading => SameText(reading.SensorType, spec.SensorType))
            .Where(reading => SameText(reading.Hardware, spec.Hardware)
                || NormalizeText(reading.Hardware).Contains(NormalizeText(spec.Hardware), StringComparison.Ordinal)
                || NormalizeText(spec.Hardware).Contains(NormalizeText(reading.Hardware), StringComparison.Ordinal))
            .ToList();

        var exact = candidates.FirstOrDefault(reading => spec.Names.Any(name => SameText(reading.Name, name)));
        if (exact is not null)
        {
            return exact;
        }

        return candidates.FirstOrDefault(reading => spec.Names.Any(name =>
            NormalizeText(reading.Name).Contains(NormalizeText(name), StringComparison.Ordinal)
            || NormalizeText(name).Contains(NormalizeText(reading.Name), StringComparison.Ordinal)));
    }

    private static string CsvEscape(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static bool SameText(string left, string right) => NormalizeText(left) == NormalizeText(right);

    private static string NormalizeText(string value) => value.Trim().ToLowerInvariant();

    private static string UnitFor(SensorType type) => type switch
    {
        SensorType.Voltage => "V",
        SensorType.Clock => "MHz",
        SensorType.Temperature => "C",
        SensorType.Load => "%",
        SensorType.Fan => "RPM",
        SensorType.Flow => "L/h",
        SensorType.Control => "%",
        SensorType.Level => "%",
        SensorType.Factor => "x",
        SensorType.Power => "W",
        SensorType.Data => "GB",
        SensorType.SmallData => "MB",
        SensorType.Throughput => "MB/s",
        SensorType.Energy => "mWh",
        SensorType.Noise => "dBA",
        _ => ""
    };

    private static string? ToLocalSensorType(SensorMessageType type) => type switch
    {
        SensorMessageType.Control => "Control",
        SensorMessageType.Rpm => "Fan",
        SensorMessageType.Temperature => "Temperature",
        SensorMessageType.UsagePercent => "Load",
        SensorMessageType.Frequency => "Clock",
        SensorMessageType.Voltage => "Voltage",
        SensorMessageType.Power => "Power",
        SensorMessageType.Data => "Data",
        _ => null
    };

    private static string UnitForFanControlType(SensorMessageType type) => type switch
    {
        SensorMessageType.Control => "%",
        SensorMessageType.Rpm => "RPM",
        SensorMessageType.Temperature => "C",
        SensorMessageType.UsagePercent => "%",
        SensorMessageType.Frequency => "MHz",
        SensorMessageType.Voltage => "V",
        SensorMessageType.Power => "W",
        SensorMessageType.Data => "GB",
        _ => ""
    };

    private static bool IsInvalidTemperature(string sensorName, float value)
    {
        if (value <= 0)
        {
            return true;
        }

        return sensorName.Contains("Critical Temperature", StringComparison.OrdinalIgnoreCase)
            || sensorName.Contains("Warning Temperature", StringComparison.OrdinalIgnoreCase);
    }
}

using System.Globalization;
using System.Text;

namespace FanControl.PCTelemetryDashboard;

internal sealed class TelemetryState
{
    private static readonly TimeSpan MaxHistoryAge = TimeSpan.FromHours(6);
    private const int MaxHistoryRows = 750_000;

    private readonly object _gate = new();
    private readonly object _logFileGate = new();
    private readonly Queue<SensorReading> _history = new();
    private IReadOnlyList<SensorReading> _latest = [];
    private UsbDisplayStatus _displayStatus = new(
        false, null, null, null, 0, 0, null,
        "PC Telemetry Display is not connected.");
    private string _activeLogFileName = $"telemetry-{DateTime.Now:yyyyMMdd}.csv";
    private bool _isCsvLoggingPaused = true;
    private bool _forceNextLogWrite;
    private DateTimeOffset _lastCsvWriteUtc = DateTimeOffset.MinValue;
    private string? _lastError;
    private string? _note = "Waiting for the first FanControl sensor sample. CSV logging starts paused.";

    public TelemetryState()
    {
        LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCTelemetryDashboard",
            "plugin-logs");
        Directory.CreateDirectory(LogDirectory);
    }

    public string LogDirectory { get; }

    public void AddSnapshot(IReadOnlyList<SensorReading> readings)
    {
        List<SensorReading>? csvSnapshot = null;
        string? csvPath = null;

        lock (_gate)
        {
            _latest = readings.ToList();
            foreach (var reading in readings.Where(DashboardSensorFilter.ShouldRetainHistory))
            {
                _history.Enqueue(reading);
            }

            var cutoff = DateTimeOffset.UtcNow.Subtract(MaxHistoryAge);
            while (_history.TryPeek(out var oldest)
                   && (oldest.TimestampUtc < cutoff || _history.Count > MaxHistoryRows))
            {
                _history.Dequeue();
            }

            _lastError = null;
            _note = readings.Count == 0
                ? "FanControl returned no numeric sensor readings."
                : null;

            var now = DateTimeOffset.UtcNow;
            if (!_isCsvLoggingPaused
                && (_forceNextLogWrite || now - _lastCsvWriteUtc >= TimeSpan.FromSeconds(5)))
            {
                _forceNextLogWrite = false;
                _lastCsvWriteUtc = now;
                csvSnapshot = readings
                    .Where(DashboardSensorFilter.ShouldRetainHistory)
                    .ToList();
                csvPath = GetActiveLogPathUnlocked();
            }
        }

        if (csvSnapshot is not null && csvPath is not null)
        {
            lock (_logFileGate)
            {
                WriteCsvSnapshot(csvPath, csvSnapshot);
            }
        }
    }

    public void SetSensorError(string message)
    {
        lock (_gate)
        {
            _lastError = message;
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
            return _history.Where(reading => reading.TimestampUtc >= cutoffUtc).ToList();
        }
    }

    public TelemetryStatus GetStatus()
    {
        lock (_gate)
        {
            return new TelemetryStatus(
                IsRunning: _latest.Count > 0 && _lastError is null,
                LatestReadingCount: _latest.Count,
                HistoryReadingCount: _history.Count,
                LatestTimestampUtc: _latest.Count == 0
                    ? null
                    : _latest.Max(reading => reading.TimestampUtc),
                LogDirectory,
                _activeLogFileName,
                GetActiveLogPathUnlocked(),
                PollIntervalSeconds: 1,
                LogIntervalSeconds: 5,
                _isCsvLoggingPaused,
                _lastError,
                _note);
        }
    }

    public void SetDisplayStatus(UsbDisplayStatus status)
    {
        lock (_gate)
        {
            _displayStatus = status;
        }
    }

    public UsbDisplayStatus GetDisplayStatus()
    {
        lock (_gate)
        {
            return _displayStatus;
        }
    }

    public LogActionResult StartNewLog()
    {
        lock (_gate)
        {
            _activeLogFileName = CreateUniqueLogFileNameUnlocked();
            _history.Clear();
            _forceNextLogWrite = true;
            _note = _isCsvLoggingPaused
                ? $"Started new log {_activeLogFileName}. CSV logging is paused."
                : $"Started new log {_activeLogFileName}.";
            return CreateLogActionResultUnlocked("Started a new log and cleared chart history.");
        }
    }

    public LogActionResult ResetCurrentLog()
    {
        string path;
        LogActionResult result;
        lock (_gate)
        {
            _history.Clear();
            _forceNextLogWrite = true;
            path = GetActiveLogPathUnlocked();
            result = CreateLogActionResultUnlocked("Reset the current log and cleared chart history.");
        }

        lock (_logFileGate)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        return result;
    }

    public LogActionResult SetCsvLoggingPaused(bool paused)
    {
        lock (_gate)
        {
            _isCsvLoggingPaused = paused;
            if (!paused)
            {
                _forceNextLogWrite = true;
            }

            _note = paused
                ? "CSV logging is paused. Live telemetry is still updating."
                : $"CSV logging resumed for {_activeLogFileName}.";
            return CreateLogActionResultUnlocked(paused ? "CSV logging paused." : "CSV logging resumed.");
        }
    }

    public LogListResponse GetLogs()
    {
        string active;
        lock (_gate)
        {
            active = _activeLogFileName;
        }

        var logs = Directory.GetFiles(LogDirectory, "telemetry-*.csv")
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .Select(info => new LogFileSummary(
                info.Name,
                info.FullName,
                info.Length,
                info.LastWriteTimeUtc,
                string.Equals(info.Name, active, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return new LogListResponse(DateTimeOffset.UtcNow, LogDirectory, logs);
    }

    public string ResolveLogPath(string fileName)
    {
        var safeName = Path.GetFileName(fileName);
        if (!string.Equals(safeName, fileName, StringComparison.Ordinal)
            || !safeName.StartsWith("telemetry-", StringComparison.OrdinalIgnoreCase)
            || !safeName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Invalid telemetry log file name.");
        }

        return Path.Combine(LogDirectory, safeName);
    }

    private string GetActiveLogPathUnlocked() => Path.Combine(LogDirectory, _activeLogFileName);

    private string CreateUniqueLogFileNameUnlocked()
    {
        var timestamp = DateTime.Now;
        for (var index = 0; index < 100; index++)
        {
            var suffix = index == 0 ? "" : $"-{index + 1}";
            var candidate = $"telemetry-{timestamp:yyyyMMdd-HHmmss}{suffix}.csv";
            if (!File.Exists(Path.Combine(LogDirectory, candidate)))
            {
                return candidate;
            }
        }

        return $"telemetry-{timestamp:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.csv";
    }

    private LogActionResult CreateLogActionResultUnlocked(string message) => new(
        DateTimeOffset.UtcNow,
        _activeLogFileName,
        GetActiveLogPathUnlocked(),
        _isCsvLoggingPaused,
        message);

    private static void WriteCsvSnapshot(string filePath, IReadOnlyList<SensorReading> readings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var needsHeader = !File.Exists(filePath) || new FileInfo(filePath).Length == 0;
        using var writer = new StreamWriter(filePath, append: true, new UTF8Encoding(false));
        if (needsHeader)
        {
            writer.WriteLine("timestamp_local,timestamp_utc,sensor_id,hardware_type,hardware,name,sensor_type,value,unit");
        }

        foreach (var reading in readings)
        {
            writer.WriteLine(string.Join(",",
                CsvEscape(reading.TimestampUtc.ToLocalTime().ToString("O", CultureInfo.InvariantCulture)),
                CsvEscape(reading.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)),
                CsvEscape(reading.SensorId),
                CsvEscape(reading.HardwareType),
                CsvEscape(reading.Hardware),
                CsvEscape(reading.Name),
                CsvEscape(reading.SensorType),
                reading.Value.ToString(CultureInfo.InvariantCulture),
                CsvEscape(reading.Unit)));
        }
    }

    private static string CsvEscape(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"")}\"";
}

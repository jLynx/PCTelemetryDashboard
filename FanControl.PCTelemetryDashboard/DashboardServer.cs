using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace FanControl.PCTelemetryDashboard;

internal sealed class DashboardServer(
    TelemetryState state,
    Action<string> log)
{
    private const string Prefix = "http://localhost:5127/";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly object _listenerGate = new();
    private readonly byte[] _indexHtml = LoadIndexHtml();
    private HttpListener? _listener;
    private string? _lastStartError;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var listener = new HttpListener();
            listener.Prefixes.Add(Prefix);

            try
            {
                listener.Start();
                lock (_listenerGate)
                {
                    _listener = listener;
                }

                if (_lastStartError is not null)
                {
                    log("Dashboard web server recovered and is listening on http://localhost:5127.");
                }
                else
                {
                    log("Dashboard web server listening on http://localhost:5127.");
                }
                _lastStartError = null;

                while (!cancellationToken.IsCancellationRequested && listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        // Stop() below unblocks this operation. Await the
                        // original task directly so a later disposal exception
                        // cannot become an unobserved task exception.
                        context = await listener.GetContextAsync().ConfigureAwait(false);
                    }
                    catch (HttpListenerException) when (
                        cancellationToken.IsCancellationRequested || !listener.IsListening)
                    {
                        break;
                    }
                    catch (ObjectDisposedException) when (
                        cancellationToken.IsCancellationRequested || !listener.IsListening)
                    {
                        break;
                    }

                    _ = Task.Run(() => HandleSafelyAsync(context), CancellationToken.None);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!string.Equals(_lastStartError, ex.Message, StringComparison.Ordinal))
                {
                    _lastStartError = ex.Message;
                    log($"Dashboard web server could not bind to port 5127: {ex}");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
            finally
            {
                lock (_listenerGate)
                {
                    if (ReferenceEquals(_listener, listener))
                    {
                        _listener = null;
                    }
                }

                if (listener.IsListening)
                {
                    listener.Stop();
                }
            }
        }
    }

    public void Stop()
    {
        lock (_listenerGate)
        {
            try
            {
                if (_listener?.IsListening == true)
                {
                    _listener.Stop();
                }
            }
            catch
            {
                // Close is best-effort and only exists to unblock GetContextAsync.
            }
        }
    }

    private async Task HandleSafelyAsync(HttpListenerContext context)
    {
        try
        {
            await HandleAsync(context).ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            await WriteTextAsync(context.Response, ex.Message, 404).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            await WriteTextAsync(context.Response, ex.Message, 400).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log($"Dashboard request failed: {ex}");
            await WriteTextAsync(context.Response, "Dashboard request failed.", 500).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch
            {
                // The browser may have disconnected before the response completed.
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var path = request.Url?.AbsolutePath ?? "/";
        var method = request.HttpMethod;

        if (method == "GET" && (path == "/" || !path.StartsWith("/api/", StringComparison.Ordinal)))
        {
            await WriteBytesAsync(context.Response, _indexHtml, "text/html; charset=utf-8").ConfigureAwait(false);
            return;
        }

        if (method == "GET" && path == "/api/status")
        {
            await WriteJsonAsync(context.Response, state.GetStatus()).ConfigureAwait(false);
            return;
        }

        if (method == "GET" && path == "/api/display/status")
        {
            await WriteJsonAsync(context.Response, state.GetDisplayStatus()).ConfigureAwait(false);
            return;
        }

        if (method == "GET" && path == "/api/current")
        {
            await WriteJsonAsync(context.Response, new
            {
                generatedUtc = DateTimeOffset.UtcNow,
                readings = state.GetLatest()
            }).ConfigureAwait(false);
            return;
        }

        if (method == "GET" && path == "/api/series")
        {
            await WriteJsonAsync(context.Response, BuildSeriesResponse(request)).ConfigureAwait(false);
            return;
        }

        if (method == "GET" && path == "/api/logs")
        {
            await WriteJsonAsync(context.Response, state.GetLogs()).ConfigureAwait(false);
            return;
        }

        if (method == "GET"
            && path.StartsWith("/api/logs/", StringComparison.Ordinal)
            && path.EndsWith("/data", StringComparison.Ordinal))
        {
            var encodedName = path["/api/logs/".Length..^"/data".Length];
            var fileName = Uri.UnescapeDataString(encodedName);
            await WriteJsonAsync(context.Response, BuildLogFileResponse(fileName, request)).ConfigureAwait(false);
            return;
        }

        if (method == "GET" && path == "/api/export")
        {
            await WriteExportAsync(context.Response).ConfigureAwait(false);
            return;
        }

        if (method == "POST" && path == "/api/log/new")
        {
            await WriteJsonAsync(context.Response, state.StartNewLog()).ConfigureAwait(false);
            return;
        }

        if (method == "POST" && path == "/api/log/reset")
        {
            await WriteJsonAsync(context.Response, state.ResetCurrentLog()).ConfigureAwait(false);
            return;
        }

        if (method == "POST" && path == "/api/log/pause")
        {
            await WriteJsonAsync(context.Response, state.SetCsvLoggingPaused(true)).ConfigureAwait(false);
            return;
        }

        if (method == "POST" && path == "/api/log/resume")
        {
            await WriteJsonAsync(context.Response, state.SetCsvLoggingPaused(false)).ConfigureAwait(false);
            return;
        }

        await WriteTextAsync(context.Response, "Not found.", 404).ConfigureAwait(false);
    }

    private SeriesResponse BuildSeriesResponse(HttpListenerRequest request)
    {
        var minutes = ParseMinutes(request.QueryString["minutes"]);
        var requestedTypes = ParseCsvSet(request.QueryString["type"]);
        var requestedSensorIds = ParseCsvSet(request.QueryString["sensorIds"]);
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-minutes);
        var series = BuildSeries(
            state.GetHistory(cutoff), requestedTypes, requestedSensorIds);
        return new SeriesResponse(DateTimeOffset.UtcNow, minutes, series);
    }

    private LogFileDataResponse BuildLogFileResponse(
        string fileName,
        HttpListenerRequest request)
    {
        var path = state.ResolveLogPath(fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Log file '{fileName}' was not found.", path);
        }

        var allReadings = ReadCsvLog(path);
        var minutes = ParseMinutes(request.QueryString["minutes"]);
        var requestedTypes = ParseCsvSet(request.QueryString["type"]);
        var requestedSensorIds = ParseCsvSet(request.QueryString["sensorIds"]);
        var latestTimestamp = allReadings.Count == 0
            ? (DateTimeOffset?)null
            : allReadings.Max(reading => reading.TimestampUtc);
        var cutoff = latestTimestamp?.AddMinutes(-minutes) ?? DateTimeOffset.MinValue;
        var readings = allReadings
            .GroupBy(reading => reading.SensorId)
            .Select(group => group.OrderByDescending(reading => reading.TimestampUtc).First())
            .OrderBy(reading => TypeRank(reading.SensorType))
            .ThenBy(reading => reading.Hardware, StringComparer.OrdinalIgnoreCase)
            .ThenBy(reading => reading.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var series = BuildSeries(
            allReadings.Where(reading => reading.TimestampUtc >= cutoff).ToList(),
            requestedTypes,
            requestedSensorIds);

        return new LogFileDataResponse(
            DateTimeOffset.UtcNow,
            fileName,
            path,
            minutes,
            latestTimestamp,
            readings.Count,
            allReadings.Count,
            readings,
            series);
    }

    private async Task WriteExportAsync(HttpListenerResponse response)
    {
        var files = Directory.GetFiles(state.LogDirectory, "telemetry-*.csv")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
        {
            await WriteTextAsync(response, "No telemetry logs have been written yet.", 404).ConfigureAwait(false);
            return;
        }

        var output = new StringBuilder();
        var wroteHeader = false;
        foreach (var file in files)
        {
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith("timestamp_local,", StringComparison.OrdinalIgnoreCase))
                {
                    if (wroteHeader) continue;
                    wroteHeader = true;
                }
                output.AppendLine(line);
            }
        }

        response.Headers["Content-Disposition"] =
            $"attachment; filename=pc-telemetry-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
        await WriteBytesAsync(
            response,
            Encoding.UTF8.GetBytes(output.ToString()),
            "text/csv; charset=utf-8").ConfigureAwait(false);
    }

    private static IReadOnlyList<SensorSeries> BuildSeries(
        IReadOnlyList<SensorReading> readings,
        HashSet<string> requestedTypes,
        HashSet<string> requestedSensorIds) => readings
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

    private static IReadOnlyList<SeriesPoint> Downsample(
        IReadOnlyList<SensorReading> readings,
        int maxPoints)
    {
        if (readings.Count <= maxPoints)
        {
            return readings
                .Select(reading => new SeriesPoint(reading.TimestampUtc, reading.Value))
                .ToList();
        }

        var bucketSize = (int)Math.Ceiling(readings.Count / (double)maxPoints);
        var points = new List<SeriesPoint>(maxPoints);
        for (var start = 0; start < readings.Count; start += bucketSize)
        {
            var end = Math.Min(start + bucketSize, readings.Count);
            var total = 0d;
            for (var index = start; index < end; index++)
            {
                total += readings[index].Value;
            }
            points.Add(new SeriesPoint(
                readings[end - 1].TimestampUtc,
                total / (end - start)));
        }
        return points;
    }

    private static IReadOnlyList<SensorReading> ReadCsvLog(string path)
    {
        var readings = new List<SensorReading>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)
                || line.StartsWith("timestamp_local,", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var columns = ParseCsvLine(line);
            if (columns.Count < 9
                || !DateTimeOffset.TryParse(
                    columns[1], CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var timestamp)
                || !double.TryParse(
                    columns[7], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            readings.Add(new SensorReading(
                timestamp,
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

    private static IReadOnlyList<string> ParseCsvLine(string line)
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

            if (character == '"') inQuotes = true;
            else if (character == ',')
            {
                values.Add(field.ToString());
                field.Clear();
            }
            else field.Append(character);
        }
        values.Add(field.ToString());
        return values;
    }

    private static int ParseMinutes(string? value) =>
        Math.Clamp(int.TryParse(value, out var parsed) ? parsed : 30, 1, 360);

    private static HashSet<string> ParseCsvSet(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static int TypeRank(string sensorType) => sensorType switch
    {
        "Temperature" => 0,
        "Control" => 1,
        "Fan" => 2,
        "Load" => 3,
        "Power" => 4,
        _ => 10
    };

    private static async Task WriteJsonAsync(HttpListenerResponse response, object value)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        await WriteBytesAsync(response, data, "application/json; charset=utf-8").ConfigureAwait(false);
    }

    private static Task WriteTextAsync(
        HttpListenerResponse response,
        string text,
        int statusCode) => WriteBytesAsync(
        response,
        Encoding.UTF8.GetBytes(text),
        "text/plain; charset=utf-8",
        statusCode);

    private static async Task WriteBytesAsync(
        HttpListenerResponse response,
        byte[] data,
        string contentType,
        int statusCode = 200)
    {
        response.StatusCode = statusCode;
        response.ContentType = contentType;
        response.ContentLength64 = data.Length;
        await response.OutputStream.WriteAsync(data).ConfigureAwait(false);
    }

    private static byte[] LoadIndexHtml()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("FanControl.PCTelemetryDashboard.index.html")
            ?? throw new InvalidOperationException("Embedded dashboard HTML was not found.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}

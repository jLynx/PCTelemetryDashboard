namespace FanControl.PCTelemetryDashboard;

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

public sealed record LogFileSummary(
    string FileName,
    string FilePath,
    long SizeBytes,
    DateTimeOffset LastWriteUtc,
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

public sealed record UsbDisplayValues(
    double? CpuTemperatureC,
    double? GpuTemperatureC,
    double? CpuLoadPercent,
    double? GpuLoadPercent,
    double? CpuPowerW,
    double? GpuPowerW,
    ushort? CpuFanRpm,
    ushort? GpuFanRpm,
    double? RadFanPercent,
    double? IoFanPercent,
    double? PcieFanPercent,
    double? ExhaustFanPercent);

public sealed record UsbDisplayStatus(
    bool IsConnected,
    string? ProductName,
    string? DevicePath,
    DateTimeOffset? LastReportUtc,
    ushort LastSequence,
    int ValidReadingCount,
    UsbDisplayValues? LastValues,
    string? LastError);

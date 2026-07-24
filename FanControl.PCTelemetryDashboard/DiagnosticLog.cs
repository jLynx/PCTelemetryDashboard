using System.Globalization;
using System.Text;

namespace FanControl.PCTelemetryDashboard;

internal static class DiagnosticLog
{
    private const long MaximumBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(
        Path.GetTempPath(),
        "PCTelemetryDashboard",
        "fancontrol-plugin.log");

    private static string PreviousFilePath => Path.Combine(
        Path.GetDirectoryName(FilePath)!,
        "fancontrol-plugin.previous.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                RotateIfRequired();

                var timestamp = DateTimeOffset.Now.ToString(
                    "yyyy-MM-dd HH:mm:ss.fff zzz",
                    CultureInfo.InvariantCulture);
                var line = $"{timestamp} [PID {Environment.ProcessId}, " +
                    $"T{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}";
                File.AppendAllText(FilePath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never interfere with fan control or telemetry.
        }
    }

    private static void RotateIfRequired()
    {
        var current = new FileInfo(FilePath);
        if (!current.Exists || current.Length < MaximumBytes)
        {
            return;
        }

        File.Move(FilePath, PreviousFilePath, true);
    }
}

namespace FanControl.PCTelemetryDashboard;

internal static class DashboardSensorFilter
{
    private static readonly string[] CpuNames =
    [
        "Core (Tctl/Tdie)", "CPU Package", "Package", "CPU Total",
        "CPU Core Max", "CCD1 (Tdie)", "CCD1", "CCD2 (Tdie)", "CCD2",
        "CCDs Max (Tdie)", "CCD Max", "CCDs Average (Tdie)", "CCD Average"
    ];

    private static readonly string[] GpuNames =
    [
        "GPU", "GPU Core", "GPU Memory Controller", "Memory Controller",
        "GPU Package", "GPU Power", "Total Board Power", "Board Power",
        "GPU Fan", "Control 1 - NVIDIA", "Control 2 - NVIDIA",
        "Control 3 - NVIDIA", "Fan 1 - NVIDIA", "Fan 2 - NVIDIA",
        "Fan 3 - NVIDIA"
    ];

    private static readonly string[] MotherboardNames =
    [
        "System #1", "System 1", "Temperature #1", "PCH", "CPU",
        "PCIe x16", "PCIEX16", "VRM MOS", "VRM", "PCIe x4", "PCIEX4",
        "System #2", "System 2", "Temperature #2", "System Fan #1",
        "System Fan #2", "System Fan #3", "CPU Optional Fan", "CPU OPT Fan",
        "CPU_OPT", "CPU Fan", "CPU_FAN", "System Fan #5 / Pump",
        "System Fan #5", "CPU Pump", "Pump"
    ];

    public static bool ShouldRetainHistory(SensorReading reading)
    {
        if (reading.Hardware.Contains("AMD Ryzen", StringComparison.OrdinalIgnoreCase))
        {
            return MatchesAny(reading.Name, CpuNames);
        }

        if (reading.Hardware.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
            || reading.Hardware.Contains("GeForce", StringComparison.OrdinalIgnoreCase))
        {
            return MatchesAny(reading.Name, GpuNames);
        }

        if (reading.Hardware.Contains("AMD Radeon", StringComparison.OrdinalIgnoreCase))
        {
            return reading.Name.Contains("GPU VR SoC", StringComparison.OrdinalIgnoreCase);
        }

        if (reading.Hardware.Contains("Gigabyte", StringComparison.OrdinalIgnoreCase))
        {
            return MatchesAny(reading.Name, MotherboardNames);
        }

        return false;
    }

    private static bool MatchesAny(string actual, IEnumerable<string> candidates) =>
        candidates.Any(candidate =>
            string.Equals(actual, candidate, StringComparison.OrdinalIgnoreCase)
            || actual.Contains(candidate, StringComparison.OrdinalIgnoreCase));
}

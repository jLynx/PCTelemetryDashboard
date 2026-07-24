using FanControl.IPC;

namespace FanControl.PCTelemetryDashboard;

internal sealed class FanControlSensorWorker(
    TelemetryState state,
    Action<string> log)
{
    private string? _lastError;
    private string? _gpuFallbackError;
    private readonly NvidiaNvmlReader _nvidia = new();
    private bool _gpuSourceLogged;
    private DateTimeOffset _nextNvidiaAttemptUtc;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var readings = ReadSnapshot(DateTimeOffset.UtcNow);
                state.AddSnapshot(readings);
                _lastError = null;
            }
            catch (Exception ex)
            {
                state.SetSensorError(ex.Message);
                if (!string.Equals(_lastError, ex.Message, StringComparison.Ordinal))
                {
                    _lastError = ex.Message;
                    log($"FanControl sensor read failed: {ex}");
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        _nvidia.Dispose();
    }

    private IReadOnlyList<SensorReading> ReadSnapshot(DateTimeOffset timestampUtc)
    {
        var client = IPCFactory.GetSensorClient();
        var allSensors = client.GetAllSensors(
            new GetAllSensorsRequest(),
            deadline: DateTime.UtcNow.AddSeconds(2));

        var ids = allSensors.Sensors
            .Where(sensor => ToLocalSensorType(sensor.Type) is not null)
            .Select(sensor => sensor.Identifier)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var values = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        if (ids.Count > 0)
        {
            try
            {
                var request = new ReadSensorValuesRequest();
                request.Ids.AddRange(ids);
                foreach (var item in client.ReadSensorValues(
                             request,
                             deadline: DateTime.UtcNow.AddSeconds(2)).Values)
                {
                    values[item.Key] = item.Value;
                }
            }
            catch
            {
                // Current FanControl builds may expose current values only from
                // GetAllSensors. Fall back to the value included on each sensor.
            }
        }

        var readings = new List<SensorReading>();
        foreach (var sensor in allSensors.Sensors)
        {
            var sensorType = ToLocalSensorType(sensor.Type);
            if (sensorType is null || string.IsNullOrWhiteSpace(sensor.Identifier))
            {
                continue;
            }

            var hasValue = values.TryGetValue(sensor.Identifier, out var value);
            if (!hasValue)
            {
                value = sensor.Value;
                hasValue = sensor.HasValue;
            }

            if (!hasValue || float.IsNaN(value) || float.IsInfinity(value))
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
                UnitFor(sensor.Type)));
        }

        var needsGpuCoreLoad = !readings.Any(reading =>
            IsNvidiaHardware(reading)
            && string.Equals(reading.SensorType, "Load", StringComparison.OrdinalIgnoreCase)
            && (reading.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)
                || string.Equals(reading.Name, "GPU", StringComparison.OrdinalIgnoreCase)));
        var needsGpuMemoryLoad = !readings.Any(reading =>
            IsNvidiaHardware(reading)
            && string.Equals(reading.SensorType, "Load", StringComparison.OrdinalIgnoreCase)
            && reading.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase));
        var needsGpuPower = !readings.Any(reading =>
            IsNvidiaHardware(reading)
            && string.Equals(reading.SensorType, "Power", StringComparison.OrdinalIgnoreCase));

        if ((needsGpuCoreLoad || needsGpuMemoryLoad || needsGpuPower)
            && timestampUtc >= _nextNvidiaAttemptUtc)
        {
            try
            {
                var metrics = _nvidia.Read();
                if (needsGpuCoreLoad)
                {
                    readings.Add(new SensorReading(
                        timestampUtc,
                        "nvml:/gpu-nvidia/0/load/0",
                        "NVIDIA GPU",
                        "GpuNvidia",
                        "GPU Core",
                        "Load",
                        Math.Round(metrics.LoadPercent, 3),
                        "%"));
                }

                if (needsGpuMemoryLoad)
                {
                    readings.Add(new SensorReading(
                        timestampUtc,
                        "nvml:/gpu-nvidia/0/load/1",
                        "NVIDIA GPU",
                        "GpuNvidia",
                        "GPU Memory Controller",
                        "Load",
                        Math.Round(metrics.MemoryLoadPercent, 3),
                        "%"));
                }

                if (needsGpuPower)
                {
                    readings.Add(new SensorReading(
                        timestampUtc,
                        "nvml:/gpu-nvidia/0/power/0",
                        "NVIDIA GPU",
                        "GpuNvidia",
                        "GPU Package",
                        "Power",
                        Math.Round(metrics.PowerWatts, 3),
                        "W"));
                }

                if (!_gpuSourceLogged)
                {
                    _gpuSourceLogged = true;
                    log("Using NVIDIA NVML for live GPU load and power fallback sensors.");
                }
                _gpuFallbackError = null;
                _nextNvidiaAttemptUtc = DateTimeOffset.MinValue;
            }
            catch (NvidiaNvmlException ex) when (ex.IsTransient)
            {
                // The NVIDIA driver temporarily rejects NVML calls during
                // sleep/resume. NvidiaNvmlReader has already discarded the
                // stale session; retry quietly once the driver has settled.
                _nextNvidiaAttemptUtc = timestampUtc.AddSeconds(5);
                _gpuFallbackError = null;
            }
            catch (Exception ex)
            {
                if (!string.Equals(_gpuFallbackError, ex.Message, StringComparison.Ordinal))
                {
                    _gpuFallbackError = ex.Message;
                    log($"NVIDIA NVML load/power fallback failed: {ex}");
                }
            }
        }

        return readings;
    }

    private static bool IsNvidiaHardware(SensorReading reading) =>
        reading.Hardware.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
        || reading.Hardware.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
        || reading.HardwareType.Contains("GpuNvidia", StringComparison.OrdinalIgnoreCase);

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

    private static string UnitFor(SensorMessageType type) => type switch
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

    private static bool IsInvalidTemperature(string sensorName, float value) =>
        value <= 0
        || value > 150
        || sensorName.Contains("Critical Temperature", StringComparison.OrdinalIgnoreCase)
        || sensorName.Contains("Warning Temperature", StringComparison.OrdinalIgnoreCase);
}

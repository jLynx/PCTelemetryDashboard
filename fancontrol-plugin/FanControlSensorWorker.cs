using FanControl.IPC;
using LibreHardwareMonitor.Hardware;

namespace FanControl.PCTelemetryDashboard;

internal sealed class FanControlSensorWorker(
    TelemetryState state,
    Action<string> log)
{
    private string? _lastError;
    private string? _gpuFallbackError;
    private Computer? _gpuComputer;

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
                    log($"FanControl sensor read failed: {ex.Message}");
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

        _gpuComputer?.Close();
        _gpuComputer = null;
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

        var needsGpuLoad = !readings.Any(reading =>
            IsNvidiaHardware(reading)
            && string.Equals(reading.SensorType, "Load", StringComparison.OrdinalIgnoreCase));
        var needsGpuPower = !readings.Any(reading =>
            IsNvidiaHardware(reading)
            && string.Equals(reading.SensorType, "Power", StringComparison.OrdinalIgnoreCase));

        if (needsGpuLoad || needsGpuPower)
        {
            try
            {
                readings.AddRange(ReadLocalNvidiaSnapshot(
                    timestampUtc, needsGpuLoad, needsGpuPower));
                _gpuFallbackError = null;
            }
            catch (Exception ex)
            {
                if (!string.Equals(_gpuFallbackError, ex.Message, StringComparison.Ordinal))
                {
                    _gpuFallbackError = ex.Message;
                    log($"NVIDIA load/power fallback failed: {ex.Message}");
                }
            }
        }

        return readings;
    }

    private IReadOnlyList<SensorReading> ReadLocalNvidiaSnapshot(
        DateTimeOffset timestampUtc,
        bool includeLoad,
        bool includePower)
    {
        EnsureGpuComputerOpen();
        var readings = new List<SensorReading>();
        foreach (var hardware in _gpuComputer!.Hardware)
        {
            ReadNvidiaHardware(
                hardware, timestampUtc, includeLoad, includePower, readings);
        }
        return readings;
    }

    private void EnsureGpuComputerOpen()
    {
        if (_gpuComputer is not null)
        {
            return;
        }

        _gpuComputer = new Computer
        {
            IsGpuEnabled = true
        };
        _gpuComputer.Open();
    }

    private static void ReadNvidiaHardware(
        IHardware hardware,
        DateTimeOffset timestampUtc,
        bool includeLoad,
        bool includePower,
        List<SensorReading> readings)
    {
        hardware.Update();
        var isNvidia = hardware.HardwareType == HardwareType.GpuNvidia
            || hardware.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
            || hardware.Name.Contains("GeForce", StringComparison.OrdinalIgnoreCase);

        if (isNvidia)
        {
            foreach (var sensor in hardware.Sensors)
            {
                var wanted = (includeLoad && sensor.SensorType == SensorType.Load)
                    || (includePower && sensor.SensorType == SensorType.Power);
                if (!wanted
                    || sensor.Value is null
                    || float.IsNaN(sensor.Value.Value)
                    || float.IsInfinity(sensor.Value.Value))
                {
                    continue;
                }

                readings.Add(new SensorReading(
                    timestampUtc,
                    $"local:{sensor.Identifier}",
                    hardware.Name,
                    hardware.HardwareType.ToString(),
                    sensor.Name,
                    sensor.SensorType.ToString(),
                    Math.Round(sensor.Value.Value, 3),
                    sensor.SensorType == SensorType.Load ? "%" : "W"));
            }
        }

        foreach (var subHardware in hardware.SubHardware)
        {
            ReadNvidiaHardware(
                subHardware, timestampUtc, includeLoad, includePower, readings);
        }
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

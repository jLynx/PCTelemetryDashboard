using System.Buffers.Binary;
using System.ComponentModel;
using HidSharp;

namespace FanControl.PCTelemetryDashboard;

internal sealed class UsbDisplayWorker(
    TelemetryState state,
    Action<string> log)
{
    private const int VendorId = 0x303A;
    private const int ProductId = 0x1001;
    private const byte UsbReportId = 6;
    private const byte ProtocolVersion = 2;
    private const int HostReportSize = 64;

    private const byte CpuTempValid = 1 << 0;
    private const byte GpuTempValid = 1 << 1;
    private const byte CpuLoadValid = 1 << 2;
    private const byte GpuLoadValid = 1 << 3;
    private const byte CpuPowerValid = 1 << 4;
    private const byte GpuPowerValid = 1 << 5;
    private const byte CpuFanValid = 1 << 6;
    private const byte GpuFanValid = 1 << 7;
    private const byte RadFanValid = 1 << 0;
    private const byte IoFanValid = 1 << 1;
    private const byte PcieFanValid = 1 << 2;
    private const byte ExhaustFanValid = 1 << 3;

    private ushort _sequence;
    private bool _wasConnected;
    private string? _lastError;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HidStream? stream = null;
            HidDevice? device = null;

            try
            {
                device = FindDisplay();
                if (device is null)
                {
                    SetDisconnected(null, null, "PC Telemetry Display is not connected.");
                    await DelayAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!device.TryOpen(out stream))
                {
                    throw new IOException("Windows found the display but could not open its HID interface.");
                }

                stream.WriteTimeout = 2000;
                var productName = SafeProductName(device);
                if (!_wasConnected)
                {
                    log($"Connected to USB display {productName}.");
                }
                _wasConnected = true;
                _lastError = null;

                while (!cancellationToken.IsCancellationRequested)
                {
                    var report = BuildReport(
                        state.GetLatest(), ++_sequence, out var validReadingCount);
                    stream.Write(report, 0, report.Length);
                    state.SetDisplayStatus(new UsbDisplayStatus(
                        true,
                        productName,
                        device.DevicePath,
                        DateTimeOffset.UtcNow,
                        _sequence,
                        validReadingCount,
                        DecodeValues(report),
                        null));
                    await DelayAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var expectedDisconnect = IsDeviceDisconnected(ex);
                var message = expectedDisconnect
                    ? "PC Telemetry Display is not connected."
                    : ex.Message;
                SetDisconnected(device, ex, message);
                await DelayAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                stream?.Dispose();
            }
        }
    }

    private void SetDisconnected(HidDevice? device, Exception? error, string message)
    {
        if (_wasConnected)
        {
            log("USB display disconnected; waiting for reconnection.");
        }
        else if (error is not null && !string.Equals(_lastError, error.Message, StringComparison.Ordinal))
        {
            log($"USB display error: {error}");
        }

        _wasConnected = false;
        _lastError = error?.Message;
        state.SetDisplayStatus(new UsbDisplayStatus(
            false,
            device is null ? null : SafeProductName(device),
            device?.DevicePath,
            null,
            _sequence,
            0,
            null,
            message));
    }

    private static async Task DelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    private static HidDevice? FindDisplay() => DeviceList.Local
        .GetHidDevices(VendorId, ProductId)
        .Where(device => device.GetMaxOutputReportLength() >= HostReportSize)
        .OrderByDescending(device =>
            SafeProductName(device).Contains("PC Telemetry Display", StringComparison.OrdinalIgnoreCase))
        .FirstOrDefault();

    private static string SafeProductName(HidDevice device)
    {
        try
        {
            return device.GetProductName() ?? "PC Telemetry Display";
        }
        catch
        {
            return "PC Telemetry Display";
        }
    }

    private static bool IsDeviceDisconnected(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is Win32Exception { NativeErrorCode: 1167 })
            {
                return true;
            }
        }

        return exception is IOException
            && exception.Message.Contains("device is not connected", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] BuildReport(
        IReadOnlyList<SensorReading> readings,
        ushort sequence,
        out int validReadingCount)
    {
        var report = new byte[HostReportSize];
        report[0] = UsbReportId;
        report[1] = ProtocolVersion;
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(2, 2), sequence);

        var latestTimestamp = readings.Count == 0
            ? DateTimeOffset.UtcNow
            : readings.Max(reading => reading.TimestampUtc);
        var sampleAgeMs = (uint)Math.Clamp(
            (DateTimeOffset.UtcNow - latestTimestamp).TotalMilliseconds,
            0,
            uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(4, 4), sampleAgeMs);

        byte flags = 0;
        validReadingCount = 0;
        WriteSignedX10(report, 9, FindCpuTemperature(readings),
            CpuTempValid, ref flags, ref validReadingCount);
        WriteSignedX10(report, 11, Find(readings, "Temperature", IsGpuHardware,
            "GPU Core", "GPU"), GpuTempValid, ref flags, ref validReadingCount);
        WriteUnsignedX10(report, 13, Find(readings, "Load", IsCpuHardware,
            "CPU Total", "Total CPU", "CPU Core Max"), CpuLoadValid, ref flags, ref validReadingCount);
        WriteUnsignedX10(report, 15, Find(readings, "Load", IsGpuHardware,
            "GPU Core", "GPU"), GpuLoadValid, ref flags, ref validReadingCount);
        WriteUnsignedX10(report, 17, Find(readings, "Power", IsCpuHardware,
            "CPU Package", "Package", "CPU Package Power"), CpuPowerValid, ref flags, ref validReadingCount);
        WriteUnsignedX10(report, 19, Find(readings, "Power", IsGpuHardware,
            "GPU Package", "GPU Power", "Total Board Power", "Board Power"), GpuPowerValid, ref flags, ref validReadingCount);
        WriteUnsigned(report, 21, Find(readings, "Fan", IsCpuHardware,
            "CPU Fan", "CPU"), CpuFanValid, ref flags, ref validReadingCount);
        WriteUnsigned(report, 23, Find(readings, "Fan", IsGpuHardware,
            "GPU Fan", "GPU"), GpuFanValid, ref flags, ref validReadingCount);
        report[8] = flags;

        byte fanFlags = 0;
        var radiatorControls = new[]
        {
            Find(readings, "Control", IsMotherboardHardware, "System Fan #2"),
            Find(readings, "Control", IsMotherboardHardware, "System Fan #3")
        }.Where(reading => reading is not null).Cast<SensorReading>().ToList();
        var radiatorControl = radiatorControls.Count == 0
            ? null
            : radiatorControls[0] with
            {
                Value = radiatorControls.Average(reading => reading.Value)
            };

        WriteUnsignedX10(report, 26, radiatorControl,
            RadFanValid, ref fanFlags, ref validReadingCount);
        WriteUnsignedX10(report, 28, Find(readings, "Control", IsMotherboardHardware,
            "CPU Optional Fan", "CPU OPT Fan", "CPU_OPT"),
            IoFanValid, ref fanFlags, ref validReadingCount);
        WriteUnsignedX10(report, 30, Find(readings, "Control", IsMotherboardHardware,
            "CPU Fan", "CPU_FAN"),
            PcieFanValid, ref fanFlags, ref validReadingCount);
        WriteUnsignedX10(report, 32, Find(readings, "Control", IsMotherboardHardware,
            "System Fan #1"),
            ExhaustFanValid, ref fanFlags, ref validReadingCount);
        report[25] = fanFlags;
        return report;
    }

    private static UsbDisplayValues DecodeValues(byte[] report)
    {
        var flags = report[8];
        double? SignedX10(int offset, byte flag) => (flags & flag) == 0
            ? null
            : BinaryPrimitives.ReadInt16LittleEndian(report.AsSpan(offset, 2)) / 10d;
        double? UnsignedX10(int offset, byte flag) => (flags & flag) == 0
            ? null
            : BinaryPrimitives.ReadUInt16LittleEndian(report.AsSpan(offset, 2)) / 10d;
        ushort? Unsigned(int offset, byte flag) => (flags & flag) == 0
            ? null
            : BinaryPrimitives.ReadUInt16LittleEndian(report.AsSpan(offset, 2));
        double? FanX10(int offset, byte flag) => (report[25] & flag) == 0
            ? null
            : BinaryPrimitives.ReadUInt16LittleEndian(report.AsSpan(offset, 2)) / 10d;

        return new UsbDisplayValues(
            SignedX10(9, CpuTempValid),
            SignedX10(11, GpuTempValid),
            UnsignedX10(13, CpuLoadValid),
            UnsignedX10(15, GpuLoadValid),
            UnsignedX10(17, CpuPowerValid),
            UnsignedX10(19, GpuPowerValid),
            Unsigned(21, CpuFanValid),
            Unsigned(23, GpuFanValid),
            FanX10(26, RadFanValid),
            FanX10(28, IoFanValid),
            FanX10(30, PcieFanValid),
            FanX10(32, ExhaustFanValid));
    }

    private static SensorReading? Find(
        IReadOnlyList<SensorReading> readings,
        string sensorType,
        Func<SensorReading, bool> hardwareMatch,
        params string[] preferredNames)
    {
        var candidates = readings
            .Where(reading => string.Equals(reading.SensorType, sensorType, StringComparison.OrdinalIgnoreCase))
            .Where(hardwareMatch)
            .ToList();

        foreach (var preferredName in preferredNames)
        {
            var exact = candidates.FirstOrDefault(reading =>
                string.Equals(reading.Name, preferredName, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }

        foreach (var preferredName in preferredNames)
        {
            var partial = candidates.FirstOrDefault(reading =>
                reading.Name.Contains(preferredName, StringComparison.OrdinalIgnoreCase));
            if (partial is not null)
            {
                return partial;
            }
        }

        return candidates.FirstOrDefault();
    }

    private static SensorReading? FindCpuTemperature(IReadOnlyList<SensorReading> readings) =>
        Find(readings, "Temperature", IsCpuHardware,
            "Core (Tctl/Tdie)", "CPU Package", "Package")
        ?? readings.FirstOrDefault(reading =>
            string.Equals(reading.SensorType, "Temperature", StringComparison.OrdinalIgnoreCase)
            && string.Equals(reading.Name, "CPU", StringComparison.OrdinalIgnoreCase)
            && !IsGpuHardware(reading));

    private static bool IsCpuHardware(SensorReading reading) =>
        reading.Hardware.Contains("AMD Ryzen", StringComparison.OrdinalIgnoreCase)
        || reading.Hardware.Contains("CPU", StringComparison.OrdinalIgnoreCase);

    private static bool IsGpuHardware(SensorReading reading) =>
        reading.Hardware.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
        || reading.Hardware.Contains("GeForce", StringComparison.OrdinalIgnoreCase);

    private static bool IsMotherboardHardware(SensorReading reading) =>
        reading.Hardware.Contains("Gigabyte", StringComparison.OrdinalIgnoreCase);

    private static void WriteSignedX10(
        byte[] report, int offset, SensorReading? reading, byte flag,
        ref byte flags, ref int validReadingCount)
    {
        if (reading is null) return;
        var scaled = (short)Math.Clamp(Math.Round(reading.Value * 10), short.MinValue, short.MaxValue);
        BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(offset, 2), scaled);
        flags |= flag;
        validReadingCount++;
    }

    private static void WriteUnsignedX10(
        byte[] report, int offset, SensorReading? reading, byte flag,
        ref byte flags, ref int validReadingCount)
    {
        if (reading is null) return;
        var scaled = (ushort)Math.Clamp(Math.Round(reading.Value * 10), 0, ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(offset, 2), scaled);
        flags |= flag;
        validReadingCount++;
    }

    private static void WriteUnsigned(
        byte[] report, int offset, SensorReading? reading, byte flag,
        ref byte flags, ref int validReadingCount)
    {
        if (reading is null) return;
        var scaled = (ushort)Math.Clamp(Math.Round(reading.Value), 0, ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(offset, 2), scaled);
        flags |= flag;
        validReadingCount++;
    }
}

using System.Runtime.InteropServices;

namespace FanControl.PCTelemetryDashboard;

internal sealed class NvidiaNvmlReader : IDisposable
{
    private const int Success = 0;
    private bool _initialized;
    private IntPtr _device;

    public NvidiaMetrics Read()
    {
        EnsureInitialized();

        try
        {
            ThrowIfFailed(
                NativeMethods.nvmlDeviceGetUtilizationRates(_device, out var utilization),
                "nvmlDeviceGetUtilizationRates");
            ThrowIfFailed(
                NativeMethods.nvmlDeviceGetPowerUsage(_device, out var powerMilliwatts),
                "nvmlDeviceGetPowerUsage");

            return new NvidiaMetrics(
                Math.Clamp(utilization.Gpu, 0u, 100u),
                Math.Clamp(utilization.Memory, 0u, 100u),
                powerMilliwatts / 1000d);
        }
        catch
        {
            // Driver handles become invalid when Windows suspends the GPU.
            // Tear down this NVML session so the next sample reinitializes it.
            Reset();
            throw;
        }
    }

    public void Dispose()
    {
        Reset();
    }

    private void Reset()
    {
        if (!_initialized)
        {
            return;
        }

        NativeMethods.nvmlShutdown();
        _initialized = false;
        _device = IntPtr.Zero;
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        ThrowIfFailed(NativeMethods.nvmlInit_v2(), "nvmlInit_v2");
        try
        {
            ThrowIfFailed(
                NativeMethods.nvmlDeviceGetHandleByIndex_v2(0, out _device),
                "nvmlDeviceGetHandleByIndex_v2");
            _initialized = true;
        }
        catch
        {
            NativeMethods.nvmlShutdown();
            throw;
        }
    }

    private static void ThrowIfFailed(int result, string operation)
    {
        if (result != Success)
        {
            throw new NvidiaNvmlException(operation, result);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    private static class NativeMethods
    {
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int nvmlInit_v2();

        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int nvmlShutdown();

        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int nvmlDeviceGetHandleByIndex_v2(
            uint index,
            out IntPtr device);

        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int nvmlDeviceGetUtilizationRates(
            IntPtr device,
            out NvmlUtilization utilization);

        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int nvmlDeviceGetPowerUsage(
            IntPtr device,
            out uint powerMilliwatts);
    }
}

internal readonly record struct NvidiaMetrics(
    double LoadPercent,
    double MemoryLoadPercent,
    double PowerWatts);

internal sealed class NvidiaNvmlException(string operation, int resultCode)
    : InvalidOperationException(
        $"NVIDIA NVML operation {operation} failed with result code {resultCode}.")
{
    public int ResultCode { get; } = resultCode;

    // These results occur while the NVIDIA driver/GPU is unavailable during
    // Windows sleep and resume. A new NVML session succeeds once it is ready.
    public bool IsTransient => ResultCode is 9 or 15 or 17 or 999;
}

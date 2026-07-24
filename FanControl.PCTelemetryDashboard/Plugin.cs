using FanControl.Plugins;

namespace FanControl.PCTelemetryDashboard;

public sealed class Plugin(IPluginLogger logger) : IPlugin2
{
    private const string DashboardUrl = "http://localhost:5127";
    private static readonly object ActivePluginGate = new();
    private static Plugin? ActivePlugin;
    private readonly object _lifecycleGate = new();
    private CancellationTokenSource? _cancellation;
    private Task[] _workers = [];
    private PublishedTemperatureSensor[] _publishedSensors = [];
    private DashboardServer? _server;

    public string Name => "PC Telemetry Dashboard";

    public void Initialize()
    {
        // FanControl may refresh plugins repeatedly. Any previous generation
        // must be completely stopped before a new Load call starts workers.
        Close();
    }

    public void Load(IPluginSensorsContainer container)
    {
        lock (ActivePluginGate)
        {
            if (!ReferenceEquals(ActivePlugin, this))
            {
                if (ActivePlugin is not null)
                {
                    Log("FanControl created a replacement plugin instance; stopping the previous worker generation.");
                    ActivePlugin.StopWorkers();
                }

                ActivePlugin = this;
            }

            StartWorkers(container);
        }
    }

    private void StartWorkers(IPluginSensorsContainer container)
    {
        lock (_lifecycleGate)
        {
            if (_cancellation is not null)
            {
                return;
            }

            try
            {
                var state = new TelemetryState();
                var cancellation = new CancellationTokenSource();
                var server = new DashboardServer(state, Log);
                var sensorWorker = new FanControlSensorWorker(state, Log);
                var usbWorker = new UsbDisplayWorker(state, Log);
                var publishedSensors = CreatePublishedSensors(state);

                foreach (var sensor in publishedSensors)
                {
                    container.TempSensors.Add(sensor);
                }

                _cancellation = cancellation;
                _server = server;
                _publishedSensors = publishedSensors;
                _workers =
                [
                    Task.Run(() => RunWorkerAsync(
                        "sensor", () => sensorWorker.RunAsync(cancellation.Token), cancellation.Token)),
                    Task.Run(() => RunWorkerAsync(
                        "USB display", () => usbWorker.RunAsync(cancellation.Token), cancellation.Token)),
                    Task.Run(() => RunWorkerAsync(
                        "dashboard server", () => server.RunAsync(cancellation.Token), cancellation.Token))
                ];

                Log($"PC Telemetry Dashboard plugin loaded. Dashboard: {DashboardUrl}");
                Log($"Diagnostic log: {DiagnosticLog.FilePath}");
            }
            catch (Exception ex)
            {
                _cancellation = null;
                _server = null;
                _workers = [];
                _publishedSensors = [];
                Log($"Plugin failed to load: {ex}");
            }
        }
    }

    public void Update()
    {
        PublishedTemperatureSensor[] sensors;
        lock (_lifecycleGate)
        {
            sensors = _publishedSensors;
        }

        foreach (var sensor in sensors)
        {
            sensor.Update();
        }
    }

    public void Close()
    {
        lock (ActivePluginGate)
        {
            if (ReferenceEquals(ActivePlugin, this))
            {
                ActivePlugin = null;
            }

            StopWorkers();
        }
    }

    private void StopWorkers()
    {
        CancellationTokenSource? cancellation;
        Task[] workers;
        DashboardServer? server;

        lock (_lifecycleGate)
        {
            cancellation = _cancellation;
            workers = _workers;
            server = _server;
            _cancellation = null;
            _workers = [];
            _publishedSensors = [];
            _server = null;
        }

        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        server?.Stop();

        try
        {
            Task.WaitAll(workers, TimeSpan.FromSeconds(3));
        }
        catch (AggregateException ex)
        {
            foreach (var error in ex.Flatten().InnerExceptions
                         .Where(error => error is not OperationCanceledException))
            {
                Log($"Worker stopped with an error: {error}");
            }
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void Log(string message)
    {
        DiagnosticLog.Write(message);

        try
        {
            logger.Log($"[PC Telemetry Dashboard] {message}");
        }
        catch
        {
            // Logging must never propagate into FanControl's plugin lifecycle.
        }
    }

    private async Task RunWorkerAsync(
        string workerName,
        Func<Task> worker,
        CancellationToken cancellationToken)
    {
        try
        {
            await worker().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal plugin shutdown.
        }
        catch (Exception ex)
        {
            Log($"The {workerName} worker stopped unexpectedly: {ex}");
        }
    }

    private static PublishedTemperatureSensor[] CreatePublishedSensors(
        TelemetryState state) =>
    [
        new(
            "pctelemetry-dashboard/cpu-temperature",
            "PC Telemetry CPU Temperature",
            () => FindTemperature(
                state.GetLatest(),
                reading => reading.Hardware.Contains("AMD Ryzen", StringComparison.OrdinalIgnoreCase),
                "Core (Tctl/Tdie)", "CPU Package", "Package")),
        new(
            "pctelemetry-dashboard/gpu-temperature",
            "PC Telemetry GPU Temperature",
            () => FindTemperature(
                state.GetLatest(),
                reading => reading.Hardware.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                    || reading.Hardware.Contains("GeForce", StringComparison.OrdinalIgnoreCase),
                "GPU Core", "GPU"))
    ];

    private static float? FindTemperature(
        IReadOnlyList<SensorReading> readings,
        Func<SensorReading, bool> hardwareMatch,
        params string[] preferredNames)
    {
        var candidates = readings
            .Where(reading => string.Equals(
                reading.SensorType, "Temperature", StringComparison.OrdinalIgnoreCase))
            .Where(hardwareMatch)
            .ToList();

        foreach (var name in preferredNames)
        {
            var reading = candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            if (reading is not null && reading.Value is > 0 and < 150)
            {
                return (float)reading.Value;
            }
        }

        return null;
    }
}

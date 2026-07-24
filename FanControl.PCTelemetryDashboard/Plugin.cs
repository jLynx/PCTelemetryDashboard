using FanControl.Plugins;

namespace FanControl.PCTelemetryDashboard;

public sealed class Plugin(IPluginLogger logger) : IPlugin2
{
    private const string DashboardUrl = "http://localhost:5127";
    private readonly object _lifecycleGate = new();
    private CancellationTokenSource? _cancellation;
    private Task[] _workers = [];
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

                _cancellation = cancellation;
                _server = server;
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
                Log($"Plugin failed to load: {ex}");
            }
        }
    }

    public void Update()
    {
        // FanControl calls this on its own 1 Hz update path. All USB, IPC and
        // HTTP work deliberately runs on background workers so this never blocks
        // fan control processing.
    }

    public void Close()
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
}

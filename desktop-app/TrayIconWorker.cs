using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

public sealed class TrayIconWorker(
    IHostApplicationLifetime lifetime,
    ILogger<TrayIconWorker> logger) : IHostedService
{
    public const string DashboardUrl = "http://localhost:5127";

    private readonly object _gate = new();
    private Thread? _trayThread;
    private TrayApplicationContext? _trayContext;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _trayThread = new Thread(RunTray)
        {
            IsBackground = true,
            Name = "PC Telemetry Dashboard tray"
        };
        _trayThread.SetApartmentState(ApartmentState.STA);
        _trayThread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        TrayApplicationContext? context;
        lock (_gate)
        {
            context = _trayContext;
        }

        context?.RequestExit();
        if (_trayThread is { IsAlive: true }
            && Thread.CurrentThread != _trayThread)
        {
            _trayThread.Join(TimeSpan.FromSeconds(2));
        }

        return Task.CompletedTask;
    }

    private void RunTray()
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using var context = new TrayApplicationContext(lifetime, logger);
            lock (_gate)
            {
                _trayContext = context;
            }

            Application.Run(context);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The system tray icon could not be started.");
        }
        finally
        {
            lock (_gate)
            {
                _trayContext = null;
            }
        }
    }

    private sealed class TrayApplicationContext : ApplicationContext
    {
        private readonly IHostApplicationLifetime _lifetime;
        private readonly ILogger _logger;
        private readonly Control _dispatcher;
        private readonly ContextMenuStrip _menu;
        private readonly NotifyIcon _notifyIcon;
        private bool _disposed;

        public TrayApplicationContext(
            IHostApplicationLifetime lifetime,
            ILogger logger)
        {
            _lifetime = lifetime;
            _logger = logger;
            _dispatcher = new Control();
            _dispatcher.CreateControl();

            _menu = new ContextMenuStrip();
            var openItem = _menu.Items.Add("Open dashboard");
            openItem.Click += (_, _) => OpenDashboard();
            var quitItem = _menu.Items.Add("Quit");
            quitItem.Click += (_, _) => _lifetime.StopApplication();

            _notifyIcon = new NotifyIcon
            {
                ContextMenuStrip = _menu,
                Icon = SystemIcons.Application,
                Text = "PC Telemetry Dashboard",
                Visible = true
            };
        }

        public void RequestExit()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _dispatcher.BeginInvoke(new Action(ExitThread));
            }
            catch (InvalidOperationException)
            {
                ExitThread();
            }
        }

        protected override void ExitThreadCore()
        {
            if (!_disposed)
            {
                _disposed = true;
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _menu.Dispose();
                _dispatcher.Dispose();
            }

            base.ExitThreadCore();
        }

        private void OpenDashboard()
        {
            try
            {
                Process.Start(new ProcessStartInfo(DashboardUrl)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The dashboard could not be opened in the browser.");
            }
        }
    }
}

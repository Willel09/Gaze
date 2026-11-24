using System;
using System.Linq;
using System.Threading;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Gaze;

public partial class App : Application
{
    Mutex? _mutex;
    EventWaitHandle? _showSignal;
    Forms.NotifyIcon? _tray;
    Forms.ToolStripMenuItem? _trackItem;
    MainWindow? _main;

    public AppController Controller { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "Gaze.SingleInstance.v1", out var first);
        if (!first)
        {
            try { EventWaitHandle.OpenExisting("Gaze.Show.v1").Set(); } catch { /* first instance is starting up */ }
            Shutdown();
            return;
        }

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += (_, ex) => { ex.Handled = true; MessageBox.Show(ex.Exception.Message, "Gaze", MessageBoxButton.OK, MessageBoxImage.Warning); };

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Gaze.Show.v1");
        ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => Dispatcher.BeginInvoke(ShowMain), null, Timeout.Infinite, false);

        Controller = new AppController();
        Controller.StatusChanged += SyncTray;
        Controller.Restart();
        BuildTray();

        // Launched by Windows at sign-in: stay in the tray, the eyes are already running.
        var quiet = e.Args.Contains("--background") && Controller.Settings.Applied;
        if (!quiet) ShowMain();
    }

    public void ShowMain()
    {
        _main ??= new MainWindow(Controller);
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    void BuildTray()
    {
        var uri = new Uri("pack://application:,,,/Assets/gaze.ico");
        using var s = GetResourceStream(uri).Stream;
        _trackItem = new Forms.ToolStripMenuItem("Eye tracking") { CheckOnClick = true };
        _trackItem.Click += (_, _) => Controller.SetTracking(_trackItem.Checked);

        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add("Open Gaze", null, (_, _) => ShowMain());
        menu.Items.Add(_trackItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        _tray = new Forms.NotifyIcon { Icon = new System.Drawing.Icon(s), Text = "Gaze", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowMain();
        SyncTray();
    }

    void SyncTray()
    {
        if (_tray == null || _trackItem == null) return;
        _trackItem.Enabled = Controller.Settings.Applied;
        _trackItem.Checked = Controller.Settings.Applied && Controller.Settings.TrackingEnabled;
        _tray.Text = Controller.Status switch
        {
            TrackingStatus.Watching => "Gaze: watching your cursor",
            TrackingStatus.Paused => "Gaze: paused, desktop is covered",
            TrackingStatus.Off => "Gaze: eye tracking off",
            _ => "Gaze",
        };
    }

    public void Quit()
    {
        _tray?.Dispose();
        Controller.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _showSignal?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { /* not owned */ }
        base.OnExit(e);
    }
}

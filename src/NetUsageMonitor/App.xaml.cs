using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using NetUsageMonitor.Common;
using NetUsageMonitor.Configuration;
using NetUsageMonitor.Engine;
using NetUsageMonitor.Ui;
using NetUsageMonitor.ViewModels;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace NetUsageMonitor;

public partial class App : Application
{
    internal const string DisplayName = "网络流量监控";
    private const string InstanceMutexName = @"Local\NetUsageMonitor.Instance";
    private const string ShowEventName = @"Local\NetUsageMonitor.ShowWindow";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;

    private AppSettings _settings = null!;
    private UsageTracker _tracker = null!;
    private IconProvider _icons = null!;
    private MainViewModel _viewModel = null!;
    private MainWindow? _mainWindow;
    private WinForms.NotifyIcon? _trayIcon;

    private bool _trayBalloonShown;
    private bool _faultReported;
    private volatile bool _isExiting;

    public bool IsExiting => _isExiting;
    public AppSettings Settings => _settings;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ---- Single instance: bring the existing window forward instead of starting twice ----
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { /* ignore */ }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        StartShowSignalListener();

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("发生意外错误：" + args.Exception.Message, DisplayName,
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // ---- Settings & records folder ----
        _settings = AppSettings.Load();
        TryEnsureRecordsFolder();
        SyncAutoStart();

        // ---- Engine ----
        _icons = new IconProvider();
        _tracker = new UsageTracker(_settings);
        _tracker.Faulted += OnTrackerFaulted;
        _tracker.CapTripped += OnCapTripped;
        _tracker.Start();

        // ---- UI ----
        _viewModel = new MainViewModel(_settings, _tracker, _icons, Dispatcher);
        _mainWindow = new MainWindow(_viewModel);

        CreateTrayIcon();

        bool startMinimized = e.Args.Contains("--minimized") || _settings.StartMinimized;
        if (!startMinimized)
            ShowMainWindow();
    }

    private void StartShowSignalListener()
    {
        var thread = new Thread(() =>
        {
            try
            {
                // ExitApp uses the same signal to wake the listener for shutdown.
                while (_showEvent!.WaitOne() && !IsExiting)
                    Dispatcher.BeginInvoke(ShowMainWindow);
            }
            catch { /* stop listening */ }
        })
        { IsBackground = true, Name = "ShowSignalListener" };
        thread.Start();
    }

    private void TryEnsureRecordsFolder()
    {
        try { Directory.CreateDirectory(_settings.RecordsFolder); }
        catch
        {
            _settings.RecordsFolder = AppPaths.DefaultRecordsFolder;
            Directory.CreateDirectory(_settings.RecordsFolder);
            _settings.Save();
        }
    }

    private void SyncAutoStart()
    {
        try
        {
            string exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName!;
            bool enabled = AutoStartManager.IsEnabled();
            if (_settings.StartWithWindows && !enabled) AutoStartManager.Enable(exe);
            else if (!_settings.StartWithWindows && enabled) AutoStartManager.Disable();
        }
        catch { /* best effort */ }
    }

    private void CreateTrayIcon()
    {
        _trayIcon = new WinForms.NotifyIcon
        {
            Text = DisplayName,
            Visible = true,
            Icon = LoadTrayIcon()
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add($"打开{DisplayName}", null, (_, _) => ShowMainWindow());
        menu.Items.Add("暂停或继续记录", null, (_, _) => _viewModel.ToggleRecordingCommand.Execute(null));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApp());
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    private static Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var stream = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;
            if (stream != null) return new Drawing.Icon(stream);
        }
        catch { /* ignore */ }
        return Drawing.SystemIcons.Application;
    }

    public void ShowMainWindow()
    {
        // A show request can already be queued when shutdown starts.
        if (IsExiting || _mainWindow is null) return;
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }

    /// <summary>Hides the window to the tray, keeping the engine running.</summary>
    public void HideToTray()
    {
        _mainWindow?.Hide();
        if (!_trayBalloonShown && _trayIcon != null)
        {
            _trayBalloonShown = true;
            _trayIcon.ShowBalloonTip(3000, "正在后台记录",
                "关闭窗口后仍会继续记录流量。如需退出，请右键单击托盘图标。",
                WinForms.ToolTipIcon.Info);
        }
    }

    public void ExitApp()
    {
        if (IsExiting) return;
        _isExiting = true;

        try { _showEvent?.Set(); } catch { /* unblock listener */ }

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        try { _tracker?.Dispose(); } catch { /* ignore */ }
        try { _settings?.Save(); } catch { /* ignore */ }

        Shutdown();
    }

    private void OnCapTripped(CapNotification n)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_settings.NotifyOnCap) return;
            _trayIcon?.ShowBalloonTip(6000, "已达流量限额",
                $"“{n.DisplayName}”已达到流量限额" +
                $"（{ByteFormatter.Bytes(n.UsedBytes)} / {ByteFormatter.Bytes(n.LimitBytes)}），已禁止联网。",
                WinForms.ToolTipIcon.Warning);
        });
    }

    private void OnTrackerFaulted(Exception ex)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try { _tracker.Stop(); } catch { /* ignore */ }
            if (_faultReported) return;
            _faultReported = true;
            MessageBox.Show(
                "无法启动流量采集。\n\n" + ex.Message +
                "\n\n读取各应用的网络流量需要管理员权限。请退出程序，再选择“以管理员身份运行”。",
                DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _trayIcon?.Dispose(); } catch { /* ignore */ }
        try { _instanceMutex?.ReleaseMutex(); } catch { /* ignore */ }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}

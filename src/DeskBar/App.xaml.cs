using System.Windows;
using System.Windows.Controls;
using DeskBar.Services;
using DeskBar.Views;

namespace DeskBar;

public partial class App : System.Windows.Application
{
    private static Mutex? _singleInstance;
    private static bool _ownsMutex;
    private static EventWaitHandle? _wakeEvent;
    private static System.Threading.Timer? _memoryGuardTimer;
    private DoubleClickToggle? _doubleClick;
    private ThemeService? _theme;
    private TrayIconHost? _tray;
    private SettingsWindow? _settingsWindow;
    private MediaSessionService? _media;
    private MusicCapsuleWindow? _capsule;
    private SystemMonitorService? _monitor;
    private SystemMonitorCapsuleWindow? _monitorWindow;
    private Views.IslandBorderWindow? _islandBorderMiddle;
    private Views.IslandBorderWindow? _islandBorderTray;
    private System.Windows.Threading.DispatcherTimer? _ownerGuardTimer;
    private static readonly uint WM_TASKBARCREATED = Interop.NativeMethods.RegisterWindowMessage("TaskbarCreated");
    private AppSettings _settings = new();

    internal static AppSettings Settings { get; private set; } = new();
    internal static ThemeService Theme { get; private set; } = new();
    internal static DesktopIconService Icons { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, @"Local\DeskBar_SingleInstance", out var isNew);
        if (!isNew)
        {
            // 唤醒已有实例的设置窗口（对应用户需求：隐藏托盘图标后再次点击启动图标可唤出窗口）
            try
            {
                using var ev = EventWaitHandle.OpenExisting(@"Local\DeskBar_WakeEvent");
                ev.Set();
            }
            catch { }
            // 副实例立即退出，绝不执行 OnExit 中的清理逻辑（否则会破坏主实例的任务栏与托盘状态）
            Environment.Exit(0);
            return;
        }

        _ownsMutex = true;
        base.OnStartup(e);

        // 创建全局唤醒事件监听线程
        try
        {
            _wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DeskBar_WakeEvent");
            var wakeThread = new Thread(() =>
            {
                while (_wakeEvent.WaitOne())
                {
                    Dispatcher.Invoke(ShowSettingsWindow);
                }
            })
            {
                IsBackground = true,
                Name = "DeskBar_WakeWatcher"
            };
            wakeThread.Start();
        }
        catch (Exception ex)
        {
            Services.Log.Warn($"wake watcher init failed: {ex.Message}");
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Services.Log.Error($"DispatcherUnhandledException: {args.Exception}");
            System.Windows.MessageBox.Show(args.Exception.ToString(), "DeskBar 错误");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Services.Log.Error($"AppDomain.UnhandledException: {args.ExceptionObject}");
        };

        var logDir = System.IO.Path.Combine(AppContext.BaseDirectory, "logs");
        Services.Log.Init(logDir);
        _settings = AppSettings.Load();
        Settings = _settings;
        Services.Log.IsDebugEnabled = _settings.EnableDebugLog;
        Services.Log.Info($"DeskBar starting, OS build={Environment.OSVersion.Version.Build}");
        _theme = new ThemeService();
        Theme = _theme;

        // 仅读取配置，暂不立即触发切换检查
        if (TimeSpan.TryParse(_settings.LightStartTime, out var lt)) _theme.LightStart = lt;
        if (TimeSpan.TryParse(_settings.DarkStartTime, out var dt)) _theme.DarkStart = dt;
        _theme.AutoTimeEnabled = _settings.AutoTimeTheme;

        // 启动定时器：延迟 6 秒后再做第一次时间段校验，确保不占用开机黄金启动期
        _theme.StartScheduleTimer(6000);

        var light = _theme.GetSystemUsesLightTheme();
        ApplyAppTheme(light);
        _theme.ThemeChanged += l => Dispatcher.Invoke(() =>
        {
            ApplyAppTheme(l);
            if (_settings.TranslucentTaskbar)
            {
                TaskbarService.ApplyStyle(_settings.TaskbarStyle, _settings.AcrylicTone);
            }
        });
        _theme.StartWatching();

        Services.Log.Info($"Startup check TranslucentTaskbar={_settings.TranslucentTaskbar}, style={_settings.TaskbarStyle}, tone={_settings.AcrylicTone}");
        if (_settings.TranslucentTaskbar)
        {
            TaskbarService.ApplyStyle(_settings.TaskbarStyle, _settings.AcrylicTone);
        }

        _doubleClick = new DoubleClickToggle(Icons) { Enabled = _settings.DoubleClickToggle };
        _doubleClick.Install();

        try
        {
            _tray = new TrayIconHost(this);
            _tray.Create(_settings.ShowTrayIcon);
        }
        catch (Exception ex)
        {
            Services.Log.Error($"tray create failed: {ex}");
        }

        // 初始化创建并启动四大浮岛组件
        CreateWidgetWindows();
        StartOwnerGuard();

        // 同步并确保极速自启优化配置生效（消除 Windows 启动等待、补齐启动快捷方式）
        if (_settings.RunAtStartup)
        {
            StartupService.SetEnabled(true);
        }
        else if (StartupService.IsEnabled())
        {
            StartupService.SetEnabled(false);
        }

        // 启动完成后触发一次轻量工作集整理，并在后台每 2 分钟微量维护一次
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000);
            TrimMemory();
            _memoryGuardTimer = new System.Threading.Timer(_ => TrimMemory(), null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
        });

        Services.Log.Info("DeskBar started");
    }

    [System.Runtime.InteropServices.DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    internal static void TrimMemory()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle);
        }
        catch { }
    }

    internal static void ApplyAppTheme(bool light)
    {
        var res = Current.Resources;
        res["WindowBackground"] = light
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF3, 0xF3, 0xF3))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20));
        res["CardBackground"] = light
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2B, 0x2B, 0x2B));
        res["ForegroundBrush"] = light
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1A, 0x1A))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0, 0xF0, 0xF0));
        res["SubTextBrush"] = light
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x66, 0x66, 0x66))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA8, 0xA8, 0xA8));

        // 侧边栏与自绘控件底色：不跟随的话，浅色界面会出现一条深色侧栏
        res["SidebarBrush"] = light
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE9, 0xE9, 0xE9))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x17, 0x17, 0x17));
        res["ControlBgBrush"] = light
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x30, 0x30, 0x30));
        res["ControlBorderBrush"] = light
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD2, 0xD2, 0xD2))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3E, 0x3E, 0x3E));
        res["ControlHoverBrush"] = light
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0xF2, 0xF2))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x3A, 0x3A));
        res["NavHoverBrush"] = light
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xE0, 0xE0))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x22, 0x22, 0x22));
        res["NavActiveBrush"] = light
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD6, 0xD6, 0xD6))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0x2E, 0x2E));
        res["TrackOffBrush"] = light
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC4, 0xC4, 0xC4))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4A, 0x4A, 0x4A));
        res["ToggleKnobBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF));

        Services.Log.Info($"app theme applied, light={light}");

        // 已打开的设置窗口要立刻跟上（尤其是标题栏颜色）
        try { (Current as App)?._settingsWindow?.RefreshThemeVisuals(); } catch { }
    }

    internal DoubleClickToggle? DoubleClick => _doubleClick;
    internal TrayIconHost? Tray => _tray;

    /// <summary>按当前设置启停音乐胶囊会话监听（关闭时彻底释放 GSMTC 句柄）。</summary>
    private void SyncMusicCapsule()
    {
        try
        {
            if (_settings.MusicCapsuleEnabled)
            {
                if (_media is null)
                {
                    _media = new MediaSessionService(_settings);
                    _capsule?.AttachService(_media);
                    _ = _media.InitializeAsync();
                    Services.Log.Info("music capsule enabled, GSMTC listening started");
                }
                else
                {
                    _ = _media.ReevaluateAsync();
                }
            }
            else if (_media is not null)
            {
                _capsule?.AttachService(null);
                _media.Dispose();
                _media = null;
                Services.Log.Info("music capsule disabled, GSMTC listening stopped");
            }

            _capsule?.RefreshFromSettings();
        }
        catch (Exception ex)
        {
            Services.Log.Error($"SyncMusicCapsule failed: {ex.Message}");
        }
    }

    /// <summary>按当前设置启停任务栏左侧硬件监控浮岛（关闭时停掉采样定时器）。</summary>
    /// <summary>任务栏风格 / 亚克力色调改变后，让两个外挂浮岛的背景与任务栏保持一致。</summary>
    internal void RefreshWidgetBackgrounds()
    {
        try { _monitorWindow?.RefreshBackground(); }
        catch (Exception ex) { Services.Log.Debug($"monitor refresh background failed: {ex.Message}"); }
        try { _capsule?.RefreshBackground(); }
        catch (Exception ex) { Services.Log.Debug($"capsule refresh background failed: {ex.Message}"); }
    }

    private void SyncSystemMonitor()
    {
        try
        {
            if (_settings.SystemMonitorEnabled)
            {
                if (_monitor is null)
                {
                    _monitor = new SystemMonitorService(1000);
                    _monitorWindow?.AttachService(_monitor);
                    _monitor.Start();
                    Services.Log.Info("system monitor enabled, sampling started");
                }
            }
            else if (_monitor is not null)
            {
                _monitorWindow?.AttachService(null);
                _monitor.Dispose();
                _monitor = null;
                Services.Log.Info("system monitor disabled, sampling stopped");
            }

            _monitorWindow?.RefreshFromSettings();
        }
        catch (Exception ex)
        {
            Services.Log.Error($"SyncSystemMonitor failed: {ex.Message}");
        }
    }

    internal void SaveSettings()
    {
        _settings.Save();
        if (_doubleClick is not null) _doubleClick.Enabled = _settings.DoubleClickToggle;
        _tray?.SetVisible(_settings.ShowTrayIcon);
        StartupService.SetEnabled(_settings.RunAtStartup);

        if (_theme is not null)
        {
            if (TimeSpan.TryParse(_settings.LightStartTime, out var lt)) _theme.LightStart = lt;
            if (TimeSpan.TryParse(_settings.DarkStartTime, out var dt)) _theme.DarkStart = dt;
            _theme.AutoTimeEnabled = _settings.AutoTimeTheme;
        }

        if (_settings.TranslucentTaskbar)
            TaskbarService.ApplyStyle(_settings.TaskbarStyle, _settings.AcrylicTone);
        else
            TaskbarService.StopIslandWatch();

        SyncMusicCapsule();
        SyncSystemMonitor();
    }


    private void CreateWidgetWindows()
    {
        try
        {
            _capsule = new MusicCapsuleWindow(_settings);
            SyncMusicCapsule();
        }
        catch (Exception ex)
        {
            Services.Log.Error($"music capsule init failed: {ex}");
        }

        try
        {
            _monitorWindow = new SystemMonitorCapsuleWindow(_settings);
            SyncSystemMonitor();
        }
        catch (Exception ex)
        {
            Services.Log.Error($"system monitor init failed: {ex}");
        }

        try
        {
            _islandBorderMiddle = new Views.IslandBorderWindow(Views.IslandBorderWindow.Side.Middle);
            _islandBorderTray = new Views.IslandBorderWindow(Views.IslandBorderWindow.Side.Tray);
        }
        catch (Exception ex)
        {
            Services.Log.Error($"island border init failed: {ex}");
        }
    }

    /// <summary>
    /// 当 explorer 崩溃、重启或任务栏重建（TaskbarCreated）导致旧 owned window 被销毁时，
    /// 整体安全重建四大浮岛，实现无感自愈与自活。
    /// </summary>
    internal void RecreateWidgetWindows()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RecreateWidgetWindows));
            return;
        }

        Services.Log.Info("recreating widget windows (taskbar recreated or explorer restarted)...");

        if (_capsule != null)
        {
            try { _capsule.AttachService(null); } catch { }
            try { _capsule.Close(); } catch { }
            _capsule = null;
        }
        if (_monitorWindow != null)
        {
            try { _monitorWindow.AttachService(null); } catch { }
            try { _monitorWindow.Close(); } catch { }
            _monitorWindow = null;
        }
        if (_islandBorderMiddle != null)
        {
            try { _islandBorderMiddle.Close(); } catch { }
            _islandBorderMiddle = null;
        }
        if (_islandBorderTray != null)
        {
            try { _islandBorderTray.Close(); } catch { }
            _islandBorderTray = null;
        }

        CreateWidgetWindows();
        Services.Log.Info("widget windows recreated successfully");
    }

    private DateTime _lastRecreateTime = DateTime.MinValue;

    private void StartOwnerGuard()
    {
        // 2秒巡检一次：如果任务栏存在，但任意浮岛被销毁或未绑定，自动触发自愈补绑或重建
        _ownerGuardTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _ownerGuardTimer.Tick += (_, _) =>
        {
            try
            {
                var tray = Interop.NativeMethods.FindWindowW("Shell_TrayWnd", null);
                if (tray == IntPtr.Zero) return;

                // 只有当窗口曾经具有句柄（h != 0），但操作系统报告该窗口句柄已失效（!IsWindow(h)）时，
                // 才是真正被操作系统内核（随 explorer 重启）同步销毁，才需要自愈重建！
                // 未 Show 或 Handle 尚未初始化的窗口绝不是 dead！
                bool dead = false;
                if (_capsule != null)
                {
                    var h = new System.Windows.Interop.WindowInteropHelper(_capsule).Handle;
                    if (h != IntPtr.Zero && !Interop.NativeMethods.IsWindow(h)) dead = true;
                }
                if (_monitorWindow != null)
                {
                    var h = new System.Windows.Interop.WindowInteropHelper(_monitorWindow).Handle;
                    if (h != IntPtr.Zero && !Interop.NativeMethods.IsWindow(h)) dead = true;
                }
                if (_islandBorderMiddle != null)
                {
                    var h = new System.Windows.Interop.WindowInteropHelper(_islandBorderMiddle).Handle;
                    if (h != IntPtr.Zero && !Interop.NativeMethods.IsWindow(h)) dead = true;
                }
                if (_islandBorderTray != null)
                {
                    var h = new System.Windows.Interop.WindowInteropHelper(_islandBorderTray).Handle;
                    if (h != IntPtr.Zero && !Interop.NativeMethods.IsWindow(h)) dead = true;
                }

                if (dead)
                {
                    // 节流保护：两次全量重建之间至少间隔 5 秒，绝不允许无限死循环重构！
                    if ((DateTime.UtcNow - _lastRecreateTime).TotalSeconds >= 5)
                    {
                        _lastRecreateTime = DateTime.UtcNow;
                        RecreateWidgetWindows();
                    }
                    return;
                }

                // 巡检补齐 owner 绑定
                if (_capsule != null) AcrylicWindowHelper.EnsureBoundToTaskbar(_capsule);
                if (_monitorWindow != null) AcrylicWindowHelper.EnsureBoundToTaskbar(_monitorWindow);
                if (_islandBorderMiddle != null) AcrylicWindowHelper.EnsureBoundToTaskbar(_islandBorderMiddle);
                if (_islandBorderTray != null) AcrylicWindowHelper.EnsureBoundToTaskbar(_islandBorderTray);
            }
            catch { }
        };
        _ownerGuardTimer.Start();
    }

    internal void ShowSettingsWindow()
    {
        if (_settingsWindow is null || !_settingsWindow.IsLoaded)
            _settingsWindow = new SettingsWindow();

        _settingsWindow.Show();
        _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
        Services.Log.Info("settings window shown");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Services.Log.Info("DeskBar exiting");
        if (_ownsMutex)
        {
            try { _media?.Dispose(); } catch { }
            try { _ownerGuardTimer?.Stop(); } catch { }
            try { _capsule?.Close(); } catch { }
            try { _monitor?.Dispose(); } catch { }
            try { _monitorWindow?.Close(); } catch { }
            try { _islandBorderMiddle?.Close(); } catch { }
            try { _islandBorderTray?.Close(); } catch { }
            try { Services.TaskbarService.CleanUp(); } catch { }
            try { _doubleClick?.Dispose(); } catch { }
            try { _theme?.Dispose(); } catch { }
            try { _tray?.Dispose(); } catch { }
            try { _wakeEvent?.Dispose(); } catch { }
            try
            {
                _singleInstance?.ReleaseMutex();
            }
            catch (Exception ex)
            {
                Services.Log.Warn($"ReleaseMutex failed: {ex.Message}");
            }
        }
        base.OnExit(e);
    }
}

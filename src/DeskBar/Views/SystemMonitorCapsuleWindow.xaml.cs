using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DeskBar.Interop;
using DeskBar.Services;

namespace DeskBar.Views;

/// <summary>任务栏最左端的硬件监控浮岛：CPU / 内存 / 网络上下行 / GPU / 磁盘。</summary>
public partial class SystemMonitorCapsuleWindow : Window
{
    /// <summary>与任务栏左边缘的间距（物理像素）。</summary>
    private const int GapToTaskbarEdgePx = 10;

    /// <summary>进度条槽宽（DIP）；填充条只占其中百分比那一部分。</summary>
    private const double BarTrackWidth = 26.0;

    private readonly AppSettings _settings;
    private SystemMonitorService? _service;
    private DispatcherTimer? _posTimer;
    private bool _shown;
    // 最近一次真正下发过的窗口位置；相同就不再调用 SetWindowPos。
    private int _lastX = int.MinValue, _lastY;

    // 任务栏「不可用」的去抖：拖拽托盘图标时 explorer 会短暂重排任务栏，
    // GetWindowRect 可能瞬时返回零矩形，单次误判就会让浮岛整块消失 140ms。
    // 用「连续多久判定失败」而不是「多少轮」，因为各窗口的定时器间隔不同。
    private DateTime _taskbarGoneSince = DateTime.MinValue;
    private const int TaskbarGoneMsNeeded = 900;

    // 16ms 轻量 z 序守卫：整条模式下浮岛不再挖洞，靠它保持压在任务栏之上。
    private DispatcherTimer? _zGuard;
    private bool _isDisposed;
    private SystemMonitorDetailWindow? _detailWindow;
    private SystemMetrics? _latestMetrics;

    internal SystemMonitorCapsuleWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        Title = "DeskBarSystemMonitor";
        SourceInitialized += OnSourceInitialized;
        RootBorder.MouseLeftButtonUp += (_, _) => ToggleDetailWindow();
        Closed += (_, _) =>
        {
            _isDisposed = true;
            StopPositionTimer();
            StopZGuard();
            try { _detailWindow?.Close(); } catch { }
            _detailWindow = null;
            if (_service != null)
            {
                _service.MetricsChanged -= OnMetricsChanged;
                _service = null;
            }
        };
        RootBorder.Opacity = 0;
        StartZGuard();
    }

    // ---------------- 外部接口 ----------------

    internal void AttachService(SystemMonitorService? service)
    {
        if (_service != null) _service.MetricsChanged -= OnMetricsChanged;
        _service = service;
        if (_service != null)
        {
            _service.MetricsChanged += OnMetricsChanged;
        }
        else
        {
            HidePanel();
        }
    }

    internal void RefreshFromSettings()
    {
        if (_settings.SystemMonitorEnabled)
        {
            StartPositionTimer();
        }
        else
        {
            StopPositionTimer();
            HidePanel();
        }
    }

    /// <summary>任务栏风格 / 亚克力色调变化后重新套用浮岛背景。</summary>
    internal void RefreshBackground()
    {
        ApplyToneBorder();
        // 只有浮岛模式（部分亚克力）里浮岛悬在壁纸上方，才需要自带磨砂；
        // 整条模式下它直接盖在任务栏自己的底上，保证与音乐胶囊、任务栏材质一致。
        AcrylicWindowHelper.Apply(this, _settings.EffectiveTone, "monitor", !TaskbarService.IsIslandActive, _settings.TaskbarBlurAmount);
    }

    /// <summary>描边色跟随 tone：浅色柔雾压暗边、深色黑丝提亮边。</summary>
    private void ApplyToneBorder()
    {
        try
        {
            Resources["CapsuleBorderBrush"] = TonePalette.BorderBrush(_settings.EffectiveTone);
        }
        catch (Exception ex)
        {
            Services.Log.Debug($"monitor capsule border apply failed: {ex.Message}");
        }
    }

    // ---------------- 数据刷新 ----------------

    private void OnMetricsChanged(SystemMetrics m)
    {
        if (_isDisposed) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isDisposed) return;
            try
            {
                TxtCpu.Text = $"{m.CpuPercent:0}%";
                SetBar(BarCpu, m.CpuPercent);

                TxtMem.Text = $"{m.MemPercent:0}%";
                SetBar(BarMem, m.MemPercent);

                TxtNetUp.Text = FormatRate(m.NetUpKBps);
                TxtNetDown.Text = FormatRate(m.NetDownKBps);

                if (m.GpuPercent >= 0)
                {
                    ItemGpu.Visibility = Visibility.Visible;
                    TxtGpu.Text = $"{m.GpuPercent:0}%";
                    SetBar(BarGpu, m.GpuPercent);
                }
                else
                {
                    ItemGpu.Visibility = Visibility.Collapsed;
                }

                // 这里不能直接 ShowPanel()：数值每秒刷新一次，直接显示会绕过
                // UpdateVisibility() 的让位判定，任务栏被隐藏时浮岛仍被强行拉回来。
                UpdateVisibility();

                _latestMetrics = m;
                if (_detailWindow != null && _detailWindow.IsVisible)
                {
                    _detailWindow.UpdateMetrics(m);
                }
            }
            catch (Exception ex)
            {
                Services.Log.Debug($"monitor ui update failed: {ex.Message}");
            }
        }));
    }

    /// <summary>网络速率：KB/s 起步，按 KB/s / MB/s / GB/s 三档，宽度不随数值跳变。</summary>
    private static string FormatRate(double kbps)
    {
        if (kbps < 0) kbps = 0;
        if (kbps < 1000) return $"{kbps:0.00}KB/s";
        if (kbps < 1048576) return $"{kbps / 1024:0.00}MB/s";
        return $"{kbps / 1048576:0.00}GB/s";
    }

    /// <summary>按百分比设置进度条填充宽度。槽宽固定，所以整块浮岛的宽度不会抖动。</summary>
    private static void SetBar(FrameworkElement bar, double percent)
    {
        if (percent < 0) percent = 0;
        if (percent > 100) percent = 100;
        bar.Width = BarTrackWidth * percent / 100.0;
    }

    // ---------------- 显示 / 隐藏 ----------------

    private void ShowPanel()
    {
        if (!_settings.SystemMonitorEnabled) return;
        if (_shown) return;

        if (_isDisposed) return;
        _shown = true;
        _lastX = int.MinValue;   // 重新显示时强制做一次定位
        try
        {
            if (!IsVisible && !_isDisposed) Show();
        }
        catch (InvalidOperationException)
        {
            _isDisposed = true;
            return;
        }

        var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        RootBorder.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void HidePanel()
    {
        try { _detailWindow?.CloseWithAnimation(); } catch { }
        TaskbarService.ClearWidgetExclusion("monitor");
        _shown = false;
        if (!IsVisible)
        {
            RootBorder.BeginAnimation(UIElement.OpacityProperty, null);
            RootBorder.Opacity = 0;
            return;
        }

        var anim = new DoubleAnimation(RootBorder.Opacity, 0, TimeSpan.FromMilliseconds(140));
        anim.Completed += (_, _) =>
        {
            if (_shown) return;
            Hide();
            RootBorder.BeginAnimation(UIElement.OpacityProperty, null);
            RootBorder.Opacity = 0;
        };
        RootBorder.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void StartPositionTimer()
    {
        if (_posTimer != null) return;
        _posTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _posTimer.Tick += (_, _) =>
        {
            UpdateVisibility();
        };
        _posTimer.Start();
    }

    private void StopPositionTimer()
    {
        try { _posTimer?.Stop(); } catch { }
        _posTimer = null;
    }

    private void StartZGuard()
    {
        if (_zGuard != null) return;
        _zGuard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _zGuard.Tick += (_, _) =>
        {
            if (_shown) AcrylicWindowHelper.KeepAboveTaskbar(this);
        };
        _zGuard.Start();
    }

    private void StopZGuard()
    {
        try { _zGuard?.Stop(); } catch { }
        _zGuard = null;
    }

    /// <summary>
    /// 统一显示状态机：任务栏不可用或持续全屏时让位，否则确保显示并归位。
    /// 让位后必须由这里重新显示，否则一旦隐藏就再也回不来。
    /// </summary>
    private void UpdateVisibility()
    {
        if (!_settings.SystemMonitorEnabled)
        {
            if (_shown) HidePanel();
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            RootBorder.Opacity = 0;
            if (!IsVisible) Show();
            hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
        }

        if (ShouldHideForTaskbar())
        {
            if (_shown) HidePanel();
            return;
        }

        RepositionWindow();
        if (!_shown) ShowPanel();
    }

    /// <summary>把浮岛停靠到任务栏最左端，纵向与任务栏居中对齐。</summary>
    private void RepositionWindow()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        // 让位统一由 UpdateVisibility 判定，这里只兜住任务栏本身消失的情况
        if (ShouldHideForTaskbar())
        {
            if (_shown) HidePanel();
            return;
        }

        if (!TaskbarService.TryGetTaskbarBounds(out int tbLeft, out int tbTop, out int tbWidth, out int tbHeight) || tbWidth <= 0)
            return;

        int scale = GetDpiForWindow(hwnd);
        double dpi = scale <= 0 ? 1.0 : scale / 96.0;

        int h = (int)Math.Round(ActualHeight * dpi);
        if (h <= 0) h = (int)Math.Round(48 * dpi);
        if (tbHeight > 0 && h > tbHeight) h = tbHeight;

        int x = tbLeft + GapToTaskbarEdgePx;
        int y = tbTop + (tbHeight - h) / 2;

        // 位置没变就不再下发，避免每 400ms 反复重绘导致的闪烁。
        if (x == _lastX && y == _lastY) return;
        _lastX = x;
        _lastY = y;

        // 与音乐胶囊一致：移动只动位置，绝不动 z 序（TaskbarLayerAnchor 当 hWndInsertAfter
        // 可能返回本窗口自己或已销毁的窗口，导致 SetWindowPos 整体失败、位置不生效）。
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
            SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOSIZE);

        // 整条模式下浮岛直接盖在任务栏自己的底上（不再单独磨砂），因此也不能再挖洞：
        // 洞里露出来的是未经模糊的原始壁纸，正是连接处「漏到背景」难看的原因。
        TaskbarService.ClearWidgetExclusion("monitor");
    }

    /// <summary>任务栏本身不可用：不存在、不可见、或被自动隐藏推离了屏幕。</summary>
    /// <summary>
    /// 「任务栏真的没了」才返回 true：必须连续 TaskbarGoneTicksNeeded 轮判定失败。
    /// 单轮的瞬时误判（拖拽托盘图标时 explorer 重排任务栏、GetWindowRect 拿到零矩形）
    /// 会被忽略，否则浮岛会整块消失再淡入 —— 用户录屏里那 150ms 就是这个。
    /// </summary>
    private bool ShouldHideForTaskbar()
    {
        if (!IsTaskbarUnavailable())
        {
            _taskbarGoneSince = DateTime.MinValue;
            return false;
        }
        if (_taskbarGoneSince == DateTime.MinValue) _taskbarGoneSince = DateTime.UtcNow;
        return (DateTime.UtcNow - _taskbarGoneSince).TotalMilliseconds >= TaskbarGoneMsNeeded;
    }

    private static bool IsTaskbarUnavailable()
    {
        if (!TaskbarService.IsTaskbarOnScreen()) return true;

        var tray = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero || !NativeMethods.IsWindowVisible(tray)) return true;

        int screenH = GetSystemMetrics(SM_CYSCREEN);
        NativeMethods.RECT tr;
        if (NativeMethods.GetWindowRect(tray, out tr))
        {
            if (tr.top >= screenH - 1 || tr.bottom <= 1) return true;
        }
        return false;
    }



    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            var src = HwndSource.FromHwnd(hwnd);
            if (src?.CompositionTarget != null)
            {
                src.CompositionTarget.BackgroundColor = Colors.Transparent;
            }
        }
        catch { }

        AcrylicWindowHelper.EnableRoundedCorners(this);
        AcrylicWindowHelper.RemoveWindowBorder(this);
        AcrylicWindowHelper.MakeNoActivate(this);
        RefreshBackground();
        RepositionWindow();
        AcrylicWindowHelper.BindToTaskbarLayer(this);
    }

    private void ToggleDetailWindow()
    {
        if (_detailWindow != null && _detailWindow.IsVisible)
        {
            _detailWindow.CloseWithAnimation();
            return;
        }

        if (_detailWindow == null)
        {
            _detailWindow = new SystemMonitorDetailWindow(_settings);
        }

        if (_latestMetrics != null)
        {
            _detailWindow.UpdateMetrics(_latestMetrics);
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        int scale = GetDpiForWindow(hwnd);
        double dpi = scale <= 0 ? 1.0 : scale / 96.0;

        double leftDip = _lastX / dpi;
        double topDip = _lastY / dpi;
        double widthDip = ActualWidth > 0 ? ActualWidth : 160;
        double heightDip = ActualHeight > 0 ? ActualHeight : 48;

        var rect = new Rect(leftDip, topDip, widthDip, heightDip);
        _detailWindow.ShowAtCapsule(rect);
    }

    // ---------------- Win32 ----------------

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint SWP_NOZORDER = 0x0004;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}

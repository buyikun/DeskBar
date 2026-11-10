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

        // AllowsTransparency 只能在建 HWND 之前设定、且不能运行时切换：
        // 岛模式要 accent 亚克力材质（layered 窗口上 accent 会让窗口整块画不出来），
        // 整条模式要 WPF 真透明（accent 那层模糊去不掉）。所以它随 TaskbarStyle 决定，
        // 切风格时由 App.RecreateWidgetWindows() 整窗重建。
        AllowsTransparency = !AcrylicWindowHelper.IsIslandStyle();
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
        // 浮岛的着色强度交给 AcrylicWindowHelper 按当前 TaskbarStyle 自己分档：
        // 岛模式自带磨砂，整条模式只用透明磨砂，保证与音乐胶囊、任务栏材质一致。
        AcrylicWindowHelper.Apply(this, _settings.EffectiveTone, "monitor", _settings.TaskbarBlurAmount);
    }

    /// <summary>描边色跟随 tone：浅色柔雾压暗边、深色黑丝提亮边。</summary>
    private void ApplyToneBorder()
    {
        try
        {
            var tone = _settings.EffectiveTone;
            Resources["CapsuleBorderBrush"] = TonePalette.BorderBrush(tone);
            // 监控浮岛的前景恒为白色（与系统托盘时钟一致），不跟 tone 判深浅。
            // 原因：softmist / sheertights 只有 15% 覆盖，浮岛底色 85% 由壁纸决定，
            // 暗壁纸下合成底色实测 #404038（深灰）—— 深字压上去只有 1.45:1，看不见。
            // 白字在同一个底色上是 10.46:1，且与系统时钟、任务栏图标同色系。
            Resources["CapsuleTextBrush"] = FrozenWhite(0xFF);
            Resources["CapsuleTextMutedBrush"] = FrozenWhite(0x99);
            Resources["CapsuleTrackBrush"] = FrozenWhite(0x33);
        }
        catch (Exception ex)
        {
            Services.Log.Debug($"monitor capsule border apply failed: {ex.Message}");
        }
    }

    /// <summary>白色画刷（冻结，可跨线程安全复用），只按需造三个 alpha 档。</summary>
    private static SolidColorBrush FrozenWhite(byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF));
        brush.Freeze();
        return brush;
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
        if (h <= 0) h = (int)Math.Round(44 * dpi);
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

        // 亚克力材质铺满整个窗口矩形，WPF 的 CornerRadius 只画描边、裁不掉材质 ——
        // 不裁的话四角就是「圆角线 + 方角底色」（2026-11-11 用户截图报的「有角」）。
        try
        {
            var src = HwndSource.FromHwnd(hwnd);
            if (src?.CompositionTarget != null)
            {
                src.CompositionTarget.BackgroundColor = Colors.Transparent;
            }
        }
        catch { }

        // 圆角必须两处同值、同心：DWM 的 DWMWCP_ROUND（8px @100%，会随 DPI 缩放）
        // 负责裁掉 accent / 亚克力材质 —— 材质铺满整个窗口矩形，SetWindowRgn 裁不动它（实测无效）；
        // XAML 的 CornerRadius 负责画那圈 1px 描边。所以 XAML 里必须是 CornerRadius=8。
        // 仅岛模式需要：整条模式（acrylic / clear）浮岛是真透明窗口、没有材质要裁。
        if (AcrylicWindowHelper.IsIslandStyle())
        {
            AcrylicWindowHelper.EnableRoundedCorners(this);
        }
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
        double heightDip = ActualHeight > 0 ? ActualHeight : 44;

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

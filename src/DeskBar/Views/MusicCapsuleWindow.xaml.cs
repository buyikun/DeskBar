using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DeskBar.Interop;
using DeskBar.Services;

namespace DeskBar.Views;

/// <summary>
/// 任务栏音乐胶囊浮岛窗口。
/// 停靠在托盘浮岛左侧，显示专辑封面 / 曲名 / 歌手，并提供上一曲、播放暂停、下一曲控制。
/// 无播放会话或功能关闭时自动隐藏，不占据任务栏空间。
/// </summary>
public partial class MusicCapsuleWindow : Window
{
    // 与托盘区左边缘的间距（物理像素）。太小（8px）时两圈独立描边会挤成一条
    // 「双线竖缝」，中间看不出任务栏底色；14px 让两框各自的圆角与外框都看得清。
    private const int GapToTrayPx = 14;

    private readonly AppSettings _settings;
    private MediaSessionService? _service;
    private DispatcherTimer? _posTimer;
    private MediaTrackInfo? _current;
    private bool _shown;
    private bool _wantVisible;
    // 最近一次真正下发过的窗口位置与尺寸；相同就不再调用 SetWindowPos。
    private int _lastX = int.MinValue, _lastY, _lastW, _lastH;

    // 任务栏「不可用」的去抖（同监控浮岛）：拖拽托盘图标时 explorer 会短暂重排任务栏，
    // 单次误判会让胶囊整块消失 140ms 再淡入。用「连续多久」而非「多少轮」，
    // 因为本窗口 400ms tick、描边窗口 16ms tick，轮数语义不一致。
    private DateTime _taskbarGoneSince = DateTime.MinValue;
    private const int TaskbarGoneMsNeeded = 900;
    // 整条模式下胶囊不再挖洞，改用 60fps 轻量 z 序守卫：
    // 只有当「正压着我们的就是任务栏本身」时才把自己抬回最上层（弹出面板压着时不抢）。
    private DispatcherTimer? _zGuard;

    // 50ms 的位置跟随定时器：托盘图标被拖出/拖入时 TrayNotifyWnd 立刻变形，
    // 靠 400ms 的可见性定时器去跟会有明显滞后，看起来就是「不跟随」。
    // RepositionWindow 内部有「位置尺寸都没变就 return」的短路，平时几乎零开销。
    private DispatcherTimer? _followTimer;
    private bool _isDisposed;

    internal MusicCapsuleWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        Title = "DeskBarMusicCapsule";

        BtnPrev.Click += async (_, _) => { if (_service != null) await _service.SkipPreviousAsync(); };
        BtnPlay.Click += async (_, _) => { if (_service != null) await _service.TogglePlayPauseAsync(); };
        BtnNext.Click += async (_, _) => { if (_service != null) await _service.SkipNextAsync(); };

        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) =>
        {
            _isDisposed = true;
            StopPositionTimer();
            StopZGuard();
            StopFollowTimer();
            if (_service != null)
            {
                _service.MediaInfoChanged -= OnMediaInfoChanged;
                _service = null;
            }
        };

        ApplyTone();
        StartZGuard();
        StartFollowTimer();
    }

    internal void AttachService(MediaSessionService? service)
    {
        if (_service is not null)
        {
            _service.MediaInfoChanged -= OnMediaInfoChanged;
        }

        _service = service;

        if (_service is not null)
        {
            _service.MediaInfoChanged += OnMediaInfoChanged;
        }
        else
        {
            _current = null;
            _wantVisible = false;
            StopPositionTimer();
            HideCapsule();
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        AcrylicWindowHelper.EnableRoundedCorners(this);
        AcrylicWindowHelper.RemoveWindowBorder(this);
        AcrylicWindowHelper.MakeNoActivate(this);

        // 让 WPF 不再绘制不透明底，背景交给 DWM 亚克力渲染
        var src = HwndSource.FromHwnd(hwnd);
        if (src?.CompositionTarget is not null)
        {
            src.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
        }

        ApplyAccent();
        AcrylicWindowHelper.BindToTaskbarLayer(this);
    }

    private void OnMediaInfoChanged(MediaTrackInfo? info)
    {
        if (_isDisposed) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnMediaInfoChanged(info)));
            return;
        }
        if (_isDisposed) return;

        _current = info;
        Services.Log.Debug($"capsule OnMediaInfoChanged: info={(info is null ? "null" : info.Title)} enabled={_settings.MusicCapsuleEnabled} shown={_shown}");
        bool hasTrack = info is not null && _settings.MusicCapsuleEnabled;

        if (!hasTrack)
        {
            _wantVisible = false;
            StopPositionTimer();
            HideCapsule();
            return;
        }

        TxtTitle.Text = info!.Title;
        TxtArtist.Text = info.Artist;
        ImgCover.Source = info.Thumbnail;
        TxtNoCover.Visibility = info.Thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

        string playGlyph = info.IsPlaying ? "\uE769" : "\uE768";
        BtnPlay.Content = playGlyph;
        BtnPlay.ToolTip = info.IsPlaying ? "暂停" : "播放";

        BtnPrev.IsEnabled = info.CanSkipPrev;
        BtnNext.IsEnabled = info.CanSkipNext;
        BtnPlay.IsEnabled = info.CanPlayPause;

        PanelControls.Visibility = _settings.MusicCapsuleShowControls
            ? Visibility.Visible
            : Visibility.Collapsed;

        _wantVisible = true;
        StartPositionTimer();
        UpdateVisibility();
    }

    /// <summary>设置里改动音乐胶囊开关 / 控制键后，立即按新配置重算显示状态。</summary>
    internal void RefreshFromSettings()
    {
        PanelControls.Visibility = _settings.MusicCapsuleShowControls
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!_settings.MusicCapsuleEnabled || _current is null)
        {
            _wantVisible = false;
            StopPositionTimer();
            HideCapsule();
            return;
        }

        _wantVisible = true;
        StartPositionTimer();
        UpdateVisibility();
    }

    /// <summary>任务栏风格 / 亚克力色调变化后重新套用胶囊背景。</summary>
    internal void RefreshBackground()
    {
        ApplyTone();
    }

    private void ApplyTone()
    {
        // 背景由系统 DWM 亚克力负责，上层画刷保持透明，只留细边框
        RootBorder.Background = System.Windows.Media.Brushes.Transparent;
        ApplyAccent();
    }

    /// <summary>应用系统亚克力模糊，使胶囊与任务栏浮岛质感一致。</summary>
    private void ApplyAccent()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        // 先关掉 Win11 系统背景材质：它按"应用主题"渲染，浅色主题下会糊上一块奶白，
        // 与任务栏实际使用的 natural 透壁纸色调对不上。
        int backdropNone = 1;   // DWMSBT_NONE
        try { DwmSetWindowAttribute(hwnd, 38, ref backdropNone, sizeof(int)); } catch { }

        // 与任务栏共用同一套色调常量，保证两处色调永远同步
        var tone = (_settings.AcrylicTone ?? "natural").ToLowerInvariant();
        uint gradient = NativeTaskbarAppearanceService.GetColorForTone(tone);

        // 胶囊永远保留自己那圈细边框：要的是「各自一个细边框」，
        // 而不是把胶囊和托盘描边合成一个整体。
        RootBorder.BorderThickness = new System.Windows.Thickness(1);

        var accent = new AccentPolicy
        {
            // 整条模式下它就盖在任务栏自己的底上，不能再糊一层，
            // 否则连接处会显出「两块不同材质」，也就是用户说的「漏到背景」。
            AccentState = TaskbarService.IsIslandActive ? 4 : 0,
            AccentFlags = 0,
            GradientColor = gradient,
            AnimationId = 0
        };

        int size = Marshal.SizeOf(typeof(AccentPolicy));
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = 19,   // WCA_ACCENT_POLICY
                Data = ptr,
                SizeOfData = size
            };
            int ok = SetWindowCompositionAttribute(hwnd, ref data);
            bool ownSurface = TaskbarService.IsIslandActive;
            Services.Log.Debug($"capsule accent mode={(ownSurface ? "island-acrylic" : "flat-on-taskbar")} style={_settings.TaskbarStyle} tone={tone} color=0x{gradient:X8} ok={ok}");
        }
        catch (Exception ex)
        {
            Services.Log.Debug($"ApplyAccent failed: {ex.Message}");
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }

        // 让 DWM 背景材质铺满整个客户区
        var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    private void StartPositionTimer()
    {
        if (_posTimer is not null) return;
        _posTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _posTimer.Tick += (_, _) => UpdateVisibility();
        _posTimer.Start();
    }

    private void StopPositionTimer()
    {
        if (_posTimer is null) return;
        _posTimer.Stop();
        _posTimer = null;
    }

    /// <summary>16ms 的 z 序守卫：整条模式下胶囊没有独立磨砂面，被任务栏压住就会「看不见」，必须立刻抬回来。</summary>
    private void StartZGuard()
    {
        if (_zGuard is not null) return;
        _zGuard = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _zGuard.Tick += (_, _) =>
        {
            if (_shown) AcrylicWindowHelper.KeepAboveTaskbar(this);
        };
        _zGuard.Start();
    }

    private void StopZGuard()
    {
        if (_zGuard is null) return;
        _zGuard.Stop();
        _zGuard = null;
    }

    /// <summary>
    /// 50ms 一轮只做「跟随」：读实时托盘区左缘重算 x/y，位置没变就直接返回。
    /// 与 UpdateVisibility 分开，是为了不让高频跟随去反复触发显隐判定与淡入淡出。
    /// </summary>
    private void StartFollowTimer()
    {
        if (_followTimer is not null) return;
        _followTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _followTimer.Tick += (_, _) =>
        {
            if (_shown) RepositionWindow();
        };
        _followTimer.Start();
    }

    private void StopFollowTimer()
    {
        if (_followTimer is null) return;
        _followTimer.Stop();
        _followTimer = null;
    }

    /// <summary>统一的可见性状态机：媒体会话是否要显示 + 当前是否该让位，两者共同决定。</summary>
    private void UpdateVisibility()
    {
        if (!_wantVisible)
        {
            if (_shown) HideCapsule();
            return;
        }

        if (_isDisposed) return;

        // 窗口句柄还没创建时，先以全透明 Show 出来拿到句柄；位置与让位判断
        // 交给下一轮定时器（400ms）校正，避免带着错误位置闪一下。
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            if (_isDisposed) return;
            RootBorder.Opacity = 0;
            try
            {
                if (!IsVisible) Show();
            }
            catch (InvalidOperationException)
            {
                _isDisposed = true;
                return;
            }
            hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
        }

        // 任务栏本身没了（自动隐藏把它推出屏幕 / 锁屏）才让位。判定必须连续几轮成立，
        // 否则拖拽托盘图标时 explorer 重排任务栏造成的一次瞬时零矩形就会让胶囊整块消失。
        if (ShouldHideForTaskbar())
        {
            if (_shown) HideCapsule();
            return;
        }

        RepositionWindow();
        if (!_shown) ShowCapsule();
    }

    private void ShowCapsule()
    {
        if (_isDisposed) return;
        _shown = true;
        _lastX = int.MinValue;   // 重新显示时强制做一次定位
        bool wasVisible = IsVisible;
        if (!wasVisible) Show();

        var start = wasVisible ? RootBorder.Opacity : 0.0;
        var anim = new DoubleAnimation(start, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        RootBorder.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void HideCapsule()
    {
        TaskbarService.ClearWidgetExclusion("capsule");
        _shown = false;
        if (!IsVisible)
        {
            RootBorder.BeginAnimation(UIElement.OpacityProperty, null);
            RootBorder.Opacity = 1;
            return;
        }

        var anim = new DoubleAnimation(RootBorder.Opacity, 0, TimeSpan.FromMilliseconds(140));
        anim.Completed += (_, _) =>
        {
            if (_shown) return;
            Hide();
            RootBorder.BeginAnimation(UIElement.OpacityProperty, null);
            RootBorder.Opacity = 1;
        };
        RootBorder.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    /// <summary>把胶囊停靠到托盘浮岛左边缘左侧。</summary>
    private void RepositionWindow()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        // 任务栏被收起时胶囊必须一起让位，否则它作为独立顶层窗口会一直浮在桌面之上。
        // 让位判定已在 UpdateVisibility 里统一做过去抖，这里复用的是同一个计数器。
        if (ShouldHideForTaskbar())
        {
            if (_shown) HideCapsule();
            return;
        }

        // 优先使用实时托盘区左缘：托盘图标一被拖出就立刻跟着走。
        // 之前这里先走 TryGetTrayIslandAnchor 的缓存锚点，而锚点只在 ApplyRegionImmediate
        // 被调用时才刷新，几何收敛后 OnAnimTick 提前 return、锚点就停在那里不动了 ——
        // 表现为「图标从托盘取出后胶囊不跟着移动」。
        bool realtime = TaskbarService.TryGetTrayLeftRealtime(out int rtLeft, out int rtTop, out int rtHeight);

        // 浮岛模式下的锚点仍然保留，作为实时读取失败时的后备。
        // 不能写成 `!realtime && TryGetTrayIslandAnchor(out ...)`：短路求值会让 out 变量
        // 在 realtime 为 true 时处于「未赋值」状态，编译器直接报 CS0165。
        int anchorLeft = 0, anchorTop = 0, taskbarHeight = 0;
        bool anchored = false;
        if (!realtime)
        {
            anchored = TaskbarService.TryGetTrayIslandAnchor(out anchorLeft, out anchorTop, out taskbarHeight);
        }

        int scale = GetDpiForWindow(hwnd);
        double dpi = scale <= 0 ? 1.0 : scale / 96.0;

        int w = (int)Math.Round(Width * dpi);
        int h = (int)Math.Round(Height * dpi);

        int x, y;
        if (realtime)
        {
            int tbHr = rtHeight;
            if (tbHr > 0 && h > tbHr) h = tbHr;
            x = rtLeft - GapToTrayPx - w;
            y = rtTop + (tbHr - h) / 2;
        }
        else if (anchored)
        {
            // 胶囊高度不得超出任务栏，否则居中后会上下溢出，看起来"悬浮在桌面上"
            if (taskbarHeight > 0 && h > taskbarHeight) h = taskbarHeight;
            x = anchorLeft - GapToTrayPx - w;
            y = anchorTop + (taskbarHeight - h) / 2;
        }
        else if (TaskbarService.TryGetIslandRectsOnScreen(out _, out _, out int trayLeftScreen, out _, out int tbTop3, out int tbH3))
        {
            // 整条模式：照样贴着托盘区左缘 —— 与两条描边窗口用的是同一份几何，天然对齐。
            // 之前这里是 x = 任务栏右缘 - 260 - w 的固定偏移，托盘图标一增减就"不跟随"：
            // 要么和新出现的托盘图标挤在一起，要么留出一道忽宽忽窄的缝。
            if (tbH3 > 0 && h > tbH3) h = tbH3;
            x = trayLeftScreen - GapToTrayPx - w;
            y = tbTop3 + (tbH3 - h) / 2;
        }
        else if (TaskbarService.TryGetShellTaskbarRect(out _, out int tbTop2, out int tbRight2, out int tbBottom2))
        {
            // 连托盘区几何都取不到（UIA 与 Win32 都失败）时的兜底：按任务栏窗口矩形贴靠。
            // 旧写法用工作区底边 +4 推算，胶囊会整体下沉 4px 并溢出任务栏底边。
            int tbH2 = tbBottom2 - tbTop2;
            if (tbH2 > 0 && h > tbH2) h = tbH2;
            x = tbRight2 - 260 - w;
            y = tbTop2 + (tbH2 - h) / 2;
        }
        else
        {
            // 最后兜底：贴合主屏工作区下方任务栏位置
            var wa = SystemParameters.WorkArea;
            x = (int)(wa.Right * dpi) - w - 260;
            y = (int)(wa.Bottom * dpi);
        }

        // 位置和尺寸都没变就不要再下发：原实现每 400ms 无条件
        // SetWindowPos + SWP_SHOWWINDOW，会让浮岛被反复重绘，
        // 表现为「点托盘图标时系统信息与音乐组件闪烁」。
        if (x == _lastX && y == _lastY && w == _lastW && h == _lastH) return;
        _lastX = x;
        _lastY = y;
        _lastW = w;
        _lastH = h;

        // 移动时用 SWP_NOZORDER，绝不在这里动 z 序。
        // 原实现传 TaskbarLayerAnchor() 当 hWndInsertAfter，而它取的是「任务栏正上方那个窗口」——
        // 那个窗口经常就是我们自己的另一个浮岛（甚至就是本窗口自己）。当锚点等于目标窗口本身
        // 或已被销毁时，SetWindowPos 会直接失败：位置一动不动，但 _lastX 已经更新过，
        // 于是再也不会重试 —— 表现就是「把图标从托盘拖出来，胶囊不跟着走」，
        // 以及拖动过程中成功/失败交替导致的闪烁。z 序统一交给 16ms 的 KeepAboveTaskbar 守卫。
        SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h,
            SWP_NOACTIVATE | SWP_NOZORDER);

        // 胶囊不再给任务栏挖洞：挖了洞就会露出未经模糊的原始壁纸（用户说的「漏到背景」）。
        // 保持任务栏自己的半透明底，胶囊只画内容 + 一圈细边框。
        TaskbarService.ClearWidgetExclusion("capsule");
    }

    /// <summary>
    /// 判断胶囊此刻是否应让位隐藏：任务栏窗口不可见、被自动隐藏移出屏幕，
    /// 或有全屏前台窗口盖住了任务栏。
    /// </summary>
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    /// <summary>
    /// 「任务栏真的没了」才返回 true：必须连续 TaskbarGoneTicksNeeded 轮判定失败。
    /// 单轮瞬时误判（拖拽托盘图标时 explorer 重排任务栏、GetWindowRect 拿到零矩形）
    /// 会被忽略，否则胶囊会整块消失再淡入。
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

    /// <summary>任务栏本身不可用：不存在、不可见、或被自动隐藏推离了屏幕。</summary>
    private static bool IsTaskbarUnavailable()
    {
        try
        {
            var tray = NativeMethods.FindWindowW("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return true;
            if (!NativeMethods.IsWindowVisible(tray)) return true;

            int screenH = GetSystemMetrics(SM_CYSCREEN);
            if (NativeMethods.GetWindowRect(tray, out var tr) &&
                (tr.top >= screenH - 1 || tr.bottom <= 1)) return true;
        }
        catch { }
        return false;
    }



    // ---------------- Win32 ----------------

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint SWP_NOZORDER = 0x0004;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }
}

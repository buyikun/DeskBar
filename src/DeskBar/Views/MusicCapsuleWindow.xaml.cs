using System;
using System.Collections.Generic;
using System.Linq;
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
/// 停靠在托盘浮岛左侧，显示专辑封面 / 曲名 / 歌手 / 实时歌词，并提供上一曲、播放暂停、下一曲控制。
/// 无播放会话或功能关闭时自动隐藏，不占据任务栏空间。
/// </summary>
public partial class MusicCapsuleWindow : Window
{
    /// <summary>
    /// 音乐胶囊右边缘距离托盘左边缘（TrayNotifyWnd）的距离。
    /// 托盘描边框向左外扩了 BorderEdgePadPx (16px)，为保证胶囊与托盘描边框之间
    /// 留出 10px 干净舒适的呼吸间隙，这里取外扩留白 + 10px。
    /// </summary>
    private static int GapToTrayPx => ShapeGeometry.BorderEdgePadPx + 10;

    private readonly AppSettings _settings;
    private MediaSessionService? _service;
    private DispatcherTimer? _posTimer;
    private MediaTrackInfo? _current;
    private bool _shown;
    private bool _wantVisible;
    private int _lastX = int.MinValue, _lastY, _lastW, _lastH;

    private DateTime _taskbarGoneSince = DateTime.MinValue;
    private const int TaskbarGoneMsNeeded = 900;
    private DispatcherTimer? _zGuard;
    private DispatcherTimer? _followTimer;
    private bool _isDisposed;

    private bool _hovering;
    private DispatcherTimer? _hoverTimer;
    private int _hoverPolls;
    private bool _showLyrics = true;
    /// <summary>整行文本宽度超出视口的像素数；0 表示整句放得下、不需要滚动。</summary>
    private double _lyricOverflow;

    /// <summary>当前显示的歌词行（取字级时间戳用）；null 表示回落显示歌手。</summary>
    private LyricLine? _currentLine;
    /// <summary>与 _currentLine.Chars 一一对应的逐字像素宽度；文本变化时置空重算。</summary>
    private double[]? _charWidths;
    /// <summary>最近一次拿到的播放位置。</summary>
    private TimeSpan _lyricPosition;
    /// <summary>滚动目标位置（px），由演唱位置决定。</summary>
    private double _scrollTarget;
    /// <summary>实际滚动位置（px），按指数缓动逼近目标。</summary>
    private double _scrollRendered;
    private DispatcherTimer? _scrollTimer;
    private DateTime _scrollLastTick = DateTime.UtcNow;
    private int _marqueeLogTick;
    /// <summary>逐字测宽缓存：字符 → 像素宽度（照 WaterSprite 的 Map 缓存做法）。</summary>
    private readonly Dictionary<char, double> _charWidthCache = new();

    /// <summary>XAML 里给歌词两层声明的默认字体，用户没导入字体时用它回退。</summary>
    /// <remarks>
    /// 写全限定名：本文件只 using 了 System.Windows.Media.Animation，
    /// 而项目同时开了 UseWindowsForms，System.Drawing 里也有 FontFamily 同名类型，
    /// 加 using System.Windows.Media 会和别处的 Media 类型命名产生歧义风险，
    /// 只为一个类型引入整段命名空间不值得。
    /// </remarks>
    private System.Windows.Media.FontFamily? _defaultLyricFont;

    /// <summary>歌名行在 XAML 里没有显式 FontFamily（继承窗口默认），单独记一份用于回退。</summary>
    private System.Windows.Media.FontFamily? _defaultTitleFont;

    internal MusicCapsuleWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        // 同 SystemMonitorCapsuleWindow：整条模式必须真透明、岛模式必须能叠 accent 材质，
        // 二者互斥且不能运行时切换，所以按 TaskbarStyle 定，切风格时整窗重建。
        AllowsTransparency = !AcrylicWindowHelper.IsIslandStyle();

        Title = "DeskBarMusicCapsule";

        // 记住 XAML 里声明的默认字体，用户清空自备字体时回退到它们
        _defaultLyricFont = TxtArtist.FontFamily;
        _defaultTitleFont = TxtTitle.FontFamily;

        BtnPrev.Click += async (_, _) => { if (_service != null) await _service.SkipPreviousAsync(); };
        BtnPlay.Click += async (_, _) => { if (_service != null) await _service.TogglePlayPauseAsync(); };
        BtnNext.Click += async (_, _) => { if (_service != null) await _service.SkipNextAsync(); };

        BorderCover.MouseLeftButtonUp += (_, _) => OpenSourceApp();
        MouseRightButtonUp += OnMouseRightButtonUp;
        LyricScroller.SizeChanged += (_, _) => UpdateLyricMarquee();

        MouseEnter += (_, _) => { _hovering = true; UpdateControlsVisibility(); };
        MouseLeave += (_, _) => { _hovering = false; UpdateControlsVisibility(); };

        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) =>
        {
            _isDisposed = true;
            StopPositionTimer();
            StopZGuard();
            StopFollowTimer();
            StopHoverTimer();
            StopScrollTimer();
            if (_service != null)
            {
                _service.MediaInfoChanged -= OnMediaInfoChanged;
                _service.LyricUpdated -= OnLyricUpdated;
                _service.LyricTick -= OnLyricTick;
                _service = null;
            }
        };

        ApplyTone();
        StartZGuard();
        StartFollowTimer();
        StartHoverTimer();
        StartScrollTimer();
        UpdateControlsVisibility();
    }

    internal void AttachService(MediaSessionService? service)
    {
        if (_service is not null)
        {
            _service.MediaInfoChanged -= OnMediaInfoChanged;
            _service.LyricUpdated -= OnLyricUpdated;
            _service.LyricTick -= OnLyricTick;
        }

        _service = service;

        if (_service is not null)
        {
            _service.MediaInfoChanged += OnMediaInfoChanged;
            _service.LyricUpdated += OnLyricUpdated;
            _service.LyricTick += OnLyricTick;

            // RecreateWidgetWindows 会先 new 新窗口、再回到 SyncMusicCapsule 调本方法，
            // 此时服务里多半已有当前曲目，但事件只为「变化」而发，不会补发既有状态。
            // 不主动拉一次，_current 就一直是 null，RefreshFromSettings 会把窗口 Hide 掉、再也不显示。
            if (_service.CurrentInfo is MediaTrackInfo snapshot)
            {
                OnMediaInfoChanged(snapshot);
            }
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

        // 圆角必须两处同值、同心：DWM 的 DWMWCP_ROUND（8px @100%，会随 DPI 缩放）
        // 负责裁掉 accent / 亚克力材质 —— 材质铺满整个窗口矩形，SetWindowRgn 裁不动它（实测无效）；
        // XAML 的 CornerRadius 负责画那圈 1px 描边。所以 XAML 里必须是 CornerRadius=8。
        // 仅岛模式需要：整条模式（acrylic / clear）浮岛是真透明窗口、没有材质要裁。
        if (AcrylicWindowHelper.IsIslandStyle())
        {
            AcrylicWindowHelper.EnableRoundedCorners(this);
        }
        AcrylicWindowHelper.RemoveWindowBorder(this);
        // 亚克力材质铺满整个窗口矩形，WPF 的 CornerRadius 只画描边、裁不掉材质 ——
        // 不裁的话四角就是「圆角线 + 方角底色」（2026-11-11 用户截图报的「有角」）。
        AcrylicWindowHelper.MakeNoActivate(this);

        var src = HwndSource.FromHwnd(hwnd);
        if (src?.CompositionTarget is not null)
        {
            src.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
        }

        ApplyAccent();
        AcrylicWindowHelper.BindToTaskbarLayer(this);
    }

    /// <summary>
    /// 拼第一行「歌名 - 歌手」。
    ///
    /// 两个必须挡掉的边界：
    /// ① 播放器没给歌手时 MediaSessionService 会填占位符「未知歌手」，拼上去就是
    ///    「某某 - 未知歌手」，比不拼更糟。所以占位符一律当没有。
    /// ② 很多播放器（尤其 QQ 音乐的部分音源）把歌手直接写进 Title，
    ///    「告白气球 - 周杰伦」这种再拼一次就变「告白气球 - 周杰伦 - 周杰伦」。
    ///    标题里已经能查到歌手名就不再追加。
    /// </summary>
    private static string BuildTitleLine(string? title, string? artist)
    {
        string name = string.IsNullOrWhiteSpace(title) ? "未知曲目" : title.Trim();

        string who = string.IsNullOrWhiteSpace(artist) ? "" : artist.Trim();
        if (who.Length == 0 || who == "未知歌手") return name;
        if (name.Contains(who, StringComparison.OrdinalIgnoreCase)) return name;

        return $"{name} - {who}";
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

        // 第一行显示「歌名 - 歌手」，是辅助信息；第二行歌词才是主角。
        TxtTitle.Text = BuildTitleLine(info!.Title, info.Artist);
        _currentLine = null; // 新曲目还没拿到歌词行，先回落显示歌手
        SetLyricText(info.Artist);
        ImgCover.Source = info.Thumbnail;
        TxtNoCover.Visibility = info.Thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

        string playGlyph = info.IsPlaying ? "\uE769" : "\uE768";
        BtnPlay.Content = playGlyph;
        BtnPlay.ToolTip = info.IsPlaying ? "暂停" : "播放";

        BtnPrev.IsEnabled = info.CanSkipPrev;
        BtnNext.IsEnabled = info.CanSkipNext;
        BtnPlay.IsEnabled = info.CanPlayPause;

        string friendlySource = string.IsNullOrEmpty(info.FriendlyAppName) ? info.AppId : info.FriendlyAppName;
        RootBorder.ToolTip = $"{info.Title}\n{info.Artist}\n来源：{friendlySource}";
        BorderCover.ToolTip = $"点击打开/唤醒 {friendlySource}";

        // 不要在这里直接设成 Visible：那会无视 _hovering，导致切歌后控件常显，
        // 直到鼠标再进出胶囊一次才恢复。统一交给 UpdateControlsVisibility 裁决。
        UpdateControlsVisibility();

        _wantVisible = true;
        StartPositionTimer();
        UpdateVisibility();
    }

    private void OnLyricUpdated(string? lyric)
    {
        if (_isDisposed) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnLyricUpdated(lyric)));
            return;
        }
        if (_isDisposed || _current == null) return;

        SetLyricText(_showLyrics && !string.IsNullOrWhiteSpace(lyric) ? lyric : _current.Artist);
    }

    /// <summary>
    /// 切换歌词文本。双层 TextBlock 必须同时改：底层是整句底色，上层靠 Width 裁出已唱部分。
    /// </summary>
    private void SetLyricText(string text)
    {
        if (_isDisposed) return;
        if (TxtArtist.Text == text && TxtLyricPlayed.Text == text) return;

        TxtArtist.Text = text;
        TxtLyricPlayed.Text = text;

        // 文本变了，逐字宽度作废；滚动也回到句首，避免带着上一句的偏移闪一下。
        _charWidths = null;
        _scrollTarget = 0;
        _scrollRendered = 0;
        LyricScroller.ScrollToHorizontalOffset(0);
        TxtLyricPlayed.Width = 0;
        // 清掉上一句的显式宽度，让 UpdateLyricMarquee 按新文本重新排版。
        TxtArtist.Width = double.NaN;

        Dispatcher.BeginInvoke(new Action(UpdateLyricMarquee), DispatcherPriority.Loaded);
    }

    internal void RefreshFromSettings()
    {
        ApplyCapsuleWidth();
        UpdateControlsVisibility();

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
        UpdateLyricMarquee();
    }

    /// <summary>把设置里的胶囊宽度应用到窗口；定位计算直接读 Width，所以改这里就够了。</summary>
    private void ApplyCapsuleWidth()
    {
        int raw = _settings.MusicCapsuleWidth;
        if (raw <= 0) raw = 250;
        double target = Math.Clamp(raw, 200, 520);

        if (Math.Abs(Width - target) > 0.5)
        {
            Width = target;
            Services.Log.Debug($"capsule width applied: {target}");
        }
    }

    /// <summary>播放控件默认收起，鼠标移入胶囊才显示；设置里关掉则永不显示。</summary>
    private void UpdateControlsVisibility()
    {
        bool show = _settings.MusicCapsuleShowControls && _hovering;
        var target = show ? Visibility.Visible : Visibility.Collapsed;
        if (ControlsBackdrop.Visibility == target) return;

        ControlsBackdrop.Visibility = target;
        Services.Log.Debug($"capsule controls -> {target} (hovering={_hovering}, enabled={_settings.MusicCapsuleShowControls})");
        // 控件是浮层、不占用布局宽度，歌词视口不随悬停变化；这里只让跑马灯按新状态复核一次。
        Dispatcher.BeginInvoke(new Action(UpdateLyricMarquee), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 量一次当前歌词的逐字宽度，算出整行宽度与溢出量。
    /// 逐字宽度同时供「已唱高亮」和「滚动锚点」使用（照 WaterSprite 的 getTextWidth + highlightedWidth）。
    /// </summary>
    private void UpdateLyricMarquee()
    {
        _lyricOverflow = 0;
        _scrollTarget = 0;

        if (_isDisposed || !_showLyrics) return;

        string text = TxtArtist.Text ?? string.Empty;
        if (text.Length == 0) return;

        var widths = MeasureCharWidths(text);
        double measured = 0;
        for (int i = 0; i < widths.Length; i++) measured += widths[i];

        // 关键：TextBlock 不设 Width，交给 ScrollViewer 做无限宽度测量，
        // extent 才是真正的自然排版宽度。之前用自己量的宽度去写 TxtArtist.Width，
        // 等于把错误的测量值变成了 extent，ScrollableWidth 随之偏小，末字永远滚不出来。
        if (!double.IsNaN(TxtArtist.Width)) TxtArtist.Width = double.NaN;
        TxtArtist.UpdateLayout();

        double viewport = LyricScroller.ViewportWidth;
        if (viewport <= 16 || double.IsNaN(viewport)) return; // 布局还没完成，等 SizeChanged 再来

        double extent = LyricScroller.ExtentWidth;
        if (double.IsNaN(extent) || extent <= 0) extent = measured;

        // 逐字宽度按「真实 extent / 测量累加值」等比缩放，让高亮像素和实际渲染对齐。
        if (measured > 0.01 && Math.Abs(extent - measured) > 0.5)
        {
            double scale = extent / measured;
            for (int i = 0; i < widths.Length; i++) widths[i] *= scale;
        }

        // 只有文本确实对应这一行歌词时才缓存，否则逐字宽度和 Chars 的下标对不上。
        _charWidths = _currentLine is not null && _currentLine.Text == text ? widths : null;

        // 关键：滚动上限由 WPF 布局引擎给出（= 内容宽 - 视口宽），不再由代码估算，
        // 所以即使逐字测宽有偏差，末字也一定能滚出来。对应 WaterSprite 里浏览器保证的
        // scrollLeft 最大值 scrollWidth - clientWidth。
        double scrollable = LyricScroller.ScrollableWidth;
        if (double.IsNaN(scrollable) || scrollable < 0) scrollable = 0;

        if (scrollable <= 0.5)
        {
            // 整句放得下：不需要滚动，但逐字高亮照常（宽度由 UpdatePlayedHighlight 设）。
            if (LyricScroller.HorizontalOffset > 0.01) LyricScroller.ScrollToHorizontalOffset(0);
            return;
        }

        _lyricOverflow = scrollable;
        Services.Log.Debug($"marquee: viewport={viewport:F1} extent={extent:F1} measured={measured:F1} scrollable={scrollable:F1} chars={_charWidths?.Length ?? -1} line='{text}'");
    }

    /// <summary>
    /// 逐字测宽（照 WaterSprite 的 getTextWidth）：每个字符单独量一遍并缓存。
    /// 中英混排时整行宽度不能靠等宽假设，必须逐字累加。
    /// </summary>
    private double[] MeasureCharWidths(string text)
    {
        var widths = new double[text.Length];
        var typeface = new System.Windows.Media.Typeface(
            TxtArtist.FontFamily, TxtArtist.FontStyle, TxtArtist.FontWeight, TxtArtist.FontStretch);
        double pixelsPerDip = System.Windows.Media.VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (_charWidthCache.TryGetValue(c, out double cached))
            {
                widths[i] = cached;
                continue;
            }

            var ft = new System.Windows.Media.FormattedText(
                c.ToString(),
                System.Globalization.CultureInfo.CurrentUICulture,
                System.Windows.FlowDirection.LeftToRight,
                typeface,
                TxtArtist.FontSize,
                System.Windows.Media.Brushes.White,
                pixelsPerDip);

            widths[i] = ft.WidthIncludingTrailingWhitespace;
            _charWidthCache[c] = widths[i];
        }

        return widths;
    }

    private void OnLyricTick(LyricLine? line, TimeSpan position)
    {
        if (_isDisposed) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnLyricTick(line, position)));
            return;
        }

        if (!ReferenceEquals(_currentLine, line))
        {
            _currentLine = line;
            _charWidths = null; // 换行了，逐字宽度要按新文本重算
            _scrollTarget = 0;
            _scrollRendered = 0;
            LyricScroller.ScrollToHorizontalOffset(0);
            TxtLyricPlayed.Width = 0;
            Dispatcher.BeginInvoke(new Action(UpdateLyricMarquee), DispatcherPriority.Loaded);
        }

        _lyricPosition = position;
        UpdatePlayedHighlight(line, position);
        UpdateScrollTarget(line, position);
    }

    /// <summary>
    /// 把上层 TextBlock 的宽度设成「已唱像素宽度」——露出多少字由它决定，
    /// 这就是逐字高亮（对应 WaterSprite 的 lyric--played）。
    /// </summary>
    private void UpdatePlayedHighlight(LyricLine? line, TimeSpan position)
    {
        if (line is null) return;

        // 行级 LRC 没有字级时间：唱到就整行点亮。
        // 注意：这里必须设成文本的自然宽度（而不是 NaN），因为 NaN 会让 TextBlock
        // 回落到父容器约束的 164px，整句只有前 164px 能被点亮。
        if (line.IsLineLevel || _charWidths is null)
        {
            TxtLyricPlayed.Width = double.IsNaN(TxtArtist.Width) ? double.NaN : TxtArtist.Width;
            return;
        }

        double played = line.GetPlayedWidth(_charWidths, position);
        TxtLyricPlayed.Width = Math.Max(0, played);

        _marqueeLogTick++;
        if (_marqueeLogTick % 40 == 0)
        {
            Services.Log.Debug($"lyric highlight: pos={position.TotalSeconds:F2}s played={played:F1} chars={_charWidths.Length} line='{line.Text}'");
        }
    }

    /// <summary>
    /// 算出滚动目标位置（照 WaterSprite 的滚动逻辑）：行级 LRC 用线性比例
    /// （提前 800ms 起步、提前 800ms 到位）；逐字歌词则跟住已唱位置，
    /// 只有当锚点跑出视口左侧、或越过右侧 90% 时才跳到「锚点 - 视口 10%」。
    /// </summary>
    private void UpdateScrollTarget(LyricLine? line, TimeSpan position)
    {
        double viewport = LyricScroller.ViewportWidth;
        if (viewport <= 16 || double.IsNaN(viewport)) return;

        double max = _lyricOverflow;
        if (max <= 0 || line is null)
        {
            _scrollTarget = 0;
            return;
        }

        if (line.IsLineLevel)
        {
            double lineStart = line.Time.TotalMilliseconds;
            double span = Math.Max(1000, (line.End - line.Time).TotalMilliseconds - 1600);
            double fraction = Math.Clamp((position.TotalMilliseconds - lineStart - 800) / span, 0, 1);
            _scrollTarget = max * fraction;
            return;
        }

        double anchor = _charWidths is null ? 0 : line.GetPlayedWidth(_charWidths, position);
        _scrollTarget = anchor < _scrollRendered || anchor - _scrollRendered > viewport * 0.9
            ? Math.Clamp(anchor - viewport * 0.1, 0, max)
            : Math.Clamp(_scrollTarget, 0, max);
    }

    private void StartScrollTimer()
    {
        if (_scrollTimer is not null) return;
        _scrollLastTick = DateTime.UtcNow;
        _scrollTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _scrollTimer.Tick += (_, _) => OnScrollTick();
        _scrollTimer.Start();
    }

    private void StopScrollTimer()
    {
        if (_scrollTimer is null) return;
        _scrollTimer.Stop();
        _scrollTimer = null;
    }

    /// <summary>
    /// 每帧把实际滚动位置按指数缓动逼近目标（时间常数 200ms）——与 WaterSprite 的
    /// `difference * (1 - Math.exp(-5 * delta / 1000))` 一致，避免进度跳变时画面抽搐。
    /// </summary>
    private void OnScrollTick()
    {
        if (_isDisposed) return;

        var now = DateTime.UtcNow;
        double dt = (now - _scrollLastTick).TotalMilliseconds;
        _scrollLastTick = now;
        if (dt <= 0 || dt > 500) dt = 16;

        double difference = _scrollTarget - _scrollRendered;
        _scrollRendered = Math.Abs(difference) < 0.5
            ? _scrollTarget
            : _scrollRendered + difference * (1 - Math.Exp(-5 * dt / 1000));

        // 交给 ScrollViewer 自己钳位：超出 ScrollableWidth 的偏移会被它自动收回到最大值，
        // 这就是 WaterSprite 依赖浏览器 scrollLeft 行为的等价物。
        if (Math.Abs(LyricScroller.HorizontalOffset - _scrollRendered) >= 0.5)
        {
            LyricScroller.ScrollToHorizontalOffset(_scrollRendered);
        }

        _marqueeLogTick++;
        if (_marqueeLogTick % 60 == 0)
        {
            Services.Log.Debug($"marquee tick: pos={_lyricPosition.TotalSeconds:F1}s target={_scrollTarget:F1} rendered={_scrollRendered:F1} offset={LyricScroller.HorizontalOffset:F1} scrollable={LyricScroller.ScrollableWidth:F1}");
        }
    }

    internal void RefreshBackground()
    {
        ApplyTone();
    }

    // alpha=1 而不是 Transparent(alpha=0)：整条模式下本窗口是分层窗口(AllowsTransparency=True)，
    // 完全透明的像素不参与鼠标命中，框内空白区会整块漏给下面的任务栏（右键直接弹任务栏菜单）。
    // 这一层肉眼不可见，只为让整个框都吃鼠标事件 —— 监控浮岛根 Border 的 #01000000 是同一个用意。
    private static readonly System.Windows.Media.SolidColorBrush HitTestBackdrop = CreateHitTestBackdrop();

    private static System.Windows.Media.SolidColorBrush CreateHitTestBackdrop()
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(1, 0, 0, 0));
        brush.Freeze();
        return brush;
    }

    private void ApplyTone()
    {
        RootBorder.Background = HitTestBackdrop;
        ApplyToneBorder();
        ApplyLyricFont();
        ApplyAccent();
    }

    /// <summary>
    /// 套用用户自备的字体。没配就是系统默认（XAML 里的 FontFamilyBase / 窗口继承字体）。
    /// 加载器自带按路径缓存，所以这里每次 tone 刷新都调用也不会重复读盘。
    /// </summary>
    private void ApplyLyricFont()
    {
        try
        {
            var family = LyricFont.FromFile(_settings.MusicCapsuleLyricFontPath, out _);

            // 歌名行、歌词两层、回落显示的歌手名，全部跟随自备字体。
            // 设置页只说「歌词字体」，但用户看到的是一整块文字，
            // 只换其中两行会显得割裂（2026-11-10 用户要求标题也一起换）。
            TxtTitle.FontFamily = family ?? _defaultTitleFont;
            TxtArtist.FontFamily = family ?? _defaultLyricFont;
            TxtLyricPlayed.FontFamily = family ?? _defaultLyricFont;

            // 换字体后字符宽度全变，必须让逐字高亮与滚动重新测量
            _charWidths = null;
            _charWidthCache.Clear();
            _scrollTarget = 0;
            _scrollRendered = 0;
        }
        catch (Exception ex)
        {
            Services.Log.Debug($"ApplyLyricFont failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 描边与悬停/按下色阶按当前 tone 重写进窗口资源。
    /// XAML 里用 DynamicResource 引用，换画刷即时生效，不必重建 ControlTemplate。
    /// 浅色柔雾用黑色系压边、深色黑丝用白色系提边 —— 同一条边不可能两种底色下都好看。
    /// </summary>
    private void ApplyToneBorder()
    {
        try
        {
            var tone = _settings.EffectiveTone;
            Resources["CapsuleBorderBrush"] = TonePalette.BorderBrush(tone);
            Resources["CapsuleHoverBrush"] = TonePalette.HoverBrush(tone);
            Resources["CapsulePressedBrush"] = TonePalette.PressedBrush(tone);
            // 前景必须与底色同向：softmist 恒浅色底，light 跟随系统深浅，
            // 一律由 IsDark(tone) 判定，这里整批按深/浅档下发，避免浅底浅字。
            Resources["CapsuleTextBrush"] = TonePalette.CapsuleTextBrush(tone);
            Resources["CapsuleTextMutedBrush"] = TonePalette.CapsuleTextMutedBrush(tone);
            Resources["CapsuleTextFaintBrush"] = TonePalette.CapsuleTextFaintBrush(tone);
            Resources["CapsuleButtonForegroundBrush"] = TonePalette.CapsuleButtonForegroundBrush(tone);
            Resources["CapsuleHoverForegroundBrush"] = TonePalette.CapsuleHoverForegroundBrush(tone);
            Resources["CapsuleCoverBackBrush"] = TonePalette.CapsuleCoverBackBrush(tone);
            Resources["CapsuleControlsMaskBrush"] = TonePalette.CapsuleControlsMaskBrush(tone);
        }
        catch (Exception ex)
        {
            Services.Log.Debug($"ApplyToneBorder failed: {ex.Message}");
        }
    }

    private void ApplyAccent()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        int backdropNone = 1;
        try { DwmSetWindowAttribute(hwnd, 38, ref backdropNone, sizeof(int)); } catch { }

        var tone = (_settings.EffectiveTone ?? "natural").ToLowerInvariant();
        // 与 AcrylicWindowHelper 同一套分档：只有岛模式需要自带色调材质，
        // 整条模式去掉着色，避免浮岛比任务栏深一块。
        var style = (_settings.TaskbarStyle ?? "island_acrylic").ToLowerInvariant();
        uint gradient = style == "island_acrylic" ? NativeTaskbarAppearanceService.GetColorForTone(tone) : 0u;

        // 整条模式（acrylic / clear）：本窗口按 AllowsTransparency=True 建，靠 WPF 真透明，
        // 绝不能再叠 accent —— accent 会给客户区糊上一层去掉不掉的磨砂（2026-11-11 实测）。
        if (style != "island_acrylic")
        {
            Services.Log.Debug($"capsule accent style={style} transparent-skip");
            return;
        }

        RootBorder.BorderThickness = new System.Windows.Thickness(1);
        
        // 此处调用与 AcrylicWindowHelper 保持一致逻辑，保持浮岛磨砂质感
        var accent = new AccentPolicy
        {
            AccentState = 4,   // 见 AcrylicWindowHelper：0 会让浮岛变成纯黑板
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
                Attribute = 19,
                Data = ptr,
                SizeOfData = size
            };
            int ok = SetWindowCompositionAttribute(hwnd, ref data);
            Services.Log.Debug($"capsule accent style={style} tone={tone} color=0x{gradient:X8} ok={ok}");
        }
        catch (Exception ex)
        {
            Services.Log.Debug($"ApplyAccent failed: {ex.Message}");
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }

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

    private void StartHoverTimer()
    {
        if (_hoverTimer is not null) return;
        _hoverTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _hoverTimer.Tick += (_, _) => PollHover();
        _hoverTimer.Start();
    }

    private void StopHoverTimer()
    {
        if (_hoverTimer is null) return;
        _hoverTimer.Stop();
        _hoverTimer = null;
    }

    /// <summary>
    /// 这个窗口不可激活（WS_EX_NOACTIVATE + ShowActivated=false），WPF 的
    /// MouseEnter / MouseLeave 收不到可靠的进出通知，表现为移开鼠标后控件不消失。
    /// 因此改用轮询：直接比较光标屏幕坐标与窗口屏幕矩形，作为 _hovering 的唯一真值来源。
    /// </summary>
    private void PollHover()
    {
        if (_isDisposed) return;
        _hoverPolls++;
        try
        {
            if (!NativeMethods.GetCursorPos(out var pt)) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            if (!NativeMethods.GetWindowRect(hwnd, out var r)) return;

            bool inside = pt.x >= r.left && pt.x < r.right && pt.y >= r.top && pt.y < r.bottom;
            if (_hoverPolls <= 3 || inside != _hovering)
            {
                Services.Log.Debug(
                    $"capsule hover #{_hoverPolls}: cursor=({pt.x},{pt.y}) rect=({r.left},{r.top},{r.right},{r.bottom}) inside={inside} hovering={_hovering}");
            }

            _hovering = inside;

            // 每次都重新裁决（UpdateControlsVisibility 内部自带早退）。不能只在
            // inside 发生变化时才调用：切歌等路径可能绕过 _hovering 改动过 Visibility。
            UpdateControlsVisibility();
        }
        catch (Exception ex)
        {
            if (_hoverPolls <= 5) Services.Log.Warn($"capsule hover poll failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void UpdateVisibility()
    {
        if (!_wantVisible)
        {
            if (_shown) HideCapsule();
            return;
        }

        if (_isDisposed) return;

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
        _lastX = int.MinValue;
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

    private void RepositionWindow()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        if (ShouldHideForTaskbar())
        {
            if (_shown) HideCapsule();
            return;
        }

        bool realtime = TaskbarService.TryGetTrayLeftRealtime(out int rtLeft, out int rtTop, out int rtHeight);
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
            if (taskbarHeight > 0 && h > taskbarHeight) h = taskbarHeight;
            x = anchorLeft - GapToTrayPx - w;
            y = anchorTop + (taskbarHeight - h) / 2;
        }
        else if (TaskbarService.TryGetIslandRectsOnScreen(out _, out _, out int trayLeftScreen, out _, out int tbTop3, out int tbH3))
        {
            if (tbH3 > 0 && h > tbH3) h = tbH3;
            x = trayLeftScreen - GapToTrayPx - w;
            y = tbTop3 + (tbH3 - h) / 2;
        }
        else if (TaskbarService.TryGetShellTaskbarRect(out _, out int tbTop2, out int tbRight2, out int tbBottom2))
        {
            int tbH2 = tbBottom2 - tbTop2;
            if (tbH2 > 0 && h > tbH2) h = tbH2;
            x = tbRight2 - 260 - w;
            y = tbTop2 + (tbH2 - h) / 2;
        }
        else
        {
            var wa = SystemParameters.WorkArea;
            x = (int)(wa.Right * dpi) - w - 260;
            y = (int)(wa.Bottom * dpi);
        }

        if (x == _lastX && y == _lastY && w == _lastW && h == _lastH) return;
        _lastX = x;
        _lastY = y;
        _lastW = w;
        _lastH = h;

        SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h,
            SWP_NOACTIVATE | SWP_NOZORDER);

        TaskbarService.ClearWidgetExclusion("capsule");
    }

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

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
        try
        {
            if (!TaskbarService.IsTaskbarOnScreen()) return true;

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

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint SWP_NOZORDER = 0x0004;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

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

    /// <summary>
    /// 点击封面：把当前媒体来源的播放器窗口带到前台。
    /// 按 进程名 → UWP/AUMID → Shell 三级回退，覆盖 Win32 播放器与浏览器播放。
    /// </summary>
    private void OpenSourceApp()
    {
        if (_current == null || string.IsNullOrWhiteSpace(_current.AppId))
        {
            Services.Log.Debug("OpenSourceApp: no current session or app id");
            return;
        }

        string appId = _current.AppId;
        try
        {
            string? procName = ResolveProcessName(appId);
            if (procName != null)
            {
                // 多进程应用（Chrome/Edge/QQ音乐）逐个 PID 试，挑真正带窗口的那个
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(procName))
                {
                    if (Services.PlayerWindowControl.Window(p.Id) == IntPtr.Zero) continue;

                    if (Services.PlayerWindowControl.Open(p.Id))
                    {
                        Services.Log.Debug($"OpenSourceApp: activated '{procName}' pid={p.Id} {Services.PlayerWindowControl.LastDiagnostic}");
                        return;
                    }

                    Services.Log.Debug($"OpenSourceApp: Open pid={p.Id} failed {Services.PlayerWindowControl.LastDiagnostic}");
                }

                Services.Log.Debug($"OpenSourceApp: no activatable window for '{procName}'");
            }

            // 回退 1：UWP / 打包应用，AppId 形如 PackageFamilyName!AppId
            if (appId.Contains('!') || (appId.Contains('_') && appId.Contains('.')))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"shell:appsFolder\\{appId}",
                    UseShellExecute = true
                });
                Services.Log.Debug($"OpenSourceApp: launched AUMID via shell:appsFolder -> {appId}");
                return;
            }

            // 回退 2：Shell 打开可执行文件路径
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = appId,
                UseShellExecute = true
            });
            Services.Log.Debug($"OpenSourceApp: shell-launched '{appId}'");
        }
        catch (Exception ex)
        {
            Services.Log.Warn($"OpenSourceApp failed for '{appId}': {ex.Message}");
        }
    }

    /// <summary>
    /// 把 GSMTC 的 SourceAppUserModelId 映射为进程名；返回 null 表示无法按进程名定位。
    /// 浏览器给的是 "Chrome" / "Microsoft.Edge" / "Mozilla.Firefox" 这类标识，
    /// 而非 "Microsoft.Edge" 会被 GetFileNameWithoutExtension 误当成扩展名取出 "Microsoft"。
    /// </summary>
    private static string? ResolveProcessName(string appId)
    {
        if (appId.Contains("Edge", StringComparison.OrdinalIgnoreCase)) return "msedge";
        if (appId.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) return "chrome";
        if (appId.Contains("Firefox", StringComparison.OrdinalIgnoreCase)) return "firefox";
        if (appId.Contains("Brave", StringComparison.OrdinalIgnoreCase)) return "brave";
        if (appId.Contains("Opera", StringComparison.OrdinalIgnoreCase)) return "opera";

        string name = System.IO.Path.GetFileNameWithoutExtension(appId);
        if (string.IsNullOrWhiteSpace(name)) return null;

        // "Microsoft.ZuneMusic" 之类会退化成无意义前缀（含点号且不像可执行名）
        if (!appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && appId.Contains('.')) return null;

        return name;
    }

    private void OnMouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu();

        if (_current != null)
        {
            string friendly = string.IsNullOrEmpty(_current.FriendlyAppName) ? _current.AppId : _current.FriendlyAppName;
            var openItem = new System.Windows.Controls.MenuItem
            {
                Header = $"打开 {friendly}"
            };
            openItem.Click += (_, _) => OpenSourceApp();
            menu.Items.Add(openItem);

            var copyItem = new System.Windows.Controls.MenuItem
            {
                Header = "复制歌曲信息"
            };
            copyItem.Click += (_, _) =>
            {
                try
                {
                    System.Windows.Clipboard.SetText($"{_current.Title} - {_current.Artist}");
                }
                catch { }
            };
            menu.Items.Add(copyItem);

            var lyricItem = new System.Windows.Controls.MenuItem
            {
                Header = "显示实时歌词",
                IsCheckable = true,
                IsChecked = _showLyrics
            };
            lyricItem.Click += (_, _) =>
            {
                _showLyrics = !_showLyrics;
                if (!_showLyrics)
                {
                    _currentLine = null;
                    if (_current != null) SetLyricText(_current.Artist);
                }
                else if (!string.IsNullOrWhiteSpace(_service?.CurrentLyric))
                {
                    // 行对象由下一次 LyricTick 补上，这里先把文本切过去。
                    SetLyricText(_service!.CurrentLyric!);
                }
                UpdateLyricMarquee();
            };
            menu.Items.Add(lyricItem);

            menu.Items.Add(new System.Windows.Controls.Separator());
        }

        var sourceItem = new System.Windows.Controls.MenuItem { Header = "媒体来源" };

        var sources = _service?.GetAvailableSources() ?? Array.Empty<string>();

        // 「自动选择」何时算选中：没有固定来源时选中；固定来源已经关掉（不在当前列表里）时，
        // 实际生效的也是自动选择，所以同样打勾。否则会出现「固定项没打勾、自动项也没打勾」
        // 这种看不出当前到底在跟谁的空白状态。
        string? pinned = _service?.PinnedAppId;
        bool pinnedPresent = !string.IsNullOrEmpty(pinned)
            && sources.Any(s => string.Equals(s, pinned, StringComparison.OrdinalIgnoreCase));

        var autoItem = new System.Windows.Controls.MenuItem
        {
            Header = pinnedPresent ? "自动选择" : "自动选择（当前生效）",
            IsChecked = !pinnedPresent
        };
        autoItem.Click += (_, _) => _service?.PinSource(null);
        sourceItem.Items.Add(autoItem);
        sourceItem.Items.Add(new System.Windows.Controls.Separator());

        if (sources.Count == 0)
        {
            sourceItem.Items.Add(new System.Windows.Controls.MenuItem
            {
                Header = _service == null ? "（音乐胶囊未启用）" : "（当前没有可接管的播放器）",
                IsEnabled = false
            });
        }

        foreach (var src in sources)
        {
            string friendly = MediaSessionService.GetFriendlySourceName(src);
            // 标出来源类型，用户才能理解自动选择为什么会挑中它（音乐 3 > 浏览器 2 > 视频 1）。
            string kind = MediaSourceRouter.DescribeKind(src);
            string headerText = friendly != src ? $"{friendly} ({src})" : src;
            if (kind.Length > 0) headerText = $"{headerText}  ·{kind}";

            var item = new System.Windows.Controls.MenuItem
            {
                Header = headerText,
                IsChecked = string.Equals(pinned, src, StringComparison.OrdinalIgnoreCase),
                ToolTip = string.Equals(pinned, src, StringComparison.OrdinalIgnoreCase)
                    ? "当前已手动固定到此来源，点一下可取消固定"
                    : $"固定到此来源（{src}）"
            };
            item.Click += (_, _) => _service?.PinSource(src);
            sourceItem.Items.Add(item);
        }

        menu.Items.Add(sourceItem);

        menu.Items.Add(new System.Windows.Controls.Separator());
        var settingsItem = new System.Windows.Controls.MenuItem { Header = "打开设置…" };
        settingsItem.Click += (_, _) =>
        {
            try
            {
                (System.Windows.Application.Current as App)?.ShowSettingsWindow();
            }
            catch (Exception ex)
            {
                Services.Log.Debug($"capsule: open settings failed: {ex.Message}");
            }
        };
        menu.Items.Add(settingsItem);

        // 本窗口带 WS_EX_NOACTIVATE，WPF 的 ContextMenu 收不到「点击菜单外」的关闭通知
        // （它依赖窗口激活状态或 Popup 的鼠标捕获），表现为只有点回胶囊内部菜单才消失。
        // 对策：菜单打开期间临时摘掉 NOACTIVATE 并把前台抢过来，关闭后原样还原并交还前台。
        IntPtr capsuleHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        long originalExStyle = GetWindowLongPtr(capsuleHwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(capsuleHwnd, GWL_EXSTYLE, (IntPtr)(originalExStyle & ~WS_EX_NOACTIVATE));
        bool gotForeground = Services.PlayerWindowControl.ActivateWindow(capsuleHwnd);
        Services.Log.Debug($"capsule menu opening: hwnd=0x{capsuleHwnd.ToInt64():X} foreground={gotForeground}");

        menu.Closed += (_, _) =>
        {
            try
            {
                SetWindowLongPtr(capsuleHwnd, GWL_EXSTYLE, (IntPtr)originalExStyle);
                // Win32 弹出菜单的标准收尾：投递 WM_NULL，把前台队列交还回去。
                Interop.NativeMethods.PostMessageW(capsuleHwnd, 0x0000, IntPtr.Zero, IntPtr.Zero);
                Services.Log.Debug("capsule menu closed: NOACTIVATE restored");
            }
            catch (Exception ex)
            {
                Services.Log.Warn($"capsule menu close cleanup failed: {ex.GetType().Name}: {ex.Message}");
            }
        };

        menu.IsOpen = true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }
}

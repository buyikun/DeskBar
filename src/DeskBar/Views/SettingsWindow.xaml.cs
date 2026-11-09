using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeskBar.Services;
// 项目启用了 ImplicitUsings + UseWindowsForms，全局 using 里同时有
// System.Windows.Forms 和 System.Drawing，与 WPF 的同名类型撞车，
// 这里显式指定 WPF 版本。
using ComboBox = System.Windows.Controls.ComboBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace DeskBar.Views;

public partial class SettingsWindow : Window
{
    private bool _loading;
    private readonly DispatcherTimer _statusTimer;

    public SettingsWindow()
    {
        _loading = true;
        InitializeComponent();
        BuildWhitelistPresets();
        LoadFromSettings();
        ShowPage(PageGeneral);
        TxtVersion.Text = "v" + (System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString(3) ?? "0.1.0");
        _loading = false;

        // 状态条里的「下次切换还有多久」需要随时间推进，用低频定时器刷新（10 秒一次足够）
        _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _statusTimer.Tick += (_, _) => UpdateThemeStatus();
        _statusTimer.Start();
    }

    /// <summary>切换右侧内容页；只显示一个，其余折叠。</summary>
    private void ShowPage(UIElement page)
    {
        foreach (var p in new UIElement[] { PageGeneral, PageAppearance, PageTheme, PageWidgets, PageUpdate, PageDiag })
        {
            p.Visibility = ReferenceEquals(p, page) ? Visibility.Visible : Visibility.Collapsed;
        }
        ((ScrollViewer)page).ScrollToTop();
    }

    /// <summary>
    /// 用 Checked 而不是 Click：鼠标点击、键盘方向键、UIA/无障碍调用 Select() 都会置 IsChecked，
    /// 只有 Click 会漏掉后两种，导致"导航项高亮了但页面没换"。
    /// </summary>
    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        // InitializeComponent 里 IsChecked="True" 会先触发一次，此时页面控件还没全部就绪
        if (!IsLoaded) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (sender == NavGeneral) ShowPage(PageGeneral);
            else if (sender == NavAppearance) ShowPage(PageAppearance);
            else if (sender == NavTheme) ShowPage(PageTheme);
            else if (sender == NavWidgets) ShowPage(PageWidgets);
            else if (sender == NavUpdate) ShowPage(PageUpdate);
            else if (sender == NavDiag) ShowPage(PageDiag);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        AcrylicWindowHelper.EnableRoundedCorners(this);
        SyncTitleBarTheme();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        SyncTitleBarTheme();
        UpdateThemeStatus();
    }

    /// <summary>由 App 在系统主题变化时回调，让已打开的窗口跟上配色。</summary>
    internal void RefreshThemeVisuals()
    {
        SyncTitleBarTheme();
        UpdateThemeStatus();
        UpdateToneSwatches();
    }

    private void SyncTitleBarTheme()
    {
        try
        {
            if (TryFindResource("WindowBackground") is not SolidColorBrush brush) return;
            var c = brush.Color;
            var luma = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
            AcrylicWindowHelper.SetDarkTitleBar(this, luma < 128);
        }
        catch (Exception ex)
        {
            Log.Debug($"SyncTitleBarTheme failed: {ex.Message}");
        }
    }

    // ===================== 载入 =====================

    private void LoadFromSettings()
    {
        _loading = true;
        var s = App.Settings;

        // 通用
        ChkDoubleClick.IsChecked = s.DoubleClickToggle;
        ChkShowTray.IsChecked = s.ShowTrayIcon;
        ChkStartup.IsChecked = StartupService.IsEnabled();

        // 外观
        ChkTranslucentTaskbar.IsChecked = s.TranslucentTaskbar;
        SelectComboByTag(CmbTaskbarStyle, s.TaskbarStyle, "island_acrylic");
        SelectComboByTag(CmbAcrylicTone, s.AcrylicTone, "natural");
        SldBlurAmount.Value = Math.Clamp(s.TaskbarBlurAmount, 0, 100);
        TxtBlurValue.Text = ((int)SldBlurAmount.Value).ToString();

        // 自动主题
        ChkAutoTimeTheme.IsChecked = s.AutoTimeTheme;
        TxtLightTime.Text = s.LightStartTime;
        TxtDarkTime.Text = s.DarkStartTime;
        RdoDayLight.IsChecked = s.DayThemeLight;
        RdoDayDark.IsChecked = !s.DayThemeLight;
        RdoNightLight.IsChecked = s.NightThemeLight;
        RdoNightDark.IsChecked = !s.NightThemeLight;
        SelectComboByTag(CmbDayTone, s.DayAcrylicTone, "softmist");
        SelectComboByTag(CmbNightTone, s.NightAcrylicTone, "sheertights");
        ChkApplySystemTheme.IsChecked = s.AutoTimeApplySystemTheme;
        ChkApplyWindowsMode.IsChecked = s.AutoTimeApplyWindowsMode;
        ChkApplyAppsMode.IsChecked = s.AutoTimeApplyAppsMode;

        // 小部件
        ChkMusicCapsule.IsChecked = s.MusicCapsuleEnabled;
        ChkMusicAutoHide.IsChecked = s.MusicCapsuleAutoHide;
        ChkMusicControls.IsChecked = s.MusicCapsuleShowControls;
        TxtMusicWhitelist.Text = s.MusicCapsuleWhitelist;
        SldMusicWidth.Value = Math.Clamp(s.MusicCapsuleWidth, 200, 520);
        TxtMusicWidthValue.Text = $"{(int)SldMusicWidth.Value} px";
        ChkSystemMonitor.IsChecked = s.SystemMonitorEnabled;

        // 诊断
        ChkDebugLog.IsChecked = s.EnableDebugLog;

        // 更新
        ChkAutoCheckUpdate.IsChecked = s.AutoCheckUpdate;
        TxtAccelPrefix.Text = s.UpdateAccelPrefix;
        TxtUpdateCurrent.Text = "当前版本 v" + UpdateService.CurrentVersion;
        UpdateAccelPreview();

        // 联动可见性 / 状态文案
        UpdateToneLockState();
        UpdateToneSwatches();
        UpdateSpanSummary();
        UpdateThemeStatus();
        UpdateRuntimeHints();

        _loading = false;

        UpdateWhitelistChips();
        UpdateWhitelistStatus();
    }

    private static void SelectComboByTag(ComboBox combo, string? tag, string fallback)
    {
        foreach (var obj in combo.Items)
        {
            if (obj is ComboBoxItem item && string.Equals((string?)item.Tag, tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        foreach (var obj in combo.Items)
        {
            if (obj is ComboBoxItem item && string.Equals((string?)item.Tag, fallback, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private static string? SelectedTag(ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Tag as string;

    private void Persist() => ((App)Application.Current).SaveSettings();

    // ===================== 状态刷新 =====================

    /// <summary>自动主题接管色调时，禁用「外观」页的色调下拉并给出说明。</summary>
    private void UpdateToneLockState()
    {
        var auto = App.Settings.AutoTimeTheme;
        CmbAcrylicTone.IsEnabled = !auto;
        TxtToneLocked.Visibility = auto ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateToneSwatches()
    {
        ApplySwatch(ToneSwatch, SelectedTag(CmbAcrylicTone));
        ApplySwatch(DayToneSwatch, SelectedTag(CmbDayTone));
        ApplySwatch(NightToneSwatch, SelectedTag(CmbNightTone));
    }

    /// <summary>
    /// 把色调键渲染成小色块。
    /// 色调本身是低 alpha 的 ARGB：直接铺在卡片上等于没有；合成到单色背景上又会把
    /// softmist / sheertights 这种 rgb 只差 12% 的色调压成同一种灰。
    /// 所以采用设计软件通用的"棋盘格 + 半透明色"表示法：棋盘格露出来的程度就是透明度，
    /// 色相就是实际着色。natural（alpha=0）会露出完整棋盘格，表示"没有着色层"。
    /// </summary>
    private static void ApplySwatch(Border target, string? tone)
    {
        try
        {
            var argb = NativeTaskbarAppearanceService.GetColorForTone(tone ?? "natural");
            var color = Color.FromArgb(
                (byte)((argb >> 24) & 0xFF),
                (byte)((argb >> 16) & 0xFF),
                (byte)((argb >> 8) & 0xFF),
                (byte)(argb & 0xFF));

            // 棋盘格按 10px 一格平铺，色调作为最上层整体覆盖，透明度自然"透"出格子
            const double tile = 10.0;
            var group = new DrawingGroup();

            // 浅格
            group.Children.Add(new GeometryDrawing(
                new SolidColorBrush(Color.FromRgb(0x76, 0x76, 0x76)), null,
                new RectangleGeometry(new Rect(0, 0, tile, tile))));

            // 深格（对角两个半格）
            var darkSquares = new GeometryGroup();
            darkSquares.Children.Add(new RectangleGeometry(new Rect(0, 0, tile / 2, tile / 2)));
            darkSquares.Children.Add(new RectangleGeometry(new Rect(tile / 2, tile / 2, tile / 2, tile / 2)));
            group.Children.Add(new GeometryDrawing(
                new SolidColorBrush(Color.FromRgb(0x4E, 0x4E, 0x4E)), null, darkSquares));

            // 色调层
            group.Children.Add(new GeometryDrawing(
                new SolidColorBrush(color), null,
                new RectangleGeometry(new Rect(0, 0, tile, tile))));

            target.Background = new DrawingBrush(group)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, tile, tile),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, tile, tile),
                ViewboxUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill
            };

            // 色块只表达"色相 + 透明度"，但柔雾/黑丝的 alpha 只有 14.5%，棋盘格几乎全露出来，
            // 用户会以为"没生效"。这里把具体值写进 ToolTip，让"几乎透明"这件事本身可见。
            var alphaPct = (int)Math.Round(color.A / 255.0 * 100);
            target.ToolTip = $"{ToneDisplayName(tone)}\n着色 ARGB: #{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}\n不透明度: {alphaPct}%（{100 - alphaPct}% 透出壁纸）";
        }
        catch (Exception ex)
        {
            Log.Debug($"ApplySwatch failed: {ex.Message}");
        }
    }

    /// <summary>刷新自动主题页顶部的状态条。</summary>
    private void UpdateThemeStatus()
    {
        try
        {
            var app = (App)Application.Current;
            // Theme 是 App 上的 static 属性，不能通过实例访问。
            var theme = App.Theme;
            if (theme is null) return;

            var s = App.Settings;
            var isDay = theme.IsDayNow;
            var plan = theme.PlanAt(DateTime.Now);

            TxtCurrentPhase.Text = isDay ? "白天" : "夜晚";
            BdCurrentPhase.Background = isDay
                ? (Brush)FindResource("AccentBrush")
                : new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x6A));

            TxtCurrentPlan.Text = $"{(plan.IsLight ? "浅色界面" : "深色界面")} · {ToneDisplayName(plan.Tone)}";

            // 两个注册表值分别受各自子开关托管：没被托管的那一项写「不改」，
            // 免得用户误以为「关掉一项」等于「整个系统都不改」。
            var sysNow = theme.GetSystemUsesLightTheme() ? "浅色" : "深色";
            var appsNow = theme.GetAppsUseLightTheme() ? "浅色" : "深色";
            var sysState = s.AutoTimeApplySystemTheme && s.AutoTimeApplyWindowsMode
                ? $"当前 {sysNow}，由本软件按时段托管"
                : "不改";
            var appsState = s.AutoTimeApplySystemTheme && s.AutoTimeApplyAppsMode
                ? $"当前 {appsNow}，由本软件按时段托管"
                : "不改";
            TxtSystemTheme.Text = $"Windows 模式：{sysState} · 应用模式：{appsState}";

            if (!s.AutoTimeTheme)
            {
                TxtNextSwitch.Text = "自动切换已关闭：到点不会变，只有点「立即应用当前时段」才生效。";
            }
            else
            {
                var (at, nextPlan) = theme.NextSwitch();
                TxtNextSwitch.Text =
                    $"下一次切换：{at:MM-dd HH:mm} → {(nextPlan.IsLight ? "浅色界面" : "深色界面")} · {ToneDisplayName(nextPlan.Tone)}"
                    + $"（{ThemeSchedule.DescribeRemaining(at - DateTime.Now)}）";
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"UpdateThemeStatus failed: {ex.Message}");
        }
    }

    private static string ToneDisplayName(string? tone) => (tone ?? "").ToLowerInvariant() switch
    {
        "natural" => "原味自然",
        "softmist" => "柔雾",
        "sheertights" => "黑丝",
        "light" => "白玉磨砂",
        "dark" => "黑曜石",
        _ => tone ?? "未知"
    };

    /// <summary>概览页的"当前为啥这样"运行态提示，只在真有降级/接管时出现。</summary>
    private void UpdateRuntimeHints()
    {
        var lines = new List<string>();

        if (!NativeTaskbarAppearanceService.IsAvailable)
        {
            // 注入失败与权限无关：Explorer 与本进程同为 Medium IL，同级别注入本就允许。
            // 失败通常意味着 ExplorerTAP.dll 缺失 / 版本不匹配 / Explorer 正在重启。
            lines.Add("任务栏渲染层暂不可用，已降级为整条磨砂：居中止岛失效。"
                      + "请确认 ExplorerTAP.dll 与程序同目录，或重启资源管理器后重新打开本窗口。");
        }

        if (App.Settings.AutoTimeTheme)
        {
            lines.Add("自动主题已接管材质色调，每日按白天 / 夜晚方案自动切换。");

            // 到点改不改 Windows 系统设置，取决于总开关与两个分项开关的实际状态。
            var s = App.Settings;
            if (!s.AutoTimeApplySystemTheme)
            {
                lines.Add("Windows 系统主题未被托管：到点只改 DeskBar 自己的外观，不动系统设置。");
            }
            else
            {
                var targets = new List<string>();
                if (s.AutoTimeApplyWindowsMode) targets.Add("Windows 模式");
                if (s.AutoTimeApplyAppsMode) targets.Add("应用模式");

                if (targets.Count == 0)
                {
                    lines.Add("总开关虽开着，但 Windows 模式与应用模式都未托管：不写注册表，也不广播刷新。");
                }
                else
                {
                    lines.Add($"到点会写入 {string.Join(" 与 ", targets)} 的注册表值，并广播刷新消息；"
                              + (targets.Count == 1 ? "另一项保持你手选的样子。" : "两项一起改。"));
                }
            }
        }

        if (lines.Count == 0)
        {
            BdRuntimeHint.Visibility = Visibility.Collapsed;
            return;
        }

        BdRuntimeHint.Visibility = Visibility.Visible;
        TxtRuntimeHint.Text = string.Join("\n", lines);
    }

    /// <summary>把两个时间点换算成"白天 X 小时 Y 分 / 夜晚 …"的摘要。</summary>
    private void UpdateSpanSummary()
    {
        try
        {
            if (!AppSettings.TryNormalizeTime(TxtLightTime.Text, out var dayStart)
                || !AppSettings.TryNormalizeTime(TxtDarkTime.Text, out var nightStart))
            {
                TxtSpanSummary.Text = "";
                return;
            }

            var daySpan = nightStart - dayStart;
            if (daySpan < TimeSpan.Zero) daySpan += TimeSpan.FromDays(1);
            var nightSpan = TimeSpan.FromDays(1) - daySpan;

            TxtSpanSummary.Text =
                $"白天 {FormatSpan(daySpan)} · 夜晚 {FormatSpan(nightSpan)}";
        }
        catch (Exception ex)
        {
            Log.Debug($"UpdateSpanSummary failed: {ex.Message}");
        }
    }

    private static string FormatSpan(TimeSpan span)
    {
        var h = (int)span.TotalHours;
        var m = span.Minutes;
        return m == 0 ? $"{h} 小时" : $"{h} 小时 {m} 分";
    }

    // ===================== 通用 =====================

    private void OnStartupChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.RunAtStartup = ChkStartup.IsChecked == true;
        Persist();
    }

    private void OnShowTrayChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.ShowTrayIcon = ChkShowTray.IsChecked == true;
        Persist();
    }

    private void OnDoubleClickChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.DoubleClickToggle = ChkDoubleClick.IsChecked == true;
        Persist();
    }

    // ===================== 外观 =====================

    private void OnTranslucentTaskbarChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.TranslucentTaskbar = ChkTranslucentTaskbar.IsChecked == true;
        Persist();
        UpdateRuntimeHints();
    }

    private void OnTaskbarStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var tag = SelectedTag(CmbTaskbarStyle);
        if (tag is null) return;

        App.Settings.TaskbarStyle = tag;
        TxtStyleDesc.Text = tag switch
        {
            "island_acrylic" => "居中岛屿：把开始区与托盘区各挖成独立浮岛，中间透出壁纸。依赖 ExplorerTAP 注入任务栏外观；注入失败时自动降级为只监视任务栏几何、不裁任务栏。",
            "acrylic" => "整条磨砂：整条任务栏铺一层毛玻璃，保留系统图标与布局。",
            "clear" => "整条全透明：整条任务栏不画底色，完全透出壁纸。",
            _ => TxtStyleDesc.Text
        };

        Persist();
        UpdateRuntimeHints();
    }

    private void OnAcrylicToneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var tag = SelectedTag(CmbAcrylicTone);
        if (tag is null) return;

        App.Settings.AcrylicTone = tag;
        ApplySwatch(ToneSwatch, tag);

        // 菜单配色只跟 tone 走，与任务栏是否开启磨砂无关，所以单独下发一次
        App.ApplyMenuTheme(App.Settings.EffectiveTone);

        var app = (App)Application.Current;
        Persist();
        if (App.Settings.TranslucentTaskbar)
        {
            try { TaskbarService.ApplyStyle(App.Settings.TaskbarStyle, App.Settings.EffectiveTone); }
            catch (Exception ex) { Log.Error($"apply style failed: {ex.Message}"); }
            app.RefreshWidgetBackgrounds();
        }
    }

    private void OnBlurAmountChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        TxtBlurValue.Text = ((int)SldBlurAmount.Value).ToString();
        App.Settings.TaskbarBlurAmount = (int)Math.Round(SldBlurAmount.Value);
        Persist();
    }

    // ===================== 自动主题 =====================

    private void OnAutoTimeThemeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.AutoTimeTheme = ChkAutoTimeTheme.IsChecked == true;
        Persist();

        var app = (App)Application.Current;
        app.ApplyToneForCurrentPlan(force: true);
        UpdateToneLockState();
        UpdateThemeStatus();
        UpdateRuntimeHints();
    }

    private void OnApplyCurrentPlanNow(object sender, RoutedEventArgs e)
    {
        try
        {
            var app = (App)Application.Current;
            // Theme 是 App 上的 static 属性，不能通过实例访问。
            var theme = App.Theme;
            if (theme is null) return;

            var plan = theme.PlanAt(DateTime.Now);
            // SetTheme 内部会再按「Windows 模式 / 应用模式」两个子开关决定写哪几个值；
            // 总开关关闭时它一个字节都不写，这里保留前置判断省掉一次空调用。
            if (App.Settings.AutoTimeApplySystemTheme)
            {
                theme.SetTheme(plan.IsLight);
            }
            ((App)Application.Current).ApplyToneNow(plan.Tone);
            App.ApplyAppTheme(plan.IsLight);
            UpdateThemeStatus();
            TxtFooterHint.Text = $"已应用当前时段：{(plan.IsLight ? "浅色界面" : "深色界面")} · {ToneDisplayName(plan.Tone)}";
        }
        catch (Exception ex)
        {
            Log.Error($"apply current plan failed: {ex.Message}");
            MessageBox.Show($"应用失败：{ex.Message}", "DeskBar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDayShadeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.DayThemeLight = RdoDayLight.IsChecked == true;
        Persist();
        ((App)Application.Current).ApplyToneForCurrentPlan(force: true);
        UpdateThemeStatus();
    }

    private void OnNightShadeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.NightThemeLight = RdoNightLight.IsChecked == true;
        Persist();
        ((App)Application.Current).ApplyToneForCurrentPlan(force: true);
        UpdateThemeStatus();
    }

    private void OnDayToneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var tag = SelectedTag(CmbDayTone);
        if (tag is null) return;
        App.Settings.DayAcrylicTone = tag;
        ApplySwatch(DayToneSwatch, tag);
        Persist();
        ((App)Application.Current).ApplyToneForCurrentPlan(force: true);
        UpdateThemeStatus();
    }

    private void OnNightToneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var tag = SelectedTag(CmbNightTone);
        if (tag is null) return;
        App.Settings.NightAcrylicTone = tag;
        ApplySwatch(NightToneSwatch, tag);
        Persist();
        ((App)Application.Current).ApplyToneForCurrentPlan(force: true);
        UpdateThemeStatus();
    }

    private void OnApplySystemThemeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.AutoTimeApplySystemTheme = ChkApplySystemTheme.IsChecked == true;
        Persist();
        var theme = App.Theme;
        if (theme is not null) theme.ApplySystemTheme = App.Settings.AutoTimeApplySystemTheme;
        UpdateThemeStatus();
        UpdateRuntimeHints();
    }

    private void OnApplyWindowsModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.AutoTimeApplyWindowsMode = ChkApplyWindowsMode.IsChecked == true;
        Persist();
        var theme = App.Theme;
        if (theme is not null) theme.ApplyWindowsMode = App.Settings.AutoTimeApplyWindowsMode;
        UpdateThemeStatus();
        UpdateRuntimeHints();
    }

    private void OnApplyAppsModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.AutoTimeApplyAppsMode = ChkApplyAppsMode.IsChecked == true;
        Persist();
        var theme = App.Theme;
        if (theme is not null) theme.ApplyAppsMode = App.Settings.AutoTimeApplyAppsMode;
        UpdateThemeStatus();
        UpdateRuntimeHints();
    }

    // --- 时间输入：只在失焦 / 回车时校验提交，不再每敲一个字符就落盘 ---

    private void OnTimeBoxFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box) box.SelectAll();
    }

    private void OnTimeBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter)
        {
            CommitTimes();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key is Key.Escape)
        {
            // 放弃编辑，恢复已保存的值
            _loading = true;
            TxtLightTime.Text = App.Settings.LightStartTime;
            TxtDarkTime.Text = App.Settings.DarkStartTime;
            _loading = false;
            HideTimeError();
            e.Handled = true;
        }
    }

    private void OnTimeBoxLostFocus(object sender, RoutedEventArgs e) => CommitTimes();

    /// <summary>
    /// 校验并提交两个时刻。非法输入不落盘、不生效，并在下方给出明确错误，
    /// 输入框保留用户的原始文本以便修改。
    /// </summary>
    private void CommitTimes()
    {
        if (_loading) return;

        var lightOk = AppSettings.TryNormalizeTime(TxtLightTime.Text, out var light);
        var darkOk = AppSettings.TryNormalizeTime(TxtDarkTime.Text, out var dark);

        if (!lightOk || !darkOk)
        {
            ShowTimeError(!lightOk
                ? "白天的开始时间不是有效时刻，请按 24 小时制填写，例如 07:00。"
                : "夜晚的开始时间不是有效时刻，请按 24 小时制填写，例如 19:00。");
            return;
        }

        if (light == dark)
        {
            ShowTimeError("白天与夜晚的开始时间不能相同，否则无法划分时段。");
            return;
        }

        HideTimeError();

        var lightText = AppSettings.FormatTime(light);
        var darkText = AppSettings.FormatTime(dark);

        // 把用户输入规整成 HH:mm 显示（例如把 7:30 补成 07:30）
        _loading = true;
        TxtLightTime.Text = lightText;
        TxtDarkTime.Text = darkText;
        _loading = false;

        var changed = App.Settings.LightStartTime != lightText || App.Settings.DarkStartTime != darkText;
        if (!changed)
        {
            UpdateSpanSummary();
            return;
        }

        App.Settings.LightStartTime = lightText;
        App.Settings.DarkStartTime = darkText;
        Persist();

        UpdateSpanSummary();
        UpdateThemeStatus();
        TxtFooterHint.Text = $"时段已更新：白天 {lightText} 起 · 夜晚 {darkText} 起";
    }

    private void ShowTimeError(string message)
    {
        TxtTimeError.Text = message;
        TxtTimeError.Visibility = Visibility.Visible;
    }

    private void HideTimeError()
    {
        TxtTimeError.Visibility = Visibility.Collapsed;
    }

    // ===================== 小部件 =====================

    private void OnMusicCapsuleChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.MusicCapsuleEnabled = ChkMusicCapsule.IsChecked == true;
        Persist();
        // 开关本身会改变「有没有来源可检测」，白名单状态文案要跟着刷新。
        UpdateWhitelistStatus();
    }

    private void OnMusicAutoHideChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.MusicCapsuleAutoHide = ChkMusicAutoHide.IsChecked == true;
        Persist();
    }

    private void OnMusicControlsChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.MusicCapsuleShowControls = ChkMusicControls.IsChecked == true;
        Persist();
    }

    private void OnMusicWidthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        TxtMusicWidthValue.Text = $"{(int)SldMusicWidth.Value} px";
        App.Settings.MusicCapsuleWidth = (int)Math.Round(SldMusicWidth.Value);
        Persist();
    }

    private void OnMusicWhitelistChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        App.Settings.MusicCapsuleWhitelist = TxtMusicWhitelist.Text.Trim();
        Persist();
        UpdateWhitelistStatus();
        UpdateWhitelistChips();
    }

    // ===================== 播放器白名单 =====================

    /// <summary>
    /// 常见播放器快捷项。填的是「匹配关键字」，MediaWhitelist 会同时比对进程名和
    /// 友好名（见 MediaSessionService.GetFriendlySourceName），所以这里填中文名也能命中。
    /// </summary>
    private static readonly (string Label, string Key)[] WhitelistPresets =
    {
        ("QQ音乐", "QQ音乐"),
        ("网易云音乐", "网易云音乐"),
        ("酷狗音乐", "酷狗"),
        ("酷我音乐", "酷我"),
        ("汽水音乐", "汽水"),
        ("Spotify", "Spotify"),
        ("Apple Music", "Apple Music"),
        ("Foobar2000", "Foobar2000"),
        ("PotPlayer", "PotPlayer"),
        ("VLC", "VLC"),
        ("哔哩哔哩", "哔哩"),
        ("Edge", "Edge"),
        ("Chrome", "Chrome"),
        ("Firefox", "Firefox")
    };

    private void BuildWhitelistPresets()
    {
        PnlWhitelistPresets.Children.Clear();
        foreach (var (label, key) in WhitelistPresets)
        {
            var chip = new Button
            {
                Content = label,
                Tag = key,
                Style = (Style)FindResource("ChipButton")
            };
            chip.Click += OnWhitelistPresetClick;
            PnlWhitelistPresets.Children.Add(chip);
        }
        UpdateWhitelistChips();
    }

    /// <summary>已加入白名单的快捷项高亮显示，让用户一眼看出当前选中了谁。</summary>
    private void UpdateWhitelistChips()
    {
        var entries = MediaWhitelist.Parse(TxtMusicWhitelist.Text);
        foreach (var obj in PnlWhitelistPresets.Children)
        {
            if (obj is not Button chip || chip.Tag is not string key) continue;
            bool on = entries.Exists(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase));
            chip.Style = (Style)FindResource(on ? "ChipButtonOn" : "ChipButton");
        }
    }

    private void OnWhitelistPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;

        var entries = MediaWhitelist.Parse(TxtMusicWhitelist.Text);
        int index = entries.FindIndex(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) entries.RemoveAt(index);
        else entries.Add(key);

        // 写回输入框会触发 OnMusicWhitelistChanged，落盘与状态刷新都走那一处，避免两条路径。
        TxtMusicWhitelist.Text = string.Join(", ", entries);
    }

    /// <summary>
    /// 实时反馈：说清楚白名单现在是否生效、命中了谁、又把谁挡在外面。
    /// 之前这块完全没有反馈 —— 用户填错一个全角逗号，胶囊就静默消失，很难自己查出来。
    /// </summary>
    private void UpdateWhitelistStatus()
    {
        var entries = MediaWhitelist.Parse(TxtMusicWhitelist.Text);
        var app = (App)Application.Current;

        if (!App.Settings.MusicCapsuleEnabled)
        {
            SetWhitelistStatus("音乐胶囊未启用，暂时无法检测来源。", warn: false);
            return;
        }

        var raw = app.GetRawMediaSources();
        var allowed = new List<string>();
        var blocked = new List<string>();
        foreach (var id in raw)
        {
            if (MediaWhitelist.IsAllowed(id, TxtMusicWhitelist.Text)) allowed.Add(MediaSessionService.GetFriendlySourceName(id));
            else blocked.Add(MediaSessionService.GetFriendlySourceName(id));
        }

        if (entries.Count == 0)
        {
            string tail = raw.Count == 0
                ? "当前没有检测到媒体会话。"
                : $"当前检测到 {raw.Count} 个来源：{string.Join("、", allowed.Distinct())}。";
            SetWhitelistStatus("未限制：任何播放器都会接管胶囊。" + tail, warn: false);
            return;
        }

        string head = $"已限制为 {entries.Count} 个播放器";
        if (blocked.Count > 0)
        {
            SetWhitelistStatus(
                $"{head}。注意：{string.Join("、", blocked.Distinct())} 正在播放，但被白名单排除，" +
                "不会显示在胶囊里。要放行就把它加进来。",
                warn: true);
            return;
        }

        if (allowed.Count > 0)
            SetWhitelistStatus($"{head}；当前生效：{string.Join("、", allowed.Distinct())}。", warn: false);
        else
            SetWhitelistStatus($"{head}；当前没有媒体会话在播放。", warn: false);
    }

    private void SetWhitelistStatus(string text, bool warn)
    {
        TxtWhitelistStatus.Text = text;
        TxtWhitelistStatus.Style = (Style)FindResource(warn ? "WarnHint" : "Hint");
    }

    private void OnSystemMonitorChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.SystemMonitorEnabled = ChkSystemMonitor.IsChecked == true;
        Persist();
    }

    // ===================== 诊断 =====================

    private void OnDebugLogChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.EnableDebugLog = ChkDebugLog.IsChecked == true;
        Log.IsDebugEnabled = App.Settings.EnableDebugLog;
        Persist();
        Log.Info("运行调试日志已从界面开启。");
    }

    private void OnOpenLogDir(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Log.LogDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.LogDirectory)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Error($"open log dir failed: {ex.Message}");
            MessageBox.Show($"无法打开日志目录：{ex.Message}", "DeskBar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClearLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            Log.ClearLogs();
            TxtProbe.Text = "日志已清空。";
        }
        catch (Exception ex)
        {
            Log.Error($"clear logs failed: {ex.Message}");
        }
    }

    private void OnToggleIconsNow(object sender, RoutedEventArgs e)
    {
        try
        {
            var visible = App.Icons.Toggle();
            TxtProbe.Text = visible ? "桌面图标当前：可见" : "桌面图标当前：已隐藏";
        }
        catch (Exception ex)
        {
            Log.Error($"toggle icons failed: {ex.Message}");
            TxtProbe.Text = $"切换失败：{ex.Message}";
        }
    }

    private void OnProbeWindows(object sender, RoutedEventArgs e)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"OS Build: {Environment.OSVersion.Version.Build}");
            sb.AppendLine($"管理员: {(NativeTaskbarAppearanceService.IsElevated ? "是" : "否")}");
            sb.AppendLine($"任务栏注入: {(NativeTaskbarAppearanceService.IsAvailable ? "可用" : "不可用")}");
            sb.AppendLine($"系统深浅色: {(App.Theme?.GetSystemUsesLightTheme() == true ? "Light" : "Dark")}");
            sb.AppendLine($"AppsUseLightTheme: {(App.Theme?.GetAppsUseLightTheme() == true ? "1" : "0")}");
            sb.AppendLine($"当前时段: {(App.Theme?.IsDayNow == true ? "白天" : "夜晚")}");
            sb.AppendLine($"生效色调: {ToneDisplayName(App.Settings.EffectiveTone)} ({App.Settings.EffectiveTone})");
            sb.AppendLine($"任务栏风格: {App.Settings.TaskbarStyle}");
            sb.AppendLine($"Progman: 0x{DeskBar.Interop.DesktopWindows.FindProgman():X}");
            sb.AppendLine($"Shell_TrayWnd: 0x{DeskBar.Interop.NativeMethods.FindWindowW("Shell_TrayWnd", null):X}");
            TxtProbe.Text = sb.ToString();
        }
        catch (Exception ex)
        {
            Log.Error($"probe failed: {ex.Message}");
            TxtProbe.Text = $"探测失败：{ex.Message}";
        }
    }

    // ===================== 检查更新 =====================

    private bool _checkingUpdate;

    /// <summary>最近一次检查得到的安装包地址与 Release 页地址，供下载 / 打开按钮使用。</summary>
    private string _lastAssetUrl = "";

    private string _lastReleasePageUrl = "";

    /// <summary>最近一次检查解析出的原始（未加速）资产地址，用于预览改写结果。</summary>
    private string _realAssetUrl = "";

    /// <summary>最近一次检查得到的最新版本号，下载后写入临时目录名。</summary>
    private string _lastLatestVersion = "";

    /// <summary>是否正在执行覆盖更新，防止重复点击。</summary>
    private bool _installingUpdate;

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        if (_checkingUpdate) return;
        _checkingUpdate = true;
        BtnCheckUpdate.IsEnabled = false;
        TxtUpdateStatus.Text = "正在检查…";
        try
        {
            var result = await UpdateService.CheckAsync(App.Settings.UpdateAccelPrefix).ConfigureAwait(true);
            ApplyUpdateResult(result);
        }
        finally
        {
            _checkingUpdate = false;
            BtnCheckUpdate.IsEnabled = true;
        }
    }

    private void ApplyUpdateResult(UpdateService.CheckResult r)
    {
        if (r.Error.Length > 0)
        {
            TxtUpdateStatus.Text = r.Error;
            TxtUpdateStatus.Style = (Style)FindResource("WarnHint");
            _lastAssetUrl = "";
            _lastReleasePageUrl = "";
            _lastLatestVersion = "";
            BranchUpdateButtons(enabled: false);
            BtnOpenReleasePage.IsEnabled = false;
            return;
        }

        TxtUpdateStatus.Style = (Style)FindResource("Hint");
        _lastAssetUrl = r.AssetUrl;
        _lastReleasePageUrl = r.ReleasePageUrl;
        _lastLatestVersion = r.LatestVersion;

        if (r.HasUpdate)
        {
            TxtUpdateStatus.Text = $"发现新版本 v{r.LatestVersion}（当前 v{r.CurrentVersion}）";
            BranchUpdateButtons(enabled: r.AssetUrl.Length > 0);
            BtnDownloadUpdate.Content = "仅下载安装包";
            BtnOpenReleasePage.IsEnabled = r.ReleasePageUrl.Length > 0;
        }
        else
        {
            TxtUpdateStatus.Text = $"已是最新版本（v{r.CurrentVersion}）。";
            BranchUpdateButtons(enabled: false);
            BtnDownloadUpdate.Content = "仅下载安装包";
            BtnOpenReleasePage.IsEnabled = r.ReleasePageUrl.Length > 0;
        }

        TxtUpdateNotes.Text = r.Notes.Length > 0 ? r.Notes : "该版本没有提供发行说明。";

        // 检查到真实资产后，预览改用真实地址，别再显示静态样例
        if (r.AssetUrl.Length > 0) _realAssetUrl = r.AssetUrl;
        UpdateAccelPreview();

        Log.Info($"update check: latest={r.LatestVersion} current={r.CurrentVersion} hasUpdate={r.HasUpdate}");
    }

    private void OnAutoCheckUpdateChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.AutoCheckUpdate = ChkAutoCheckUpdate.IsChecked == true;
        Persist();
    }

    private void OnAccelPrefixChanged(object sender, TextChangedEventArgs e)
    {
        UpdateAccelPreview();
        if (_loading) return;
        App.Settings.UpdateAccelPrefix = TxtAccelPrefix.Text.Trim();
        Persist();
    }

    /// <summary>把当前前缀与一个示例 GitHub 地址拼出来，让用户直接看到改写效果。</summary>
    private void UpdateAccelPreview()
    {
        // 优先用检查更新拿到的真实资产地址；还没检查过时用当前版本拼一个形状相同的样例
        var sample = _realAssetUrl.Length > 0
            ? _realAssetUrl
            : $"https://github.com/buyikun/DeskBar/releases/download/v{UpdateService.CurrentVersion}/DeskBar-v{UpdateService.CurrentVersion}-win-x64.zip";
        var prefix = TxtAccelPrefix.Text.Trim();
        if (prefix.Length == 0)
        {
            TxtAccelPreview.Text = "当前为直连模式：" + sample;
            return;
        }
        var applied = UpdateService.ApplyAccel(sample, prefix);
        TxtAccelPreview.Text = "实际下载地址：" + applied;
    }

    private void OnResetAccelPrefix(object sender, RoutedEventArgs e)
    {
        TxtAccelPrefix.Text = UpdateService.DefaultAccelPrefix;
    }

    private void OnUseDirectGitHub(object sender, RoutedEventArgs e)
    {
        TxtAccelPrefix.Text = "";
    }

    private async void OnDownloadUpdate(object sender, RoutedEventArgs e)
    {
        if (BtnDownloadUpdate.IsEnabled == false) return;
        var url = _lastAssetUrl;
        if (url.Length == 0) return;
        await OpenUrlAsync(url);
    }

    /// <summary>
    /// 覆盖式更新：下载 → 解压到临时目录 → 起外部脚本等本进程退出后替换文件 → 重启。
    /// 用户确认后程序会自动关闭并重新打开，配置与日志不受影响。
    /// </summary>
    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (_installingUpdate) return;
        var url = _lastAssetUrl;
        if (url.Length == 0) return;

        var confirm = MessageBox.Show(
            "将下载并安装新版本。\n\n安装过程中 DeskBar 会自动关闭，替换完成后重新启动。\n设置与日志不会丢失。",
            "确认更新", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        _installingUpdate = true;
        SetUpdateBusy(true);
        try
        {
            var result = await UpdateService.DownloadAsync(url, _lastLatestVersion, UpdateProgress);
            if (result.Error.Length > 0)
            {
                ShowUpdateError(result.Error);
                return;
            }

            var install = UpdateService.StartInstall(result.StagedDir, result.TempRoot);
            if (install.Error.Length > 0)
            {
                ShowUpdateError(install.Error);
                return;
            }

            TxtUpdateProgress.Text = "更新程序已启动，DeskBar 即将关闭并自动完成替换…";
            TxtUpdateProgress.Visibility = Visibility.Visible;
            Log.Info("update: apply script started, shutting down for replacement");

            // 延迟 1.2 秒退出，让脚本有机会先观察到本进程存在，再等它消失
            await Task.Delay(1200);
            Close();
            App.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Warn($"install update failed: {ex.Message}");
            ShowUpdateError("安装失败：" + ex.Message);
        }
        finally
        {
            _installingUpdate = false;
            SetUpdateBusy(false);
        }
    }

    /// <summary>下载进度回调（可能来自后台线程，需切回 UI）。</summary>
    private void UpdateProgress(string phase, int percent)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            TxtUpdateProgress.Text = phase;
            TxtUpdateProgress.Visibility = Visibility.Visible;
            BarUpdate.Visibility = Visibility.Visible;
            if (percent >= 0)
            {
                BarUpdate.IsIndeterminate = false;
                BarUpdate.Value = Math.Clamp(percent, 0, 100);
            }
            else
            {
                // 加速站不返回 Content-Length 时用不确定进度
                BarUpdate.IsIndeterminate = true;
            }
        }));
    }

    private void SetUpdateBusy(bool busy)
    {
        BranchUpdateButtons(enabled: !busy && _lastAssetUrl.Length > 0);
        BarUpdate.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BarUpdate.Value = 0;
        BarUpdate.IsIndeterminate = false;
        if (!busy)
        {
            TxtUpdateProgress.Visibility = Visibility.Collapsed;
            TxtUpdateProgress.Style = (Style)FindResource("Hint");
        }
    }

    /// <summary>「立即更新」与「仅下载安装包」总有资产时一并可用/禁用。</summary>
    private void BranchUpdateButtons(bool enabled)
    {
        BtnInstallUpdate.IsEnabled = enabled;
        BtnDownloadUpdate.IsEnabled = enabled;
    }

    private void ShowUpdateError(string message)
    {
        TxtUpdateProgress.Text = message;
        TxtUpdateProgress.Visibility = Visibility.Visible;
        TxtUpdateProgress.Style = (Style)FindResource("WarnHint");
        TxtUpdateStatus.Text = message;
        TxtUpdateStatus.Style = (Style)FindResource("WarnHint");
        BarUpdate.Visibility = Visibility.Collapsed;
        Log.Warn("update: " + message);
    }

    private void OnOpenReleasePage(object sender, RoutedEventArgs e)
    {
        var url = _lastReleasePageUrl;
        if (url.Length == 0) return;
        _ = OpenUrlAsync(url);
    }

    private static async Task OpenUrlAsync(string url)
    {
        try
        {
            await Task.Run(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true
            }));
        }
        catch (Exception ex)
        {
            Log.Warn($"open url failed: {ex.Message}");
        }
    }

    // ===================== 底部操作 =====================

    private void OnHideToTray(object sender, RoutedEventArgs e)
    {
        Hide();
        App.TrimMemory();
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
        App.TrimMemory();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _statusTimer.Stop();
        e.Cancel = true;
        Hide();
        App.TrimMemory();
    }
}

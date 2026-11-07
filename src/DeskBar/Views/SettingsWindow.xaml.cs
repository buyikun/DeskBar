using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using System.Windows.Controls;
using DeskBar.Services;

namespace DeskBar.Views;

public partial class SettingsWindow : Window
{
    private bool _loading;

    public SettingsWindow()
    {
        _loading = true;
        InitializeComponent();
        LoadFromSettings();
        ShowPage(PageGeneral);
        TxtVersion.Text = "v" + (System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString(3) ?? "0.1.0");
    }

    /// <summary>切换右侧内容页；只显示一个，其余折叠。</summary>
    private void ShowPage(UIElement page)
    {
        foreach (var p in new UIElement[] { PageGeneral, PageTaskbar, PageTheme, PageWidgets, PageDiag })
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
            else if (sender == NavTaskbar) ShowPage(PageTaskbar);
            else if (sender == NavTheme) ShowPage(PageTheme);
            else if (sender == NavWidgets) ShowPage(PageWidgets);
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
        // 用户可能刚切了系统深浅色，标题栏颜色跟着走
        SyncTitleBarTheme();
    }

    /// <summary>外部（App.ApplyAppTheme）切换深浅色后调用，刷新标题栏与状态文本。</summary>
    internal void RefreshThemeVisuals()
    {
        SyncTitleBarTheme();
        UpdateThemeStatus();
    }

    private void SyncTitleBarTheme()
    {
        try
        {
            var bg = (System.Windows.Media.SolidColorBrush)FindResource("WindowBackground");
            var c = bg.Color;
            double luma = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
            AcrylicWindowHelper.SetDarkTitleBar(this, luma < 128);
        }
        catch { }
    }

    private void LoadFromSettings()
    {
        _loading = true;
        var s = App.Settings;

        ChkDoubleClick.IsChecked = s.DoubleClickToggle;
        ChkShowTray.IsChecked = s.ShowTrayIcon;
        ChkTranslucentTaskbar.IsChecked = s.TranslucentTaskbar;

        foreach (var obj in CmbTaskbarStyle.Items)
        {
            if (obj is ComboBoxItem item && (string?)item.Tag == s.TaskbarStyle)
            {
                CmbTaskbarStyle.SelectedItem = item;
                break;
            }
        }
        if (CmbTaskbarStyle.SelectedItem is null)
        {
            CmbTaskbarStyle.SelectedIndex = 0;
            // island_clear（“部分透明”）与 clear 效果重复，已从选项里移除；旧配置回落到部分亚克力
            s.TaskbarStyle = "island_acrylic";
        }

        foreach (var obj in CmbAcrylicTone.Items)
        {
            if (obj is ComboBoxItem item && (string?)item.Tag == s.AcrylicTone)
            {
                CmbAcrylicTone.SelectedItem = item;
                break;
            }
        }
        if (CmbAcrylicTone.SelectedItem is null) CmbAcrylicTone.SelectedIndex = 0;

        ChkStartup.IsChecked = StartupService.IsEnabled();
        ChkShowTray.IsChecked = s.ShowTrayIcon;
        ChkDebugLog.IsChecked = s.EnableDebugLog;
        ChkMusicCapsule.IsChecked = s.MusicCapsuleEnabled;
        ChkMusicAutoHide.IsChecked = s.MusicCapsuleAutoHide;
        ChkMusicControls.IsChecked = s.MusicCapsuleShowControls;
        TxtMusicWhitelist.Text = s.MusicCapsuleWhitelist;
        ChkSystemMonitor.IsChecked = s.SystemMonitorEnabled;

        ChkAutoTimeTheme.IsChecked = s.AutoTimeTheme;
        TxtLightTime.Text = s.LightStartTime;
        TxtDarkTime.Text = s.DarkStartTime;

        UpdateThemeStatus();
        _loading = false;
    }

    private void UpdateThemeStatus()
    {
        var light = App.Theme.GetSystemUsesLightTheme();
        var auto = App.Settings.AutoTimeTheme;
        TxtThemeStatus.Text = $"当前系统：{(light ? "浅色模式" : "深色模式")} | 定时自动切换：{(auto ? "已开启" : "未开启")}";
    }

    private void Persist() => ((App)Application.Current).SaveSettings();

    private void OnMusicCapsuleChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.MusicCapsuleEnabled = ChkMusicCapsule.IsChecked == true;
        Persist();
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

    private void OnMusicWhitelistChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        App.Settings.MusicCapsuleWhitelist = TxtMusicWhitelist.Text ?? "";
        Persist();
    }

    private void OnSystemMonitorChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.SystemMonitorEnabled = ChkSystemMonitor.IsChecked == true;
        Persist();
    }

    private void OnDoubleClickChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.DoubleClickToggle = ChkDoubleClick.IsChecked == true;
        Persist();
    }

    private void OnTranslucentTaskbarChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.TranslucentTaskbar = ChkTranslucentTaskbar.IsChecked == true;
        Persist();
    }

    private void OnTaskbarStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (CmbTaskbarStyle.SelectedItem is not ComboBoxItem item) return;
        App.Settings.TaskbarStyle = (string?)item.Tag ?? "island_acrylic";
        Persist();
        if (App.Settings.TranslucentTaskbar)
        {
            TaskbarService.ApplyStyle(App.Settings.TaskbarStyle, App.Settings.AcrylicTone);
        }
        // 系统信息浮岛与音乐胶囊的背景跟随任务栏风格 / 亚克力色调一起变
        ((App)Application.Current).RefreshWidgetBackgrounds();
    }

    private void OnAcrylicToneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (CmbAcrylicTone.SelectedItem is not ComboBoxItem item) return;
        App.Settings.AcrylicTone = (string?)item.Tag ?? "natural";
        Persist();
        if (App.Settings.TranslucentTaskbar)
        {
            TaskbarService.ApplyStyle(App.Settings.TaskbarStyle, App.Settings.AcrylicTone);
        }
        // 系统信息浮岛与音乐胶囊的背景跟随任务栏风格 / 亚克力色调一起变
        ((App)Application.Current).RefreshWidgetBackgrounds();
    }

    private void OnAutoTimeThemeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.AutoTimeTheme = ChkAutoTimeTheme.IsChecked == true;
        Persist();
        UpdateThemeStatus();
    }

    private void OnTimeConfigChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        App.Settings.LightStartTime = TxtLightTime.Text.Trim();
        App.Settings.DarkStartTime = TxtDarkTime.Text.Trim();
        Persist();
    }

    private void OnManualSwitchLight(object sender, RoutedEventArgs e)
    {
        App.Theme.SetTheme(true);
        UpdateThemeStatus();
    }

    private void OnManualSwitchDark(object sender, RoutedEventArgs e)
    {
        App.Theme.SetTheme(false);
        UpdateThemeStatus();
    }

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
        ((App)Application.Current).Tray?.SetVisible(App.Settings.ShowTrayIcon);
    }

    private void OnDebugLogChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.EnableDebugLog = ChkDebugLog.IsChecked == true;
        Log.IsDebugEnabled = App.Settings.EnableDebugLog;
        Persist();
        if (App.Settings.EnableDebugLog)
        {
            Log.Info("运行调试日志已从界面开启。");
        }
    }

    private void OnOpenLogDir(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Log.LogDirectory;
            if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开日志目录失败: {ex.Message}", "DeskBar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClearLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            Log.ClearLogs();
            MessageBox.Show("日志文件已全部清空！", "DeskBar", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"清空日志失败: {ex.Message}", "DeskBar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnToggleIconsNow(object sender, RoutedEventArgs e)
    {
        var v = App.Icons.Toggle();
        TxtProbe.Text = v ? "图标当前：可见" : "图标当前：已隐藏";
    }

    private void OnProbeWindows(object sender, RoutedEventArgs e)
    {
        var sb = new System.Text.StringBuilder();
        var progman = Interop.DesktopWindows.FindProgman();
        sb.AppendLine($"Progman        = 0x{progman:X8}");
        var defView = Interop.DesktopWindows.FindDefView();
        sb.AppendLine($"SHELLDLL_DefView= 0x{defView:X8}  visible={Interop.NativeMethods.IsWindowVisible(defView)}");
        var lv = Interop.DesktopWindows.FindDesktopListView();
        sb.AppendLine($"SysListView32   = 0x{lv:X8}  visible={Interop.NativeMethods.IsWindowVisible(lv)}");
        var tray = Interop.NativeMethods.FindWindowW("Shell_TrayWnd", null);
        sb.AppendLine($"Shell_TrayWnd   = 0x{tray:X8}");
        sb.AppendLine($"TaskbarEngine   = 原生COM驱动 (ExplorerTAP)");
        sb.AppendLine($"SystemLight     = {App.Theme.GetSystemUsesLightTheme()}");
        sb.AppendLine($"OS Build        = {Environment.OSVersion.Version.Build}");
        TxtProbe.Text = sb.ToString();
        Log.Info("probe windows:\n" + sb);
    }

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
        e.Cancel = true;
        Hide();
        App.TrimMemory();
    }
}

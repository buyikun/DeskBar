using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DeskBar.Services;

namespace DeskBar.Views;

/// <summary>点击时创建、失去激活即关闭的设备全列表；无常驻计时器。</summary>
internal sealed class WirelessBatteryDetailWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Border _root;
    private readonly StackPanel _deviceList;
    private readonly TextBlock _title;
    private readonly TextBlock _status;
    private readonly Button _refreshButton;
    private readonly Button _closeButton;
    private readonly Button _hiddenToggleButton;
    private WirelessBatteryService? _service;
    private Action<IReadOnlyList<WirelessBatteryDevice>>? _devicesChangedHandler;
    private int _serviceVersion;
    private bool _closed;
    private bool _editing;
    private bool _showHidden;
    private bool _confirmingRemoval;

    internal WirelessBatteryDetailWindow(AppSettings settings, WirelessBatteryService? service)
    {
        _settings = settings;
        Title = "无线设备电量";
        Width = 340;
        Height = 360;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        // 自有简短模板，避免系统默认按钮用另一套颜色盖住面板材质。
        var buttonTemplate = new ControlTemplate(typeof(Button));
        var buttonBorder = new FrameworkElementFactory(typeof(Border));
        buttonBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        buttonBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        buttonBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        buttonBorder.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        buttonBorder.AppendChild(presenter);
        buttonTemplate.VisualTree = buttonBorder;
        var buttonStyle = new Style(typeof(Button));
        buttonStyle.Setters.Add(new Setter(Control.TemplateProperty, buttonTemplate));
        buttonStyle.Setters.Add(new Setter(Control.BackgroundProperty, new System.Windows.DynamicResourceExtension("WirelessActionBrush")));
        buttonStyle.Setters.Add(new Setter(Control.BorderBrushProperty, new System.Windows.DynamicResourceExtension("WirelessActionBorderBrush")));
        buttonStyle.Setters.Add(new Setter(Control.ForegroundProperty, new System.Windows.DynamicResourceExtension("WirelessTextBrush")));
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, new System.Windows.DynamicResourceExtension("WirelessActionHoverBrush")));
        buttonStyle.Triggers.Add(hover);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Control.BackgroundProperty, new System.Windows.DynamicResourceExtension("WirelessActionPressedBrush")));
        buttonStyle.Triggers.Add(pressed);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, 0.55));
        buttonStyle.Triggers.Add(disabled);

        _deviceList = new StackPanel();
        _status = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        _title = new TextBlock { Text = "无线设备电量", FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _refreshButton = new Button { Content = "刷新", Width = 64, Height = 28, Focusable = false, Cursor = Cursors.Hand, Style = buttonStyle };
        _hiddenToggleButton = new Button { Content = "已隐藏设备", Width = 88, Height = 28, Focusable = false, Cursor = Cursors.Hand, Style = buttonStyle, Margin = new Thickness(0, 0, 8, 0) };
        _closeButton = new Button { Content = "×", Width = 28, Height = 28, FontSize = 16, Focusable = false, Cursor = Cursors.Hand, Style = buttonStyle };
        var heading = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(_title);
        Grid.SetColumn(_closeButton, 1);
        heading.Children.Add(_closeButton);
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(_refreshButton, Dock.Right);
        footer.Children.Add(_refreshButton);
        DockPanel.SetDock(_hiddenToggleButton, Dock.Left);
        footer.Children.Add(_hiddenToggleButton);
        footer.Children.Add(new TextBlock { Text = "仅显示设备实际报告的电量", FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
        var body = new Grid { Margin = new Thickness(16, 13, 16, 13) };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition());
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.Children.Add(heading);
        var scroll = new ScrollViewer { Content = _deviceList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false };
        Grid.SetRow(scroll, 1);
        body.Children.Add(scroll);
        Grid.SetRow(_status, 2);
        body.Children.Add(_status);
        Grid.SetRow(footer, 3);
        body.Children.Add(footer);
        _root = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = body };
        Content = _root;
        _hiddenToggleButton.Click += (_, _) => { _showHidden = !_showHidden; RenderDevices(_service?.CurrentDevices ?? Array.Empty<WirelessBatteryDevice>()); };
        _closeButton.Click += (_, _) => RequestClose();
        _refreshButton.Click += async (_, _) => await RefreshDevicesAsync();
        Deactivated += (_, _) =>
        {
            if (!_confirmingRemoval) RequestClose();
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; RequestClose(); } };
        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) =>
        {
            _closed = true;
            ++_serviceVersion;
            if (_service != null && _devicesChangedHandler != null) _service.DevicesChanged -= _devicesChangedHandler;
            _service = null;
            _devicesChangedHandler = null;
            _deviceList.Children.Clear();
        };
        RefreshTonePalette();
        AttachService(service);
    }

    internal void AttachService(WirelessBatteryService? service)
    {
        if (_closed) return;
        if (_service != null && _devicesChangedHandler != null) _service.DevicesChanged -= _devicesChangedHandler;
        _service = service;
        int version = ++_serviceVersion;
        _devicesChangedHandler = devices => QueueDevices(devices, version);
        if (service != null) service.DevicesChanged += _devicesChangedHandler;
        QueueDevices(service?.CurrentDevices ?? Array.Empty<WirelessBatteryDevice>(), version);
    }

    /// <param name="capsuleRect">主任务栏胶囊的屏幕 DIP 矩形。</param>
    internal void ShowAtCapsule(Rect capsuleRect)
    {
        if (_closed) return;
        var work = SystemParameters.WorkArea;
        Width = Math.Min(340, work.Width);
        Height = Math.Min(360, work.Height);
        double top = capsuleRect.Top - Height - 8;
        if (top < work.Top) top = capsuleRect.Bottom + 8;
        Left = Math.Clamp(capsuleRect.Left, work.Left, Math.Max(work.Left, work.Right - Width));
        Top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - Height));
        Show();
        AcrylicWindowHelper.BindToTaskbarLayer(this);
        Activate();
    }

    internal void RefreshTonePalette()
    {
        if (_closed) return;
        string tone = _settings.EffectiveTone;
        _root.Background = TonePalette.PanelBrush(tone);
        _root.BorderBrush = TonePalette.PanelBorderBrush(tone);
        Foreground = TonePalette.PanelTextSecondaryBrush(tone);
        _title.Foreground = TonePalette.PanelTextPrimaryBrush(tone);
        _status.Foreground = TonePalette.PanelTextMutedBrush(tone);
        Resources["WirelessTextBrush"] = TonePalette.PanelTextPrimaryBrush(tone);
        Resources["WirelessActionBrush"] = TonePalette.PanelActionBrush(tone);
        Resources["WirelessActionBorderBrush"] = TonePalette.PanelActionBorderBrush(tone);
        Resources["WirelessActionHoverBrush"] = TonePalette.PanelActionHoverBrush(tone);
        Resources["WirelessActionPressedBrush"] = TonePalette.PanelActionPressedBrush(tone);
        AcrylicWindowHelper.Apply(this, tone, "wireless-battery-detail", _settings.TaskbarBlurAmount, forceMaterial: true);
        RenderDevices(_service?.CurrentDevices ?? Array.Empty<WirelessBatteryDevice>());
    }

    private void RequestClose()
    {
        if (_closed || !IsVisible) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_closed && IsVisible) Close();
        }), System.Windows.Threading.DispatcherPriority.Normal);
    }

    private async Task RefreshDevicesAsync()
    {
        if (_closed || _service == null || !_refreshButton.IsEnabled) return;
        var service = _service;
        int version = _serviceVersion;
        _refreshButton.IsEnabled = false;
        _refreshButton.Content = "刷新中";
        string? error = null;
        try { await service.RefreshAsync(); }
        catch (Exception) { error = "刷新未完成，请稍后重试"; }
        if (_closed || version != _serviceVersion) return;
        _refreshButton.IsEnabled = true;
        _refreshButton.Content = "刷新";
        RenderDevices(service.CurrentDevices);
        if (error != null) _status.Text = error;
    }

    private void QueueDevices(IReadOnlyList<WirelessBatteryDevice> devices, int version)
    {
        if (_closed || version != _serviceVersion) return;
        void Apply() { if (!_closed && version == _serviceVersion) RenderDevices(devices); }
        if (Dispatcher.CheckAccess()) Apply();
        else if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
        {
            try { Dispatcher.BeginInvoke(new Action(Apply)); }
            catch (InvalidOperationException) { /* Dispatcher 已关闭。 */ }
        }
    }

    private void RenderDevices(IReadOnlyList<WirelessBatteryDevice> devices)
    {
        if (_closed || _editing) return;
        _deviceList.Children.Clear();
        string tone = _settings.EffectiveTone;
        _hiddenToggleButton.Content = _showHidden ? "返回设备" : "不显示的设备";
        // 「不显示」页只列 DisplayMode=never 的设备；其余（常显 / 连接时）在主列表。
        var displayedDevices = devices
            .Where(device => (device.DisplayMode == WirelessBatteryDisplayModes.Never) == _showHidden)
            .ToArray();
        _title.Text = _showHidden ? "不显示的设备" : "无线设备电量";
        foreach (var device in displayedDevices)
        {
            bool known = device.Percent is >= 0 and <= 100;
            Brush primary = TonePalette.PanelTextPrimaryBrush(tone);
            Brush muted = TonePalette.PanelTextMutedBrush(tone);
            Brush valueBrush = known && device.Percent <= 15 ? TonePalette.MetricSparkBrush(tone) : primary;
            string displayName = _settings.GetWirelessBatteryName(device.Id, device.Name);
            string effectiveType = _settings.GetWirelessBatteryType(device.Id, device.Type);
            string selectedType = _settings.WirelessBatteryDeviceTypes.TryGetValue(device.Id, out var savedType) ? savedType : "auto";
            var cardContent = new StackPanel();
            var summary = new Grid { MinHeight = 40 };
            summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            summary.ColumnDefinitions.Add(new ColumnDefinition());
            summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
            summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            summary.Children.Add(new Border
            {
                Width = 30, Height = 34, CornerRadius = new CornerRadius(8),
                HorizontalAlignment = HorizontalAlignment.Left, Background = TonePalette.PanelActionBrush(tone),
                Child = new TextBlock { Text = WirelessBatteryCapsuleWindow.GetDeviceGlyph(effectiveType), FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 18, Foreground = primary, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            });
            string state = !device.ConnectionKnown ? "状态未知" : device.IsConnected ? "已连接 · " + device.Connection : "已断开 · " + device.Connection;
            if (device.IsStale) state += " · 上次读取";
            else if (!known) state += " · 未提供电量";
            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            labels.Children.Add(new TextBlock { Text = displayName, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = primary, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = displayName });
            labels.Children.Add(new TextBlock { Text = state, FontSize = 10, Foreground = muted, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = state });
            Grid.SetColumn(labels, 1);
            summary.Children.Add(labels);
            var reading = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            reading.Children.Add(new TextBlock { Text = known ? $"{device.Percent}%" : "--", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = valueBrush, TextAlignment = TextAlignment.Right });
            var track = new Border { Width = 44, Height = 3, CornerRadius = new CornerRadius(1.5), Background = TonePalette.PanelTrackBrush(tone), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            if (known) track.Child = new Border { Width = device.Percent * 0.44, Height = 3, CornerRadius = new CornerRadius(1.5), Background = valueBrush, HorizontalAlignment = HorizontalAlignment.Left };
            reading.Children.Add(track);
            Grid.SetColumn(reading, 2);
            summary.Children.Add(reading);
            var editButton = new Button { Content = new TextBlock { Text = "编辑", FontSize = 10 }, Width = 28, Height = 28, Focusable = false, Cursor = Cursors.Hand, Style = _refreshButton.Style, Background = Brushes.Transparent, BorderThickness = new Thickness(0), BorderBrush = Brushes.Transparent, ToolTip = "修改名称和设备图标", VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(editButton, 3);
            summary.Children.Add(editButton);
            cardContent.Children.Add(summary);

            // 表单只在编辑时出现；常态列表只保留设备信息。
            var editor = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
            editor.Children.Add(new Border { Height = 1, Background = TonePalette.PanelCardBorderBrush(tone), Margin = new Thickness(0, 0, 0, 12) });
            editor.Children.Add(new TextBlock { Text = "显示名称", FontSize = 11, Foreground = muted, Margin = new Thickness(0, 0, 0, 6) });
            var nameBox = new TextBox { Text = _settings.GetWirelessBatteryName(device.Id, string.Empty), FontSize = 12, Height = 30, Padding = new Thickness(8, 4, 8, 4), Foreground = primary, Background = TonePalette.PanelActionBrush(tone), BorderBrush = TonePalette.PanelActionBorderBrush(tone), BorderThickness = new Thickness(1), ToolTip = "留空恢复设备原名", MaxLength = 80 };
            System.Windows.Automation.AutomationProperties.SetName(nameBox, "显示名称");
            editor.Children.Add(nameBox);
            editor.Children.Add(new TextBlock { Text = "设备图标", FontSize = 11, Foreground = muted, Margin = new Thickness(0, 12, 0, 6) });
            var types = new WrapPanel();
            var choices = new List<(Button Button, string Type)>();
            void ApplyTypeSelection()
            {
                foreach (var choice in choices)
                {
                    bool selected = choice.Type == selectedType;
                    choice.Button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
                    choice.Button.BorderBrush = selected ? primary : TonePalette.PanelActionBorderBrush(tone);
                    choice.Button.Background = selected ? TonePalette.PanelActionPressedBrush(tone) : TonePalette.PanelActionBrush(tone);
                }
            }
            foreach (var (label, value) in new[] { ("自动", "auto"), ("耳机", "headset"), ("键盘", "keyboard"), ("鼠标", "mouse"), ("手柄", "controller"), ("其他", "other") })
            {
                var choice = new Button { Content = label, Width = 78, Height = 28, Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand, Style = _refreshButton.Style, FontSize = 11 };
                choice.Click += (_, _) => { selectedType = value; ApplyTypeSelection(); };
                choices.Add((choice, value));
                types.Children.Add(choice);
            }
            ApplyTypeSelection();
            editor.Children.Add(types);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
            var cancelButton = new Button { Content = "取消", Width = 58, Height = 28, Cursor = Cursors.Hand, Style = _refreshButton.Style };
            var saveButton = new Button { Content = "保存", Width = 64, Height = 28, Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand, Style = _refreshButton.Style, FontWeight = FontWeights.SemiBold };
            actions.Children.Add(cancelButton);
            actions.Children.Add(saveButton);
            editor.Children.Add(actions);
            cardContent.Children.Add(editor);
            editButton.Click += (_, _) =>
            {
                if (_editing) return;
                _editing = true;
                editButton.Visibility = Visibility.Collapsed;
                editor.Visibility = Visibility.Visible;
                nameBox.Focus();
                nameBox.SelectAll();
                nameBox.BringIntoView();
            };
            saveButton.Click += (_, _) =>
            {
                _settings.SetWirelessBatteryName(device.Id, nameBox.Text);
                _settings.SetWirelessBatteryType(device.Id, selectedType);
                ((App)Application.Current).SaveSettings();
                _editing = false;
                RenderDevices(_service?.CurrentDevices ?? Array.Empty<WirelessBatteryDevice>());
            };
            cancelButton.Click += (_, _) =>
            {
                _editing = false;
                RenderDevices(_service?.CurrentDevices ?? Array.Empty<WirelessBatteryDevice>());
            };
            var management = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            var visibilityButton = new Button
            {
                // 三态循环按钮：显示当前模式，点一下切到下一个。
                Content = WirelessBatteryDisplayModes.Label(device.DisplayMode),
                Width = 68, Height = 26, Cursor = Cursors.Hand, Style = _refreshButton.Style, FontSize = 11,
                ToolTip = "显示方式，点击循环切换：\n连接时（断开就隐藏，默认）→ 常显（断开也显示最后电量）→ 不显示\n永久移除请用「移除」。"
            };
            visibilityButton.Click += (_, _) => _service?.SetDisplayMode(device.Id, NextDisplayMode(device.DisplayMode));
            var removeButton = new Button { Content = "移除", Width = 56, Height = 26, Margin = new Thickness(6, 0, 0, 0), Cursor = Cursors.Hand, Style = _refreshButton.Style, FontSize = 11, ToolTip = "从历史记录中移除此设备" };
            removeButton.Click += (_, _) =>
            {
                _confirmingRemoval = true;
                MessageBoxResult answer;
                try { answer = MessageBox.Show(this, $"移除“{displayName}”的电量历史和自定义设置？下次重新检测到设备时，它会重新加入列表。", "移除无线设备", MessageBoxButton.YesNo, MessageBoxImage.Warning); }
                finally { _confirmingRemoval = false; }
                if (answer == MessageBoxResult.Yes) _service?.RemoveDevice(device.Id);
            };
            management.Children.Add(visibilityButton);
            management.Children.Add(removeButton);
            cardContent.Children.Add(management);
            _deviceList.Children.Add(new Border { Background = TonePalette.PanelCardBrush(tone), BorderBrush = TonePalette.PanelCardBorderBrush(tone), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 8), Opacity = device.IsStale ? 0.7 : 1, Child = cardContent });
        }
        _status.Text = _showHidden ? $"设为不显示 {displayedDevices.Length} 台设备" : _service?.StatusText ?? "电量采集服务不可用";
        if (displayedDevices.Length == 0)
            _deviceList.Children.Add(new TextBlock { Text = _showHidden ? "没有设为不显示的设备。" : devices.Count == 0 ? "暂未发现可读取电量的无线设备。连接设备后点击刷新。" : "没有显示中的设备。可在“不显示的设备”中改回来。", FontSize = 12, Foreground = TonePalette.PanelTextSecondaryBrush(tone), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 10, 2, 2) });
    }

    /// <summary>三态循环：连接时 → 常显 → 不显示 → 连接时。</summary>
    private static string NextDisplayMode(string? current) => WirelessBatteryDisplayModes.Normalize(current) switch
    {
        WirelessBatteryDisplayModes.Connected => WirelessBatteryDisplayModes.Always,
        WirelessBatteryDisplayModes.Always => WirelessBatteryDisplayModes.Never,
        _ => WirelessBatteryDisplayModes.Connected
    };

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
        AcrylicWindowHelper.EnableRoundedCorners(this);
        AcrylicWindowHelper.RemoveWindowBorder(this);
        RefreshTonePalette();
    }
}

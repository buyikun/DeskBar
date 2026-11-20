using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DeskBar.Interop;
using DeskBar.Services;

namespace DeskBar.Views;

/// <summary>任务栏同层的无线设备摘要；位置由 App 现有几何回调驱动。</summary>
internal sealed class WirelessBatteryCapsuleWindow : Window
{
    private const double HeightDip = 44;
    private const int GapPx = 6;
    private readonly AppSettings _settings;
    private readonly Border _root;
    private readonly StackPanel _items;
    private readonly TextBlock _emptyLabel;
    private WirelessBatteryService? _service;
    private Action<IReadOnlyList<WirelessBatteryDevice>>? _devicesChangedHandler;
    private IReadOnlyList<WirelessBatteryDevice> _devices = Array.Empty<WirelessBatteryDevice>();
    private WirelessBatteryDetailWindow? _detailWindow;
    private Rect? _monitorBoundsPx;
    private DateTime _taskbarUnavailableSince = DateTime.MinValue;
    private bool _closed;
    private int _serviceVersion;
    private int _renderedCount = -1;
    private int _lastX = int.MinValue, _lastY = int.MinValue, _lastWidth, _lastHeight;

    internal WirelessBatteryCapsuleWindow(AppSettings settings)
    {
        _settings = settings;
        Title = "DeskBarWirelessBattery";
        Width = 80;
        Height = HeightDip;
        Left = Top = -32000;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = !AcrylicWindowHelper.IsIslandStyle();
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        _items = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _emptyLabel = new TextBlock { Text = "无线 --", FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var content = new Grid();
        content.Children.Add(_items);
        content.Children.Add(_emptyLabel);
        _root = new Border
        {
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            // 分层窗口 alpha=0 会穿透鼠标；此层仅负责命中，不另加材质。
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
            Padding = new Thickness(9, 0, 9, 0), Cursor = Cursors.Hand,
            Child = content, ToolTip = "无线设备电量 · 点击查看全部"
        };
        Content = _root;
        _root.MouseLeftButtonUp += (_, _) => ToggleDetails();
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        RefreshBackground();
    }

    internal void AttachService(WirelessBatteryService? service)
    {
        if (_closed) return;
        if (_service != null && _devicesChangedHandler != null) _service.DevicesChanged -= _devicesChangedHandler;
        _service = service;
        int version = ++_serviceVersion;
        _devicesChangedHandler = devices => QueueDevices(devices, version);
        if (service != null) service.DevicesChanged += _devicesChangedHandler;
        _detailWindow?.AttachService(service);
        // 窗口重建后，事件不会主动补发既有状态。
        QueueDevices(service?.CurrentDevices ?? Array.Empty<WirelessBatteryDevice>(), version);
    }

    internal void RefreshFromSettings()
    {
        if (_closed) return;
        RefreshBackground();
        UpdatePlacement(_monitorBoundsPx);
    }

    internal void RefreshBackground()
    {
        if (_closed) return;
        string tone = _settings.EffectiveTone;
        _root.BorderBrush = TonePalette.BorderBrush(tone);
        _emptyLabel.Foreground = TonePalette.CapsuleTextMutedBrush(tone);
        AcrylicWindowHelper.Apply(this, tone, "wireless-battery", _settings.TaskbarBlurAmount);
        _detailWindow?.RefreshTonePalette();
        RenderDevices(_renderedCount < 0 ? 2 : _renderedCount, force: true);
    }

    /// <param name="monitorBoundsPx">当前可见系统监控胶囊的物理像素矩形；无监控时为 null。</param>
    internal void UpdatePlacement(Rect? monitorBoundsPx)
    {
        if (_closed) return;
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0) return;
        _monitorBoundsPx = monitorBoundsPx;
        if (!_settings.WirelessBatteryEnabled) { HideCapsule(); return; }
        int tbLeft = 0, tbTop = 0, tbWidth = 0, tbHeight = 0;
        bool available = TaskbarService.IsTaskbarOnScreen()
            && TaskbarService.TryGetTaskbarBounds(out tbLeft, out tbTop, out tbWidth, out tbHeight)
            && tbWidth > 0 && tbHeight > 0;
        if (!available)
        {
            if (_taskbarUnavailableSince == DateTime.MinValue) _taskbarUnavailableSince = DateTime.UtcNow;
            if ((DateTime.UtcNow - _taskbarUnavailableSince).TotalMilliseconds >= 900) HideCapsule();
            return;
        }
        _taskbarUnavailableSince = DateTime.MinValue;
        // 未知中心应用区边界时先隐藏，绝不猜测可用空间覆盖应用按钮。
        if (!TaskbarService.TryGetIslandRectsOnScreen(out int appLeft, out _, out _, out _, out _, out _))
        { HideCapsule(); return; }

        IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
        double scale = GetDpi() / 96.0;
        int x = monitorBoundsPx is Rect monitor && !monitor.IsEmpty && monitor.Width > 0
            ? (int)Math.Ceiling(monitor.Right) + GapPx : tbLeft + 10;
        int room = Math.Min(appLeft - GapPx, tbLeft + tbWidth) - x;
        RenderDevices(2);
        int widthPx = (int)Math.Ceiling(Width * scale);
        if (widthPx > room && _devices.Count > 1)
        {
            RenderDevices(1);
            widthPx = (int)Math.Ceiling(Width * scale);
        }
        if (room < widthPx || x < tbLeft) { HideCapsule(); return; }
        int heightPx = (int)Math.Round(HeightDip * scale);
        int y = tbTop + (tbHeight - heightPx) / 2;
        bool wasVisible = IsVisible;
        if (x != _lastX || y != _lastY || widthPx != _lastWidth || heightPx != _lastHeight || !wasVisible)
        {
            // 只移动尺寸，不改变 Z 序；层级交给 taskbar owner 与 App 的 ownerGuard。
            if (!SetWindowPos(hwnd, IntPtr.Zero, x, y, widthPx, heightPx, 0x0010 | 0x0004 | 0x0200)) return;
            _lastX = x; _lastY = y; _lastWidth = widthPx; _lastHeight = heightPx;
        }
        if (!wasVisible)
        {
            Show();
            AcrylicWindowHelper.BindToTaskbarLayer(this);
        }
    }

    private void QueueDevices(IReadOnlyList<WirelessBatteryDevice> devices, int version)
    {
        if (_closed || version != _serviceVersion) return;
        void Apply()
        {
            if (_closed || version != _serviceVersion) return;
            _devices = devices;
            _renderedCount = -1;
            UpdatePlacement(_monitorBoundsPx);
        }
        if (Dispatcher.CheckAccess()) Apply();
        else if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
        {
            try { Dispatcher.BeginInvoke(new Action(Apply)); }
            catch (InvalidOperationException) { /* Dispatcher 已关闭。 */ }
        }
    }

    private void RenderDevices(int limit, bool force = false)
    {
        var visibleDevices = _devices.Where(device => !device.IsHidden)
            .OrderByDescending(device => device.IsConnected)
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        int count = Math.Min(limit, Math.Min(2, visibleDevices.Length));
        if (!force && _renderedCount == count) return;
        _renderedCount = count;
        _items.Children.Clear();
        _items.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _emptyLabel.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (count == 0) { Width = 80; return; }
        for (int i = 0; i < count; i++)
        {
            if (i > 0) _items.Children.Add(new Border { Width = 1, Height = 22, Margin = new Thickness(4, 0, 4, 0), Background = TonePalette.CapsuleTrackBrush(_settings.EffectiveTone) });
            _items.Children.Add(CreateDeviceItem(visibleDevices[i]));
        }
        const double itemWidth = 48;
        double width = 20 + count * itemWidth + (count - 1) * 9;
        int hidden = visibleDevices.Length - count;
        if (hidden > 0)
        {
            var more = new TextBlock { Text = $"+{hidden}", FontSize = 10, Foreground = TonePalette.CapsuleTextMutedBrush(_settings.EffectiveTone), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            more.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            width += more.DesiredSize.Width;
            _items.Children.Add(more);
        }
        Width = Math.Ceiling(width);
    }

    private FrameworkElement CreateDeviceItem(WirelessBatteryDevice device)
    {
        string tone = _settings.EffectiveTone;
        bool known = device.Percent is >= 0 and <= 100;
        string name = _settings.GetWirelessBatteryName(device.Id, device.Name);
        string type = _settings.GetWirelessBatteryType(device.Id, device.Type);
        Brush fore = known && device.Percent <= 15 ? TonePalette.MetricSparkBrush(tone) : TonePalette.CapsuleTextBrush(tone);
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(19) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.Children.Add(new TextBlock { Text = GetDeviceGlyph(type), FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14, Foreground = fore, VerticalAlignment = VerticalAlignment.Center });
        var percent = new TextBlock { Text = known ? $"{device.Percent}%" : "--", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = fore, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(percent, 1);
        header.Children.Add(percent);
        var item = new StackPanel { Width = 48, VerticalAlignment = VerticalAlignment.Center, Opacity = device.IsStale ? 0.65 : 1, ToolTip = $"{name}\n{(known ? $"{device.Percent}%" : "电量未知")}{(device.IsStale ? " · 上次读取" : "")}" };
        item.Children.Add(header);
        return item;
    }

    internal static string GetDeviceGlyph(string? type) => type switch
    {
        "mouse" => "\uE962",
        "keyboard" => "\uE765",
        "headset" => "\uE7F6",
        "controller" => "\uE7FC",
        _ => "\uE772"
    };

    private void ToggleDetails()
    {
        if (_closed || !_settings.WirelessBatteryEnabled) return;
        if (_detailWindow is { IsVisible: true }) { _detailWindow.Close(); return; }
        var detail = new WirelessBatteryDetailWindow(_settings, _service);
        _detailWindow = detail;
        detail.Closed += (_, _) => { if (ReferenceEquals(_detailWindow, detail)) _detailWindow = null; };
        double scale = GetDpi() / 96.0;
        detail.ShowAtCapsule(new Rect(_lastX / scale, _lastY / scale, _lastWidth / scale, _lastHeight / scale));
    }

    private void HideCapsule()
    {
        _detailWindow?.Close();
        if (IsVisible) Hide();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
        if (AcrylicWindowHelper.IsIslandStyle()) AcrylicWindowHelper.EnableRoundedCorners(this);
        AcrylicWindowHelper.RemoveWindowBorder(this);
        AcrylicWindowHelper.MakeNoActivate(this);
        AcrylicWindowHelper.BindToTaskbarLayer(this);
        RefreshBackground();
    }

    private int GetDpi()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        uint dpi = hwnd == IntPtr.Zero ? 96 : GetDpiForWindow(hwnd);
        return dpi > 0 ? (int)dpi : 96;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        ++_serviceVersion;
        if (_service != null && _devicesChangedHandler != null) _service.DevicesChanged -= _devicesChangedHandler;
        _service = null;
        _devicesChangedHandler = null;
        _detailWindow?.Close();
        _detailWindow = null;
        _items.Children.Clear();
        _devices = Array.Empty<WirelessBatteryDevice>();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}

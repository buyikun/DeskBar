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

    /// <summary>最多显示几个设备；再多的折叠成「+N」。按可用空间在此上限内动态取。</summary>
    private const int MaxVisibleDevices = 4;
    /// <summary>单设备宽度（DIP）：图标 19 + 百分比文本。</summary>
    private const double ItemWidthWithPercent = 48;
    /// <summary>没有电量时的单设备宽度：只画图标，不给「--」占位。</summary>
    private const double ItemWidthIconOnly = 26;
    /// <summary>设备之间的竖分隔线 + 左右各 4px 外边距。</summary>
    private const double SeparatorWidth = 9;
    /// <summary>胶囊左右内边距合计。</summary>
    private const double HorizontalPadding = 20;
    /// <summary>「+N」文字的估算宽度（10pt）。只用于判断放不放得下。</summary>
    private const double MoreLabelWidth = 22;
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
    private bool _shouldShow;
    internal bool WantsLayerMaintenance => _shouldShow && !_closed;
    private int _serviceVersion;
    private int _renderedCount = -1;
    /// <summary>上一次渲染的「显示几个 + 各设备有无电量」签名，用来跳过无变化的重建。</summary>
    private string _renderedKey = "";
    /// <summary>上一次算出的可用宽度（DIP）；刷新配色时沿用它，避免胶囊先撑宽再被缩回。</summary>
    private double _lastAvailableDip = double.PositiveInfinity;
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
        RenderDevices(MaxVisibleDevices, _lastAvailableDip, force: true);
    }

    /// <param name="monitorBoundsPx">当前可见系统监控胶囊的物理像素矩形；无监控时为 null。</param>
    internal void UpdatePlacement(Rect? monitorBoundsPx)
    {
        if (_closed) return;
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0) return;
        _monitorBoundsPx = monitorBoundsPx;
        if (!_settings.WirelessBatteryEnabled) { HideCapsule(); return; }
        if (TaskbarService.IsFullscreenForeground()) return;
        // 已由共享 layerGuard 为截图遮罩折叠时，不要在遮罩消失前主动重显。
        if (AcrylicWindowHelper.IsLayerSuppressed(this) && !IsVisible) return;
        // 所有设备都被设成「不显示」时，整块胶囊一起收起，而不是留一个空的「无线 --」。
        // （_devices 为空表示还没检测到任何设备，那种情况下保留占位提示。）
        if (_devices.Count > 0 && !_devices.Any(device => device.IsVisibleOnTaskbar))
        {
            HideCapsule();
            return;
        }
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
        // 几何暂时取不到（例如任务栏刚重建、UIA 尚未就绪）时保留上一次的位置，绝不 Hide ——
        // 与监控浮岛 / 描边窗口同一口径。旧写法在这里直接 HideCapsule，于是「切个设置项
        // 触发 ApplyStyle 重建任务栏区域」就会让组件消失几百毫秒再出现。
        if (!TaskbarService.TryGetIslandRectsOnScreen(out int appLeft, out _, out _, out _, out _, out _))
            return;

        IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
        double scale = GetDpi() / 96.0;
        int x = monitorBoundsPx is Rect monitor && !monitor.IsEmpty && monitor.Width > 0
            ? (int)Math.Ceiling(monitor.Right) + GapPx : tbLeft + 10;
        int room = Math.Min(appLeft - GapPx, tbLeft + tbWidth) - x;
        // 按可用空间在 MaxVisibleDevices 上限内动态决定显示几个，不再写死 2 个。
        _lastAvailableDip = room / scale;
        RenderDevices(MaxVisibleDevices, _lastAvailableDip);
        int widthPx = (int)Math.Ceiling(Width * scale);
        if (room < widthPx || x < tbLeft) { HideCapsule(); return; }
        AcrylicWindowHelper.ClearLayerSuppression(this);
        int heightPx = (int)Math.Round(HeightDip * scale);
        int y = tbTop + (tbHeight - heightPx) / 2;
        bool wasVisible = IsVisible;
        if (x != _lastX || y != _lastY || widthPx != _lastWidth || heightPx != _lastHeight || !wasVisible)
        {
            // 只移动尺寸，不改变 Z 序；层级交给 taskbar owner 与 App 的共享 layerGuard。
            if (!SetWindowPos(hwnd, IntPtr.Zero, x, y, widthPx, heightPx, 0x0010 | 0x0004 | 0x0200)) return;
            _lastX = x; _lastY = y; _lastWidth = widthPx; _lastHeight = heightPx;
        }
        _shouldShow = true;
        if (!wasVisible)
        {
            AcrylicWindowHelper.BindToTaskbarLayer(this);
            Show();
        }
        if (AcrylicWindowHelper.ShouldDeferForCaptureOverlay(this)) return;
        AcrylicWindowHelper.ClearLayerSuppression(this);
    }

    private void QueueDevices(IReadOnlyList<WirelessBatteryDevice> devices, int version)
    {
        if (_closed || version != _serviceVersion) return;
        void Apply()
        {
            if (_closed || version != _serviceVersion) return;
            _devices = devices;
            // 设备列表换了，强制重建一次（签名比较只挡「内容完全相同」的重复渲染）。
            _renderedKey = "";
            UpdatePlacement(_monitorBoundsPx);
        }
        if (Dispatcher.CheckAccess()) Apply();
        else if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
        {
            try { Dispatcher.BeginInvoke(new Action(Apply)); }
            catch (InvalidOperationException) { /* Dispatcher 已关闭。 */ }
        }
    }

    /// <param name="limit">最多显示几个，仍受 <see cref="MaxVisibleDevices"/> 约束。</param>
    /// <param name="availableDip">可用宽度（DIP）；放不下就逐个减到放得下。</param>
    private void RenderDevices(int limit, double availableDip = double.PositiveInfinity, bool force = false)
    {
        var visibleDevices = _devices.Where(device => device.IsVisibleOnTaskbar)
            .OrderByDescending(device => device.IsConnected)
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        int upper = Math.Min(Math.Min(limit, MaxVisibleDevices), visibleDevices.Length);
        int count = upper;
        while (count > 0 && EstimateCapsuleWidth(visibleDevices, count) > availableDip) count--;

        string key = BuildRenderKey(visibleDevices, count);
        if (!force && _renderedKey == key) return;
        _renderedKey = key;
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

        double width = EstimateCapsuleWidth(visibleDevices, count, includeMore: false);
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

    /// <summary>
    /// 估算显示 <paramref name="count"/> 个设备时的胶囊宽度（DIP）。只用于「放不放得下」的判断，
    /// 实际宽度在渲染后用 Measure 修正，所以这里允许少量误差。
    /// </summary>
    private static double EstimateCapsuleWidth(IReadOnlyList<WirelessBatteryDevice> devices, int count, bool includeMore = true)
    {
        double width = HorizontalPadding;
        for (int i = 0; i < count; i++)
        {
            if (i > 0) width += SeparatorWidth;
            width += devices[i].Percent is >= 0 and <= 100 ? ItemWidthWithPercent : ItemWidthIconOnly;
        }
        if (includeMore && devices.Count > count) width += MoreLabelWidth;
        return width;
    }

    /// <summary>显示个数 + 每个设备有无电量的签名；任一变化都要重建（无电量只画图标，宽度会变）。</summary>
    private static string BuildRenderKey(IReadOnlyList<WirelessBatteryDevice> devices, int count)
    {
        var sb = new System.Text.StringBuilder(count + 1);
        sb.Append((char)('0' + count));
        for (int i = 0; i < count; i++)
            sb.Append(devices[i].Percent is >= 0 and <= 100 ? '1' : '0');
        return sb.ToString();
    }

    private FrameworkElement CreateDeviceItem(WirelessBatteryDevice device)
    {
        string tone = _settings.EffectiveTone;
        bool known = device.Percent is >= 0 and <= 100;
        string name = _settings.GetWirelessBatteryName(device.Id, device.Name);
        string type = _settings.GetWirelessBatteryType(device.Id, device.Type);
        Brush fore = known && device.Percent <= 15 ? TonePalette.MetricSparkBrush(tone) : TonePalette.CapsuleTextBrush(tone);
        var header = new Grid();
        // 有电量时「图标 + 百分比」两列；没有电量时不给第二列，让图标在整格里居中，
        // 否则图标会被 19px 的固定列顶到左边、右边空一条。
        if (known)
        {
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(19) });
            header.ColumnDefinitions.Add(new ColumnDefinition());
        }
        header.Children.Add(new TextBlock
        {
            Text = GetDeviceGlyph(type),
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = fore,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = known ? HorizontalAlignment.Left : HorizontalAlignment.Center
        });
        // 没有电量时不写「--」、不占文本位，只留图标。
        if (known)
        {
            var percent = new TextBlock { Text = $"{device.Percent}%", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = fore, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(percent, 1);
            header.Children.Add(percent);
        }
        var item = new StackPanel { Width = known ? ItemWidthWithPercent : ItemWidthIconOnly, VerticalAlignment = VerticalAlignment.Center, Opacity = device.IsStale ? 0.65 : 1, ToolTip = $"{name}\n{(known ? $"{device.Percent}%" : "电量未知")}{(device.IsStale ? " · 上次读取" : "")}" };
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
        _shouldShow = false;
        AcrylicWindowHelper.ClearLayerSuppression(this);
        _detailWindow?.Close();
        if (Visibility == Visibility.Collapsed) Visibility = Visibility.Hidden;
        else if (IsVisible) Hide();
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
        _shouldShow = false;
        AcrylicWindowHelper.ClearLayerSuppression(this);
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

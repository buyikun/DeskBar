using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DeskBar.Services;

namespace DeskBar.Views;

/// <summary>
/// 任务栏上「中间应用区」与「托盘区」的 1px 细边框覆盖层。
/// 全透明 / 全亚克力（整条）模式下任务栏没有任何可见结构，而左侧系统监控浮岛那种描边
/// 很受用；这里给两块功能区补上同款描边。窗口完全点击穿透、不抢焦点，只负责绘制。
/// </summary>
public partial class IslandBorderWindow : Window
{
    internal enum Side
    {
        Middle,
        Tray
    }

    private readonly Side _side;
    private readonly DispatcherTimer _timer;
    private double _lx = double.NaN;
    private double _ly;
    private double _lw;
    private double _lh;
    private bool _islandStyle;

    // 任务栏「不在屏上」必须持续一段时间才 Hide：拖拽托盘图标 / 任务栏重建时
    // 一次瞬时误判就会让描边凭空消失。用时长而非轮数，因为本窗口是 16ms tick。
    private DateTime _goneSince = DateTime.MinValue;
    private const int GoneMsNeeded = 900;
    private int _raiseTicks;
    private bool _isDisposed;

    /// <summary>
    /// 任务栏真的不在屏幕上了才返回 false：需连续 GoneTicksNeeded 轮判定失败。
    /// 单轮瞬时误判（explorer 重排任务栏时窗口矩形瞬变为零）会被忽略。
    /// </summary>
    private bool TaskbarOnScreenSustained()
    {
        if (TaskbarService.IsTaskbarOnScreen())
        {
            _goneSince = DateTime.MinValue;
            return true;
        }
        if (_goneSince == DateTime.MinValue) _goneSince = DateTime.UtcNow;
        return (DateTime.UtcNow - _goneSince).TotalMilliseconds < GoneMsNeeded;
    }

    internal IslandBorderWindow(Side side)
    {
        _side = side;
        InitializeComponent();
        Title = side == Side.Middle ? "DeskBarIslandBorderMiddle" : "DeskBarIslandBorderTray";

        // 先放到屏幕外，第一次 Sync 算好几何再真正显示，避免闪一下
        Left = -32000;
        Top = -32000;

        SourceInitialized += (_, _) =>
        {
            try {
                AcrylicWindowHelper.MakeClickThrough(this);
                AcrylicWindowHelper.BindToTaskbarLayer(this);
            } catch { }
        };

        Closed += (_, _) =>
        {
            _isDisposed = true;
            try { _timer.Stop(); } catch { }
        };

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _timer.Tick += (_, _) => Sync();
        _timer.Start();
    }

    private void Sync()
    {
        if (_isDisposed)
        {
            try { _timer?.Stop(); } catch { }
            return;
        }
        try
        {
            // 部分亚克力（浮岛）模式下任务栏本身就是两颗带形状的岛屿，
            // 再描一圈边纯属多余，直接不显示。
            if (TaskbarService.IsIslandActive)
            {
                if (IsVisible) Hide();
                return;
            }

            bool onScreen = TaskbarOnScreenSustained();
            bool haveRects = TaskbarService.TryGetIslandRectsOnScreen(out int midL, out int midR, out int trayL, out int trayR,
                    out int top, out int height);
            if (!onScreen)
            {
                if (IsVisible) Hide();
                return;
            }
            if (!haveRects)
            {
                // 几何暂时取不到（例如任务栏刚重建、UIA 尚未就绪）时保留上一次的位置，
                // 绝不 Hide —— 否则用户会看到描边凭空消失。
                return;
            }

            int l = _side == Side.Middle ? midL : trayL;
            int r = _side == Side.Middle ? midR : trayR;

            // 托盘区左缘改用实时值，与音乐胶囊读的是同一份数据 ——
            // 两边各读各的（一个读缓存锚点、一个读扫描结果）就会错位或一边不跟随。
            if (_side == Side.Tray &&
                TaskbarService.TryGetTrayLeftRealtime(out int rtTrayLeft, out _, out _) &&
                rtTrayLeft > l && rtTrayLeft < r)
            {
                l = rtTrayLeft;
            }

            // 用户要求「不要把两段合成一个描边整体，各自一个细边框」，
            // 所以胶囊与托盘区各画各的，中间露出的是任务栏自己的底色。
            int w = r - l;
            if (w < 8 || height < 8)
            {
                if (IsVisible) Hide();
                return;
            }

            var dpi = VisualTreeHelper.GetDpi(this);
            double sx = dpi.DpiScaleX <= 0 ? 1.0 : dpi.DpiScaleX;
            double sy = dpi.DpiScaleY <= 0 ? 1.0 : dpi.DpiScaleY;
            double dipL = l / sx;
            double dipT = top / sy;
            double dipW = w / sx;
            double dipH = height / sy;

            // 浮岛模式下任务栏被裁成「顶角圆润、底边平直」的形状，描边跟着走；
            // 整条模式下这两块区域没有形状线索，用与监控浮岛一致的四角圆角。
            bool island = TaskbarService.IsIslandActive;
            if (island != _islandStyle)
            {
                _islandStyle = island;
                var rad = island ? new CornerRadius(24) : new CornerRadius(24);
                Edge.CornerRadius = rad;
            }

            if (IsVisible &&
                Math.Abs(dipL - _lx) < 0.5 && Math.Abs(dipT - _ly) < 0.5 &&
                Math.Abs(dipW - _lw) < 0.5 && Math.Abs(dipH - _lh) < 0.5)
            {
                // 几何没变，但 explorer 随时可能把任务栏抬到我们上面（点一下任务栏就会），
                // 每 16ms 确认一次自己在最上层。
                AcrylicWindowHelper.KeepAboveTaskbar(this);
                return;
            }
            _raiseTicks = 0;

            _lx = dipL;
            _ly = dipT;
            _lw = dipW;
            _lh = dipH;
            Left = dipL;
            Top = dipT;
            Width = dipW;
            Height = dipH;

            try
            {
                if (!IsVisible && !_isDisposed) Show();
            }
            catch (InvalidOperationException)
            {
                _isDisposed = true;
                try { _timer?.Stop(); } catch { }
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"island border sync failed: {ex.Message}");
        }
    }
}

using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using DeskBar.Services;

namespace DeskBar.Views;

/// <summary>
/// 托盘图标宿主。
/// 本机实测：H.NotifyIcon 的 ForceCreate 在此环境抛 "TryCreate failed"，
/// 因此托盘图标本身仍用 System.Windows.Forms.NotifyIcon（官方 API，无需额外依赖）；
/// 但右键菜单已换成 WPF ContextMenu —— 只有 WPF 才能吃到应用级的
/// 柔雾/黑丝资源字典（Themes/MenuStyles.xaml），与音乐胶囊菜单保持同一套外观。
/// </summary>
internal sealed class TrayIconHost : IDisposable
{
    private readonly App _app;
    private NotifyIcon? _icon;
    private Icon? _ownedIcon;

    /// <summary>
    /// WPF 菜单的归属窗口。ContextMenu 必须挂在一个占据前台的窗口上，
    /// 否则点击菜单以外的区域不会关闭菜单（托盘场景尤其明显：
    /// 右键托盘时前台窗口是资源管理器，我们不在前台就拿不到跨进程的鼠标捕获）。
    /// 所以造一个 1x1、透明、放在屏幕外的窗口专门干这件事。
    /// </summary>
    private Window? _menuOwner;
    private IntPtr _menuOwnerHwnd;
    private System.Windows.Controls.ContextMenu? _menu;

    public TrayIconHost(App app) => _app = app;

    public void Create(bool visible)
    {
        Log.Info("tray: begin create");
        _ownedIcon = BuildIcon();

        _icon = new NotifyIcon
        {
            Icon = _ownedIcon,
            Text = "DeskBar — 双击桌面空白可切换图标",
            Visible = visible
        };
        Log.Info($"tray: NotifyIcon created, visible={visible}");

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                _app.ShowSettingsWindow();
            }
            else if (e.Button == MouseButtons.Right)
            {
                ShowContextMenu();
            }
        };
        _icon.DoubleClick += (_, _) => _app.ShowSettingsWindow();

        Log.Info("tray: ready");
    }

    // ---------------- WPF 右键菜单 ----------------

    /// <summary>首次右键时才造 owner 窗口，避免启动期多一个无谓的顶层窗口。</summary>
    private void EnsureMenuOwner()
    {
        if (_menuOwner is not null) return;

        _menuOwner = new Window
        {
            Width = 1,
            Height = 1,
            Left = -4000,
            Top = -4000,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            // 不抢焦点：真正决定菜单能否关闭的是下面主动申请的前台权，
            // 而不是窗口激活态的副作用。抢焦点会让任务栏短暂失焦。
            ShowActivated = false,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            Title = "DeskBarMenuOwner"
        };
        _menuOwner.Show();
        _menuOwnerHwnd = new System.Windows.Interop.WindowInteropHelper(_menuOwner).Handle;
        Log.Info($"tray: menu owner created, hwnd=0x{_menuOwnerHwnd:X}");
    }

    private void ShowContextMenu()
    {
        try
        {
            EnsureMenuOwner();
            if (_menuOwner is null) return;

            _menu ??= BuildMenu();

            _menu.PlacementTarget = _menuOwner;
            _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;

            // 把前台权移到 owner 窗口，PopupRoot 才能拿到跨进程鼠标捕获，
            // 从而实现「点菜单外面任意程序都能关掉菜单」这一用户预期行为。
            if (_menuOwnerHwnd != IntPtr.Zero)
            {
                Services.PlayerWindowControl.ActivateWindow(_menuOwnerHwnd);
            }

            _menu.IsOpen = true;
        }
        catch (Exception ex)
        {
            Log.Error($"tray: show context menu failed: {ex.Message}");
        }
    }

    private System.Windows.Controls.ContextMenu BuildMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var openItem = new System.Windows.Controls.MenuItem { Header = "打开设置" };
        openItem.Click += (_, _) => _app.ShowSettingsWindow();
        menu.Items.Add(openItem);

        var toggleIcons = new System.Windows.Controls.MenuItem { Header = "显示/隐藏桌面图标" };
        toggleIcons.Click += (_, _) =>
        {
            var v = App.Icons.Toggle();
            UpdateTip(v);
        };
        menu.Items.Add(toggleIcons);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var quit = new System.Windows.Controls.MenuItem { Header = "退出" };
        quit.Click += (_, _) => System.Windows.Application.Current.Shutdown();
        menu.Items.Add(quit);

        // 菜单收起后把前台队列交还，避免 owner 窗口长期霸占前台
        // 导致其他程序的激活行为变怪。
        menu.Closed += (_, _) =>
        {
            try
            {
                if (_menuOwnerHwnd != IntPtr.Zero)
                {
                    Interop.NativeMethods.PostMessageW(_menuOwnerHwnd, 0x0000, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch { }
        };

        return menu;
    }

    private void UpdateTip(bool visible)
    {
        if (_icon is null) return;
        _icon.Text = visible ? "DeskBar — 桌面图标可见" : "DeskBar — 桌面图标已隐藏";
    }

    /// <summary>运行时切托盘可见性。</summary>
    /// <summary>
    /// 自动检查更新发现新版本时，用托盘气泡提示。
    /// 用气泡而不是弹窗：启动阶段弹窗会抢走焦点，而用户此刻通常没在操作 DeskBar。
    /// </summary>
    public void ShowUpdateBalloon(string latestVersion)
    {
        try
        {
            if (_icon is null || !_icon.Visible) return;
            _icon.BalloonTipTitle = "DeskBar 有新版本";
            _icon.BalloonTipText = $"v{latestVersion} 已发布。点击托盘图标打开设置 → 检查更新，即可下载。";
            _icon.BalloonTipIcon = ToolTipIcon.Info;
            _icon.ShowBalloonTip(5000);
            Log.Info($"tray: update balloon shown for v{latestVersion}");
        }
        catch (Exception ex)
        {
            Log.Debug($"tray: update balloon failed: {ex.Message}");
        }
    }

    public void SetVisible(bool visible)
    {
        if (_icon is null) return;
        _icon.Visible = visible;
        Log.Info($"tray visibility -> {visible}");
    }

    /// <summary>绘制 32x32 图标：深色圆角方块 + 两条蓝色横杠（示意任务栏）。</summary>
    private static Icon BuildIcon()
    {
        try
        {
            var iconUri = new Uri("pack://application:,,,/DeskBar;component/app.ico");
            var streamInfo = System.Windows.Application.GetResourceStream(iconUri);
            if (streamInfo != null)
            {
                using var s = streamInfo.Stream;
                using var resIcon = new Icon(s, new System.Drawing.Size(32, 32));
                return (Icon)resIcon.Clone();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Load app.ico resource failed: {ex.Message}, fallback to procedural icon");
        }

        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var bg = new SolidBrush(Color.FromArgb(0x1E, 0x1E, 0x1E));
            using var path = RoundedRect(1, 1, 30, 30, 8);
            g.FillPath(bg, path);

            using var accent = new SolidBrush(Color.FromArgb(0x4C, 0x8B, 0xF5));
            g.FillRectangle(accent, 7, 9, 18, 4);
            g.FillRectangle(accent, 7, 17, 18, 4);
            using var dot = new SolidBrush(Color.FromArgb(0x9A, 0xC5, 0xFF));
            g.FillEllipse(dot, 13, 23, 6, 4);
        }

        // 存成 ICO 流再从流加载，得到由 Icon 自行管理的内存
        using var ms = new System.IO.MemoryStream();
        var hIcon = bmp.GetHicon();
        using (var tmp = Icon.FromHandle(hIcon))
        {
            tmp.Save(ms);
        }
        Interop.NativeMethods.DestroyIcon(hIcon);
        ms.Position = 0;
        // Clone 使 Icon 拥有独立的流副本，避免依赖 ms 生命周期
        using var loaded = new Icon(ms);
        return (Icon)loaded.Clone();
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(int x, int y, int w, int h, int r)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        p.AddArc(x, y, r, r, 180, 90);
        p.AddArc(x + w - r, y, r, r, 270, 90);
        p.AddArc(x + w - r, y + h - r, r, r, 0, 90);
        p.AddArc(x, y + h - r, r, r, 90, 90);
        p.CloseFigure();
        return p;
    }

    public void Dispose()
    {
        if (_icon is not null)
        {
            _icon.Visible = false;
            _icon.Dispose();
            _icon = null;
        }
        try { if (_menu is not null) _menu.IsOpen = false; } catch { }
        try { _menuOwner?.Close(); } catch { }
        _menuOwner = null;
        _menu = null;
        _ownedIcon?.Dispose();
        _ownedIcon = null;
    }
}

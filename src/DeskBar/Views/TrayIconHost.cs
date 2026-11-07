using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using DeskBar.Services;

namespace DeskBar.Views;

/// <summary>
/// 托盘图标宿主。
/// 本机实测：H.NotifyIcon 的 ForceCreate 在此环境抛 "TryCreate failed"，
/// 因此改用 System.Windows.Forms.NotifyIcon（官方 API，无需额外依赖）。
/// </summary>
internal sealed class TrayIconHost : IDisposable
{
    private readonly App _app;
    private NotifyIcon? _icon;
    private Icon? _ownedIcon;

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

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开设置", null, (_, _) => _app.ShowSettingsWindow());
        menu.Items.Add("显示/隐藏桌面图标", null, (_, _) =>
        {
            var v = App.Icons.Toggle();
            UpdateTip(v);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => System.Windows.Application.Current.Shutdown());

        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _app.ShowSettingsWindow();
        };
        _icon.DoubleClick += (_, _) => _app.ShowSettingsWindow();

        Log.Info("tray: ready");
    }

    private void UpdateTip(bool visible)
    {
        if (_icon is null) return;
        _icon.Text = visible ? "DeskBar — 桌面图标可见" : "DeskBar — 桌面图标已隐藏";
    }

    /// <summary>运行时切托盘可见性。</summary>
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
        _ownedIcon?.Dispose();
        _ownedIcon = null;
    }
}

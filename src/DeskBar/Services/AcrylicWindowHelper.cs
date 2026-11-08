using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DeskBar.Services;

/// <summary>
/// 让任意 WPF 浮岛窗口获得与任务栏完全同源的亚克力质感。
/// 实测关键结论：AccentFlags 必须为 0；只要非 0，系统就会在模糊层上再叠一层降亮/着色涂层，
/// 把壁纸彻底挡死，看起来就是一块死白或死黑的塑料板。
/// </summary>
internal static class AcrylicWindowHelper
{
    public static void Apply(Window window, string? tone, string logTag, bool flat = false, int blur = 18)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        // 先关掉 Win11 系统背景材质：它按"应用主题"渲染，浅色主题下会糊上一块奶白，
        // 与任务栏实际使用的 natural 透壁纸色调对不上。
        int backdropNone = 1;   // DWMSBT_NONE
        try { DwmSetWindowAttribute(hwnd, 38, ref backdropNone, sizeof(int)); } catch { }

        // 与任务栏共用同一套色调常量，保证两处色调永远同步
        var t = (tone ?? "natural").ToLowerInvariant();
        uint gradient = NativeTaskbarAppearanceService.GetColorForTone(t);

        var accent = new AccentPolicy
        {
            // 整条模式（全透明 / 全亚克力）下浮岛就盖在任务栏自己的底上，
            // 再糊一层亚克力会与任务栏材质对不上；只有浮岛模式里它悬在壁纸上方，才需要自带磨砂。
            AccentState = flat ? 0 : 4,   // 0 = ACCENT_DISABLED / 4 = ACCENT_ENABLE_ACRYLICBLURBEHIND
            AccentFlags = 0,     // 必须为 0，否则叠加降亮涂层
            GradientColor = flat ? 0u : gradient,
            AnimationId = 0
        };

        // 如需调整模糊程度，可在此处扩展，但 AccentPolicy 本身不支持直接指定模糊度数值，
        // 我们通过调整 AccentState 或其他 DWM 属性尝试映射，目前保持原生行为。
        
        int size = Marshal.SizeOf<AccentPolicy>();
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
            Log.Debug($"{logTag} accent mode={(flat ? "flat-on-taskbar" : "island-acrylic")} tone={t} color=0x{gradient:X8} ok={ok}");
        }
        catch (Exception ex)
        {
            Log.Debug($"{logTag} ApplyAccent failed: {ex.Message}");
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }

        // 让材质铺满整个客户区
        var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    /// <summary>
    /// 去掉 DWM 给窗口额外画的那圈 1px 边框（DWMWA_BORDER_COLOR = 34 / DWMWA_COLOR_NONE）。
    /// 浮岛窗口把 DWM 框架铺满整个客户区，系统会在边缘再画一道浅色细线，
    /// 与我们自己的描边叠在一起就是用户看到的「双重框」。
    /// </summary>
    public static void RemoveWindowBorder(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int none = unchecked((int)0xFFFFFFFE);   // DWMWA_COLOR_NONE
            DwmSetWindowAttribute(hwnd, 34, ref none, sizeof(int));
        }
        catch { }
    }

    /// <summary>Win11 圆角（DWMWCP_ROUND）。无边框窗口需显式开启，否则呈直角。</summary>
    public static void EnableRoundedCorners(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int pref = 2;   // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int));
        }
        catch { }
    }

    /// <summary>让系统标题栏使用深色（DWMWA_USE_IMMERSIVE_DARK_MODE）。带标题栏的设置窗口用。</summary>
    public static void SetDarkTitleBar(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int v = dark ? 1 : 0;
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Win10 2004+)；旧版本用 19
            if (DwmSetWindowAttribute(hwnd, 20, ref v, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, 19, ref v, sizeof(int));
            }
        }
        catch { }
    }

    /// <summary>标记为工具窗口且不抢焦点，避免出现在 Alt-Tab 与获得激活态。</summary>
    public static void MakeNoActivate(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            ex |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
        }
        catch { }
    }

    /// <summary>完全点击穿透的绘制层：鼠标事件直接落到下层窗口，且不激活、不进 Alt-Tab。</summary>
    public static void MakeClickThrough(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            ex |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
        }
        catch { }
    }

    /// <summary>
    /// 顶层窗口之间没有稳定的 z 序：点一下任务栏，explorer 就会把它抬到我们这些
    /// NoActivate 覆盖层之上。整条模式（全透明/全亚克力）下任务栏没有为浮岛挖洞，
    /// 于是浮岛和描边会整块「消失」，直到下一次几何变化才被重新抬起来。
    /// 这里只在「压着我们的正是任务栏本身」时才把自己抬回最上层；
    /// 若是音量面板之类的弹出层压着我们，就让它留在上面——否则浮岛会穿透弹出面板，那正是之前的闪烁。
    /// </summary>
    public static void RaiseAboveTaskbarIfCovered(Window window)
    {
        try
        {
            if (!window.IsVisible) return;
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            IntPtr above = GetWindow(hwnd, GW_HWNDPREV);
            if (above == IntPtr.Zero) return;            // 已经在我们能到的最高处

            IntPtr tray = FindWindowW("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return;

            if (above != tray && GetAncestor(above, GA_ROOT) != tray) return;

            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }
        catch { }
    }

    /// <summary>
    /// 无条件把自己钉在最上层（被谁压着不管是任务栏还是弹出面板）。
    /// 只挪 z 序、不发 SWP_SHOWWINDOW，所以不会象早先那版每 400ms 那样把浮岛重绘出闪烁。
    /// 与 RaiseAboveTaskbarIfCovered 的区别：那个只在「正上方就是任务栏」时才动手，
    /// 太保守——只要任务栏不是紧挨着我们上面，它就永远不会出手，而任务栏照样把我们盖住。
    /// </summary>
    /// <summary>
    /// 「与任务栏同层」的锚点：任务栏正上方那个窗口。
    /// 把本窗口插到它正下方，就等于待在任务栏正上方 —— 截图工具的遮罩、音量面板
    /// 这些本来就在任务栏之上的窗口依旧压着我们（跟着一起变暗、点不到），
    /// 而任务栏自己永远盖不住我们。
    /// </summary>
    public static IntPtr TaskbarLayerAnchor()
    {
        try
        {
            var tray = FindWindowW("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return HWND_TOPMOST;
            var above = GetWindow(tray, GW_HWNDPREV);
            return above == IntPtr.Zero ? HWND_TOPMOST : above;
        }
        catch { return HWND_TOPMOST; }
    }

    private const int GWLP_HWNDPARENT = -8;

    /// <summary>
    /// 把浮岛窗口设为任务栏（Shell_TrayWnd）的 owned window。
    /// OS 内核层保证 owned window 永远排在 owner 之上，彻底杜绝 z 序竞争与反复置顶拉锯。
    /// </summary>
    public static bool BindToTaskbarLayer(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return false;

            var tray = FindWindowW("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return false;

            if (GetWindowLongPtr(hwnd, GWLP_HWNDPARENT) == tray) return true;
            SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, tray);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 低频（如 1~2 秒）巡检确认 owner 是否依然是任务栏，不是则补设。
    /// </summary>
    public static void EnsureBoundToTaskbar(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            var tray = FindWindowW("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return;

            if (GetWindowLongPtr(hwnd, GWLP_HWNDPARENT) == tray) return;
            SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, tray);
        }
        catch { }
    }

    public static void KeepAboveTaskbar(Window window)
    {
        try
        {
            if (TaskbarService.IsFullscreenForeground())
            {
                if (window.Visibility != Visibility.Collapsed)
                    window.Visibility = Visibility.Collapsed;
                return;
            }

            if (!window.IsVisible)
            {
                if (window.Visibility == Visibility.Collapsed)
                    window.Visibility = Visibility.Visible;
                return;
            }

            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            var tray = FindWindowW("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return;

            // 已经在任务栏上方就什么都不做 —— 否则两个浮岛 + 两条描边会互相把对方
            // 往「任务栏正上方」那一个位置上挤，每 16ms 换一次 z 序。
            IntPtr cur = hwnd;
            for (int i = 0; i < 40; i++)
            {
                cur = GetWindow(cur, GW_HWNDPREV);
                if (cur == IntPtr.Zero) break;
                if (cur == tray || GetAncestor(cur, GA_ROOT) == tray)
                {
                    SetWindowPos(hwnd, TaskbarLayerAnchor(), 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                    return;
                }
            }
        }
        catch { }
    }

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint GW_HWNDPREV = 3;
    private const uint GA_ROOT = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_TOPMOST = 0x00000008;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const long WS_EX_TRANSPARENT = 0x00000020;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

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

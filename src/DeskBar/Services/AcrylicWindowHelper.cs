using System.Runtime.CompilerServices;
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
    private sealed class LayerSuppressionState
    {
        public bool IsSuppressed;
        public DateTime LastAlignmentWarningUtc;
    }

    private static readonly ConditionalWeakTable<Window, LayerSuppressionState> LayerSuppressedWindows = new();

    /// <summary>
    /// 当前任务栏风格是不是「岛」档 —— 只有这一档浮岛悬在壁纸上方、必须自己带材质。
    /// 整条模式（acrylic / clear）下浮岛靠 AllowsTransparency=True 的 WPF 真透明，
    /// 不能再叠 accent：那层磨砂无论 alpha 多低都去不掉。
    /// </summary>
    internal static bool IsIslandStyle()
        => (App.Settings?.TaskbarStyle ?? "island_acrylic").ToLowerInvariant() == "island_acrylic";

    /// <param name="forceMaterial">
    /// 整条模式下是否仍然强上材质。只给「点开才出现的浮层面板」（进程详情窗）用：
    /// 它不是贴在任务栏上的常驻浮岛，真透明会让文字压在壁纸上看不清。
    /// </param>
    public static void Apply(Window window, string? tone, string logTag, int blur = 18, bool forceMaterial = false)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        // 先关掉 Win11 系统背景材质：它按"应用主题"渲染，浅色主题下会糊上一块奶白，
        // 与任务栏实际使用的 natural 透壁纸色调对不上。
        int backdropNone = 1;   // DWMSBT_NONE
        try { DwmSetWindowAttribute(hwnd, 38, ref backdropNone, sizeof(int)); } catch { }

        // 与任务栏共用同一套色调常量，保证两处色调永远同步。
        // 但只有浮岛模式（island_acrylic）才带着色：那个模式下任务栏被挖了洞、浮岛悬在
        // 壁纸上方，必须自带磨砂。整条模式（acrylic / clear）里浮岛就盖在任务栏自己的底上，
        // 再叠同色会双重着色、比任务栏深一块。
        var t = (tone ?? "natural").ToLowerInvariant();
        var style = (App.Settings?.TaskbarStyle ?? "island_acrylic").ToLowerInvariant();
        uint gradient = style == "island_acrylic" ? NativeTaskbarAppearanceService.GetColorForTone(t) : 0u;

        // 整条模式（acrylic / clear）下浮岛窗口按 AllowsTransparency=True 建，靠 WPF 真透明即可，
        // 绝不能在这里再叠 accent —— accent 会给客户区重新糊上一层不透明磨砂，
        // 即使 GradientColor 的 alpha 为 0 也去不掉那层模糊（2026-11-11 实测）。
        if (!forceMaterial && style != "island_acrylic")
        {
            Log.Debug($"{logTag} accent style={style} transparent-skip");
            return;
        }

        var accent = new AccentPolicy
        {
            // 恒为 4，绝不能退到 0（ACCENT_DISABLED）：两个浮岛窗口 AllowsTransparency=False，
            // WPF 画不出真透明，禁用 accent 后只剩 DWM 延伸框架的黑色底 ——
            // 实测整条模式下浮岛会烧成一块纯黑板。
            AccentState = 4,   // ACCENT_ENABLE_ACRYLICBLURBEHIND
            AccentFlags = 0,     // 必须为 0，否则叠加降亮涂层
            GradientColor = gradient,
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
            Log.Debug($"{logTag} accent style={style} tone={t} color=0x{gradient:X8} ok={ok}");
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

            KeepAboveTaskbar(window);
        }
        catch { }
    }

    /// <summary>任务栏上方的相邻窗口；为零表示当前层级顶部。</summary>
    public static IntPtr TaskbarLayerAnchor()
    {
        try
        {
            var tray = FindWindowW("Shell_TrayWnd", null);
            return tray == IntPtr.Zero ? IntPtr.Zero : GetWindow(tray, GW_HWNDPREV);
        }
        catch { return IntPtr.Zero; }
    }

    private const int GWLP_HWNDPARENT = -8;

    /// <summary>把浮岛窗口设为任务栏（Shell_TrayWnd）的 owned window；owner 不替代 Z 序校准。</summary>
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
            return GetWindowLongPtr(hwnd, GWLP_HWNDPARENT) == tray;
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

            if (TryYieldToCaptureOverlay(hwnd) != OverlayYieldResult.None) return;

            var tray = FindWindowW("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return;

            if (GetWindowLongPtr(hwnd, GWLP_HWNDPARENT) != tray)
            {
                SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, tray);
                if (GetWindowLongPtr(hwnd, GWLP_HWNDPARENT) != tray) return;
            }
        }
        catch { }
    }

    public static void KeepAboveTaskbar(Window window)
        => MaintainTaskbarLayer(window, shouldBeVisible: window.IsVisible || IsLayerSuppressed(window));

    public static bool IsLayerSuppressed(Window window)
        => LayerSuppressedWindows.TryGetValue(window, out var state) && state.IsSuppressed;

    /// <summary>截图遮罩是否要求本窗口让位（已压到遮罩之下或需要折叠时返回 true）。</summary>
    public static bool ShouldDeferForCaptureOverlay(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;
        var result = TryYieldToCaptureOverlay(hwnd);
        if (result == OverlayYieldResult.NeedsCollapse)
            SuppressForLayerMaintenance(window);
        return result != OverlayYieldResult.None;
    }

    public static void ClearLayerSuppression(Window window)
        => LayerSuppressedWindows.Remove(window);

    private static void SuppressForLayerMaintenance(Window window)
    {
        var state = LayerSuppressedWindows.GetValue(window, _ => new LayerSuppressionState());
        state.IsSuppressed = true;
        if (window.Visibility != Visibility.Collapsed)
            window.Visibility = Visibility.Collapsed;
    }

    public static void MaintainTaskbarLayer(Window window, bool shouldBeVisible)
        => MaintainTaskbarLayer(window, shouldBeVisible, TaskbarService.IsFullscreenForeground());

    public static void MaintainTaskbarLayer(Window window, bool shouldBeVisible, bool isFullscreen)
    {
        if (window.Visibility == Visibility.Hidden) return;
        if (!shouldBeVisible)
        {
            ClearLayerSuppression(window);
            return;
        }
        try
        {
            if (isFullscreen)
            {
                SuppressForLayerMaintenance(window);
                return;
            }

            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            // 截图遮罩在场时优先处理：遮罩消失前保持已折叠/下压状态。
            var overlayResult = TryYieldToCaptureOverlay(hwnd);
            if (overlayResult == OverlayYieldResult.NeedsCollapse)
            {
                SuppressForLayerMaintenance(window);
                return;
            }
            if (overlayResult != OverlayYieldResult.None) return;

            if (window.Visibility == Visibility.Collapsed)
            {
                if (!IsLayerSuppressed(window)) return;
                window.Visibility = Visibility.Visible;
                ClearLayerSuppression(window);
            }
            else if (window.IsVisible)
            {
                ClearLayerSuppression(window);
            }
            else
            {
                return;
            }

            var tray = FindWindowW("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero || !Interop.NativeMethods.IsWindow(tray)) return;

            if (GetWindowLongPtr(hwnd, GWLP_HWNDPARENT) != tray)
            {
                SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, tray);
                if (GetWindowLongPtr(hwnd, GWLP_HWNDPARENT) != tray) return;
            }

            if (!GetWindowBand(hwnd, out uint windowBand) ||
                !GetWindowBand(tray, out uint taskbarBand) || windowBand != taskbarBand)
                return;

            bool taskbarTopmost = (GetWindowLongPtr(tray, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;
            bool windowTopmost = (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;
            bool levelChanged = taskbarTopmost != windowTopmost;
            if (levelChanged)
            {
                IntPtr level = taskbarTopmost ? HWND_TOPMOST : HWND_NOTOPMOST;
                if (!SetWindowPos(hwnd, level, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER))
                {
                    LogLayerAlignmentIncomplete(window, hwnd, "topmost transition failed");
                    return;
                }
                if (((GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0) != taskbarTopmost ||
                    GetWindowLongPtr(hwnd, GWLP_HWNDPARENT) != tray)
                {
                    LogLayerAlignmentIncomplete(window, hwnd, "topmost transition changed owner or did not apply");
                    return;
                }
            }

            bool? taskbarAbove = IsTaskbarAboveWindow(hwnd, tray);
            if (taskbarAbove is null) return;
            // 层级状态刚被改过时，窗口可能因此沉到任务栏下方，必须继续做一次定位。
            if (!levelChanged && taskbarAbove == false) return;

            // 锚点只取「任务栏正上方那个窗口」：插到它下方就是紧贴任务栏上方，
            // 既不会越过音量面板之类的弹出层，也不会把窗口永久推到 topmost。
            // 拿不到锚点说明任务栏已在层顶，此时什么都不做比乱插安全。
            IntPtr anchor = GetWindow(tray, GW_HWNDPREV);
            if (anchor == IntPtr.Zero || anchor == hwnd) return;
            if (!Interop.NativeMethods.IsWindow(anchor)) return;

            if (!SetWindowPos(hwnd, anchor, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER))
            {
                LogLayerAlignmentIncomplete(window, hwnd, "z-order move failed");
                return;
            }

            bool finalTopmost = (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;
            bool? finalTaskbarAbove = IsTaskbarAboveWindow(hwnd, tray);
            if (finalTopmost != taskbarTopmost || GetWindowLongPtr(hwnd, GWLP_HWNDPARENT) != tray ||
                finalTaskbarAbove != false)
                LogLayerAlignmentIncomplete(window, hwnd, $"postcondition failed; anchor=0x{anchor.ToInt64():X}");
        }
        catch { }
    }

    /// <summary>
    /// 层级纠正失败时的诊断日志。16ms 巡检会反复命中同一情况，按窗口限频 5 秒。
    /// </summary>
    private static void LogLayerAlignmentIncomplete(Window window, IntPtr hwnd, string reason)
    {
        var state = LayerSuppressedWindows.GetValue(window, _ => new LayerSuppressionState());
        var now = DateTime.UtcNow;
        if ((now - state.LastAlignmentWarningUtc).TotalSeconds < 5) return;
        state.LastAlignmentWarningUtc = now;
        Log.Warn($"taskbar layer alignment incomplete: hwnd=0x{hwnd.ToInt64():X}, {reason}");
    }

    private static bool? IsTaskbarAboveWindow(IntPtr hwnd, IntPtr tray)
    {
        if (!Interop.NativeMethods.IsWindow(hwnd) || !Interop.NativeMethods.IsWindow(tray)) return null;
        if (!GetWindowBand(hwnd, out uint targetBand) || !GetWindowBand(tray, out uint trayBand) || targetBand != trayBand)
            return null;

        IntPtr current = hwnd;
        for (int i = 0; i < 256; i++)
        {
            current = GetWindow(current, GW_HWNDPREV);
            if (current == IntPtr.Zero) return false;
            if (!Interop.NativeMethods.IsWindow(current)) return null;
            if (!GetWindowBand(current, out uint currentBand)) return null;
            if (currentBand != targetBand) return false;
            if (current == tray || GetAncestor(current, GA_ROOT) == tray) return true;
        }
        // Reaching the traversal bound is unknown, not proof that the taskbar is below the target.
        return null;
    }

    /// <summary>
    /// 截图遮罩在场时的处理结果。
    /// </summary>
    private enum OverlayYieldResult
    {
        /// <summary>没有截图遮罩，或遮罩不覆盖本窗口，无需处理。</summary>
        None,
        /// <summary>已经压到遮罩之下（或本就压在下面），调用方应直接返回。</summary>
        Handled,
        /// <summary>遮罩覆盖本窗口，但窗口与遮罩不在同一 z 序带，压不下去，需要折叠隐藏。</summary>
        NeedsCollapse,
    }

    private static bool IsBelowOverlay(IntPtr hwnd, IntPtr overlay)
    {
        IntPtr previous = GetWindow(hwnd, GW_HWNDPREV);
        for (int i = 0; i < 80 && previous != IntPtr.Zero; i++)
        {
            if (previous == overlay) return true;
            previous = GetWindow(previous, GW_HWNDPREV);
        }
        return false;
    }

    private static OverlayYieldResult TryYieldToCaptureOverlay(IntPtr hwnd)
    {
        IntPtr overlay = FindWindowW("SnipOverlayRootWindow", null);
        if (overlay == IntPtr.Zero || !IsWindowVisible(overlay)) return OverlayYieldResult.None;

        if (!Interop.NativeMethods.GetWindowRect(hwnd, out var windowRect)) return OverlayYieldResult.None;
        if (!Interop.NativeMethods.GetWindowRect(overlay, out var overlayRect) ||
            !CoversWindow(overlayRect, windowRect)) return OverlayYieldResult.None;

        GetWindowThreadProcessId(overlay, out uint overlayProcessId);
        if (overlayProcessId == 0) return OverlayYieldResult.None;

        // 窗口与遮罩不在同一个 z 序带（band）时，SetWindowPos 跨带无效，窗口会一直浮在
        // 遮罩的压暗层之上。这种状态下唯一可靠的做法是折叠窗口，等遮罩消失再恢复。
        // 正常启动时两者同带（均为 DESKTOP），走下面的常规压暗路径，行为不变。
        if (!GetWindowBand(hwnd, out uint windowBand) || !GetWindowBand(overlay, out uint overlayBand))
            return OverlayYieldResult.None;
        if (windowBand != overlayBand)
        {
            Log.Debug($"overlay band mismatch: hwnd=0x{hwnd.ToInt64():X} band={windowBand} overlayBand={overlayBand}; collapsing until overlay gone");
            return OverlayYieldResult.NeedsCollapse;
        }

        var owner = GetWindowLongPtr(hwnd, GWLP_HWNDPARENT);
        if (owner != IntPtr.Zero)
        {
            SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, IntPtr.Zero);
            if (GetWindowLongPtr(hwnd, GWLP_HWNDPARENT) != IntPtr.Zero) return OverlayYieldResult.Handled;
        }

        // 压暗是遮罩本体（SnipOverlayRootWindow）自己画的一层半透明覆盖，所以只有插到它
        // 正下方才会跟着一起变暗。早先这里插的是「遮罩组里最高的那个同进程窗口」，
        // 而那个窗口（XamlWindow）本身就压在遮罩之上 —— 胶囊被插在两者之间，
        // 依旧浮在压暗层上面，正是「截图时组件浮现、跟任务栏不在同一层」的成因。
        if (IsBelowOverlay(hwnd, overlay)) return OverlayYieldResult.Handled;

        return SetWindowPos(hwnd, overlay, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER)
            ? OverlayYieldResult.Handled : OverlayYieldResult.None;
    }

    private static bool CoversWindow(Interop.NativeMethods.RECT outer, Interop.NativeMethods.RECT inner)
    {
        return outer.left <= inner.left && outer.top <= inner.top &&
            outer.right >= inner.right && outer.bottom >= inner.bottom;
    }

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
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
    private static extern bool GetWindowBand(IntPtr hWnd, out uint pdwBand);

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

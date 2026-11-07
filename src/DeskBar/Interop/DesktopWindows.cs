using System.Runtime.InteropServices;

namespace DeskBar.Interop;

/// <summary>
/// 桌面窗口层级定位。
/// 本机实测（build 26200）的层级：
///   Progman 0x...15E ("Program Manager")
///     └─ SHELLDLL_DefView  (图标宿主)
///          └─ SysListView32 ("FolderView")
/// 坑：FindWindowExW 只能查直接子级，深层窗口必须用 EnumChildWindows 递归。
/// </summary>
internal static class DesktopWindows
{
    public static IntPtr FindProgman()
    {
        // 实测：FindWindowW("Progman", null) 与 FindWindowW("Progman","Program Manager")
        // 在某些会话下均可能返回 0；因此枚举顶层窗口按类名兜底。
        var h = NativeMethods.FindWindowW("Progman", "Program Manager");
        if (h != IntPtr.Zero) return h;
        h = NativeMethods.FindWindowW("Progman", null);
        if (h != IntPtr.Zero) return h;

        var result = IntPtr.Zero;
        NativeMethods.EnumWindows((w, _) =>
        {
            var sb = new System.Text.StringBuilder(64);
            if (NativeMethods.GetClassNameW(w, sb, sb.Capacity) > 0 && sb.ToString() == "Progman")
            {
                result = w;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>在 Progman / 所有 WorkerW 下递归查找指定类名的子窗口。</summary>
    public static IntPtr FindChildByClass(string className, params IntPtr[] roots)
    {
        foreach (var root in roots)
        {
            if (root == IntPtr.Zero) continue;
            var found = EnumForClass(root, className);
            if (found != IntPtr.Zero) return found;
        }
        return IntPtr.Zero;
    }

    private static IntPtr EnumForClass(IntPtr root, string className)
    {
        var result = IntPtr.Zero;
        NativeMethods.EnumChildWindows(root, (h, _) =>
        {
            var sb = new System.Text.StringBuilder(256);
            var len = NativeMethods.GetClassNameW(h, sb, sb.Capacity);
            if (len > 0 && sb.ToString() == className)
            {
                result = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>枚举所有顶层 WorkerW（桌面图标在某些配置下挂在这里）。</summary>
    public static List<IntPtr> FindAllWorkerW()
    {
        var list = new List<IntPtr>();
        var cur = IntPtr.Zero;
        while (true)
        {
            cur = NativeMethods.FindWindowExW(IntPtr.Zero, cur, "WorkerW", null);
            if (cur == IntPtr.Zero) break;
            list.Add(cur);
        }
        return list;
    }

    /// <summary>
    /// 定位 SHELLDLL_DefView —— 发 WM_COMMAND 切换图标时的目标窗口。
    /// 优先 Progman 下，再扫 WorkerW。
    /// </summary>
    public static IntPtr FindDefView()
    {
        var progman = FindProgman();
        var roots = new List<IntPtr>();
        if (progman != IntPtr.Zero) roots.Add(progman);
        roots.AddRange(FindAllWorkerW());
        var found = FindChildByClass("SHELLDLL_DefView", roots.ToArray());
        if (found != IntPtr.Zero) return found;
        // 兜底：某些系统版本 DefView 不在 Progman 下，直接扫顶层窗口的子级
        return FindChildByClass("SHELLDLL_DefView", roots.ToArray());
    }

    public static IntPtr FindDesktopListView()
    {
        var progman = FindProgman();
        var roots = new List<IntPtr>();
        if (progman != IntPtr.Zero) roots.Add(progman);
        roots.AddRange(FindAllWorkerW());
        return FindChildByClass("SysListView32", roots.ToArray());
    }

    /// <summary>判断某个窗口类名是否属于桌面（用于双击命中判定）。</summary>
    public static bool IsDesktopClass(string className) =>
        className is "Progman" or "WorkerW" or "SysListView32" or "SHELLDLL_DefView";
}

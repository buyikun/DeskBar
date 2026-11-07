using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskBar.Services
{
    /// <summary>
    /// 把指定进程的主窗口带到前台。
    /// 移植自 WaterSprite-TaskbarMediaBar 的 soda-player/window-control.cs。
    ///
    /// 同一份 PID 下可能有几十个顶层窗口（QQ音乐有 30 个以上，浏览器更多），
    /// 所以用 EnumWindows 打分挑主窗口，而不是用 Process.MainWindowHandle。
    /// SetForegroundWindow 会被 Windows 的前台锁定规则拒绝，因此这里有多级回退：
    /// 直接设置 → 附加前台输入上下文（AttachThreadInput）后再设置。
    /// 全程只还原最小化的窗口，绝不移动、缩放或持久置顶，避免打断用户。
    /// </summary>
    internal static class PlayerWindowControl
    {
        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int size);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
        [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr window, ref WindowPlacement placement);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

        [StructLayout(LayoutKind.Sequential)]
        private struct Message
        {
            public IntPtr window;
            public uint message;
            public UIntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int x, y;
            public uint extra;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int x, y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int left, top, right, bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowPlacement
        {
            public int length;
            public int flags;
            public int showCmd;
            public Point minPosition;
            public Point maxPosition;
            public Rect normalPosition;
        }

        private const int GwlExStyle = -20;
        private const long WsExToolWindow = 0x00000080L;

        /// <summary>主窗口的最小正常尺寸；低于此值的是托盘/辅助窗口（如 QQ音乐的 1x1 GDI+ Hook 窗口）。</summary>
        private const int MinMainWindowWidth = 200;
        private const int MinMainWindowHeight = 150;

        private const int SwRestore = 9;
        private const int SwShow = 5;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private const uint SwpAsyncWindowPos = 0x4000;

        /// <summary>最近一次 Open 的诊断串，便于在日志里定位失败原因。</summary>
        public static string LastDiagnostic { get; private set; } = string.Empty;

        /// <summary>
        /// 按 PID 枚举顶层窗口，返回最像主窗口的那个；没有可用的返回 IntPtr.Zero。
        ///
        /// 关键点：最小化的窗口 IsWindowVisible 为 false，且 GetWindowRect 只返回缩小后的尺寸，
        /// 因此用 GetWindowPlacement 的 normalPosition 判断真实大小，并给"已最小化"最高权重 ——
        /// QQ音乐这类播放器最小化到托盘时，主窗口恰恰就是"不可见 + 已最小化"的状态。
        /// </summary>
        public static IntPtr Window(int pid)
        {
            IntPtr best = IntPtr.Zero;
            int score = int.MinValue;

            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out uint owner);
                if (owner != (uint)pid) return true;

                // 工具窗口（托盘图标、右键菜单、提示条）不是主窗口
                long exStyle = GetWindowLongPtr(window, GwlExStyle).ToInt64();
                if ((exStyle & WsExToolWindow) != 0) return true;

                var placement = new WindowPlacement { length = Marshal.SizeOf<WindowPlacement>() };
                if (!GetWindowPlacement(window, ref placement)) return true;
                int width = placement.normalPosition.right - placement.normalPosition.left;
                int height = placement.normalPosition.bottom - placement.normalPosition.top;

                if (width < MinMainWindowWidth || height < MinMainWindowHeight) return true;

                var title = new StringBuilder(512);
                GetWindowText(window, title, title.Capacity);

                int value = 0;
                if (IsIconic(window)) value += 60000;              // 最小化的很可能就是主窗口
                else if (IsWindowVisible(window)) value += 50000;  // 可见的次之
                value += Math.Min(title.Length, 200);
                value += Math.Min(width * height / 2000, 30000);   // 面积越大越像主窗口
                if (title.Length == 0) value -= 25000;             // 无标题的辅助窗口降权

                if (value > score) { score = value; best = window; }
                return true;
            }, IntPtr.Zero);

            return best;
        }

        /// <summary>该进程是否有可用的主窗口。</summary>
        public static bool HasWindow(int pid) => Window(pid) != IntPtr.Zero;

        /// <summary>把该进程的主窗口提到前台。成功返回 true。</summary>
        public static bool Open(int pid)
        {
            LastDiagnostic = "pid=" + pid;

            IntPtr window = Window(pid);
            LastDiagnostic += ";window=" + window.ToInt64();
            if (window == IntPtr.Zero || IsHungAppWindow(window))
            {
                LastDiagnostic += ";missing-or-hung";
                return false;
            }

            // 只还原最小化的窗口；已经可见（含最大化）的窗口保持原尺寸。
            if (IsIconic(window))
            {
                ShowWindowAsync(window, SwRestore);
                System.Threading.Thread.Sleep(80);
            }
            else if (!IsWindowVisible(window))
            {
                ShowWindowAsync(window, SwShow);
            }

            // 提起一次，但不启用持久置顶、不移动、不缩放。
            SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoSize | SwpNoMove | SwpNoActivate | SwpShowWindow | SwpAsyncWindowPos);

            if (SetForegroundWindow(window) && GetForegroundWindow() == window)
            {
                LastDiagnostic += ";direct=true";
                return true;
            }

            // 回退：临时与其他线程共享前台输入上下文，绕过前台锁定规则。
            IntPtr foreground = GetForegroundWindow();
            uint foregroundThread = GetWindowThreadProcessId(foreground, out _);
            uint currentThread = GetCurrentThreadId();
            uint targetThread = GetWindowThreadProcessId(window, out _);
            bool attached = false, targetAttached = false;
            try
            {
                PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
                if (foreground != IntPtr.Zero && !IsHungAppWindow(foreground) &&
                    foregroundThread != 0 && foregroundThread != currentThread)
                {
                    attached = AttachThreadInput(currentThread, foregroundThread, true);
                }
                if (targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread)
                {
                    targetAttached = AttachThreadInput(currentThread, targetThread, true);
                }

                BringWindowToTop(window);
                bool result = SetForegroundWindow(window);
                LastDiagnostic += $";attached={attached};targetAttached={targetAttached};activate={result};foreground={GetForegroundWindow().ToInt64()}";
                return GetForegroundWindow() == window;
            }
            finally
            {
                if (targetAttached) AttachThreadInput(currentThread, targetThread, false);
                if (attached) AttachThreadInput(currentThread, foregroundThread, false);
            }
        }

        /// <summary>
        /// 把任意窗口提到前台（与 Open 相同的三级前台策略，但不做还原和置位动作）。
        /// 供需要临时获得前台权的场景复用，例如在 WS_EX_NOACTIVATE 窗口上弹出上下文菜单。
        /// </summary>
        public static bool ActivateWindow(IntPtr window)
        {
            if (window == IntPtr.Zero) return false;

            if (SetForegroundWindow(window) && GetForegroundWindow() == window) return true;

            IntPtr foreground = GetForegroundWindow();
            uint foregroundThread = GetWindowThreadProcessId(foreground, out _);
            uint currentThread = GetCurrentThreadId();
            uint targetThread = GetWindowThreadProcessId(window, out _);
            bool attached = false, targetAttached = false;
            try
            {
                PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
                if (foreground != IntPtr.Zero && !IsHungAppWindow(foreground) &&
                    foregroundThread != 0 && foregroundThread != currentThread)
                {
                    attached = AttachThreadInput(currentThread, foregroundThread, true);
                }
                if (targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread)
                {
                    targetAttached = AttachThreadInput(currentThread, targetThread, true);
                }

                BringWindowToTop(window);
                SetForegroundWindow(window);
                return GetForegroundWindow() == window;
            }
            finally
            {
                if (targetAttached) AttachThreadInput(currentThread, targetThread, false);
                if (attached) AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }
}

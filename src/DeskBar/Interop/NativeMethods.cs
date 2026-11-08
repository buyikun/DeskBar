using System.Runtime.InteropServices;

namespace DeskBar.Interop;

/// <summary>
/// Win32 互操作声明。常量与签名均经本机实测（Windows 11 build 26200）验证。
/// 两条本机实测坑：
///   1) 判断系统版本必须用 CurrentBuild，不能用 ProductName（本机 ProductName 仍是 "Windows 10"）。
///   2) FindWindowW("Progman", null) 匹配不到，必须带标题 "Program Manager"。
///   3) FindWindowExW 只查直接子级，深层窗口必须 EnumChildWindows 递归。
/// </summary>
internal static class NativeMethods
{
    private const string User32 = "user32.dll";
    private const string Kernel32 = "kernel32.dll";
    private const string Advapi32 = "advapi32.dll";
    private const string Shell32 = "shell32.dll";
    private const string Dwmapi = "dwmapi.dll";

    // ---- 窗口查找 ----
    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindowExW(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterWindowMessageW(string lpString);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, [MarshalAs(UnmanagedType.Bool)] bool bErase);

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);



    [DllImport("gdi32.dll")]

    public static extern IntPtr ExtCreateRegion(IntPtr lpXform, uint nCount, byte[] lpRgnData);

    [DllImport("gdi32.dll")]
    public static extern int CombineRgn(IntPtr hrgnDest, IntPtr hrgnSrc1, IntPtr hrgnSrc2, int fnCombineMode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport(User32)]
    public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);

    [DllImport(User32)]
    public static extern IntPtr GetForegroundWindow();

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>
    /// 取窗口的「扩展边框」矩形。Win11 上 GetWindowRect 会把不可见的拖拽边框算进去，
    /// 导致任务栏实际可见高度比返回值小 1-3px，浮岛会贴出边界或压住图标。
    /// DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS=9) 返回的是真实绘制区域。
    /// 对应 WaterSprite `shell-bridge.ps1:81` 的 Bounds 实现。
    /// </summary>
    [DllImport(Dwmapi)]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [DllImport(User32)]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    public const uint MONITOR_DEFAULTTONULL = 0;
    public const uint MONITOR_DEFAULTTOPRIMARY = 1;
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport(User32, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    /// <summary>
    /// 优先用 DWM 扩展边框，失败时回退 GetWindowRect。
    /// 结果为空矩形（right&lt;=left 或 bottom&lt;=top）时同样回退。
    /// </summary>
    public static bool TryGetWindowBounds(IntPtr hWnd, out RECT rect)
    {
        if (hWnd == IntPtr.Zero)
        {
            rect = default;
            return false;
        }

        try
        {
            if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var dwm, Marshal.SizeOf<RECT>()) == 0
                && dwm.right > dwm.left && dwm.bottom > dwm.top)
            {
                rect = dwm;
                return true;
            }
        }
        catch { }

        return GetWindowRect(hWnd, out rect) && rect.right > rect.left && rect.bottom > rect.top;
    }

    public const int RGN_AND = 1;
    public const int RGN_OR = 2;
    public const int RGN_DIFF = 4;

    // ---- WinEventHook ----
    public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport(User32, SetLastError = true)]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    public const uint EVENT_SYSTEM_CAPTURESTART = 0x0008;
    public const uint WINEVENT_OUTOFCONTEXT = 0;

    // ---- 鼠标钩子 ----
    [DllImport(User32, SetLastError = true)]
    public static extern IntPtr SetWindowsHookExW(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport(User32)]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport(Kernel32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport(Kernel32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport(Kernel32, CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport(Kernel32)]
    public static extern uint GetCurrentThreadId();

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport(User32, SetLastError = true)]
    public static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport(User32, SetLastError = true)]
    public static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessageW(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport(User32)]
    public static extern IntPtr WindowFromPoint(POINT point);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport(User32)]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport(User32, SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport(User32)]
    public static extern uint GetDoubleClickTime();

    [DllImport(User32)]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterWindowMessage(string lpString);

    // ---- 注册表 ----
    [DllImport(Advapi32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int RegOpenKeyExW(IntPtr hKey, string lpSubKey, uint ulOptions, int samDesired, out IntPtr phkResult);

    [DllImport(Advapi32, SetLastError = true)]
    public static extern int RegNotifyChangeKeyValue(IntPtr hKey, bool bWatchSubtree, int dwNotifyFilter,
        IntPtr hEvent, bool fAsynchronous);

    [DllImport(Advapi32, SetLastError = true)]
    public static extern int RegCloseKey(IntPtr hKey);

    // ---- 进程 / 内存（跨进程 hit-test）----
    [DllImport(Kernel32, SetLastError = true)]
    public static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport(Kernel32, SetLastError = true)]
    public static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, IntPtr dwSize, uint flAllocationType, uint flProtect);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, IntPtr dwSize, uint dwFreeType);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesWritten);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesRead);

    [DllImport(Kernel32, SetLastError = true)]
    public static extern IntPtr CreateEventW(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport(Kernel32, SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    // ---- 任务栏 ----
    [DllImport(Shell32, SetLastError = true)]
    public static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    // ================= 常量 =================
    public const int WM_COMMAND = 0x0111;
    /// <summary>资源管理器“显示桌面图标”菜单命令 ID。发这条命令让 shell 自己切，注册表状态由它维护。</summary>
    public const int ToggleDesktopIconsCommand = 29698;

    public const uint WM_SETTINGCHANGE = 0x001A;
    public static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    public const int WH_MOUSE_LL = 14;
    public const int WM_QUIT = 0x0012;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int VK_LBUTTON = 0x01;

    public const int SM_CXDOUBLECLK = 36;
    public const int SM_CYDOUBLECLK = 37;

    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;
    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_SHOWNA = 8;

    public const int HKEY_CURRENT_USER = unchecked((int)0x80000001);
    public const int KEY_NOTIFY = 0x0010;
    public const int KEY_QUERY_VALUE = 0x0001;
    public const int REG_NOTIFY_CHANGE_LAST_SET = 0x00000004;

    public const uint ABM_GETSTATE = 4;
    public const uint ABM_SETSTATE = 10;
    public const int ABS_AUTOHIDE = 0x01;
    public const int ABS_ALWAYSONTOP = 0x02;

    // 跨进程 hit-test
    public const int PROCESS_VM_OPERATION = 0x0008;
    public const int PROCESS_VM_READ = 0x0010;
    public const int PROCESS_VM_WRITE = 0x0020;
    public const uint MEM_COMMIT = 0x1000;
    public const uint MEM_RESERVE = 0x2000;
    public const uint MEM_RELEASE = 0x8000;
    public const uint PAGE_READWRITE = 0x04;

    /// <summary>LVM_FIRST (0x1000) + 4</summary>
    public const uint LVM_GETITEMCOUNT = 0x1004;
    /// <summary>LVM_FIRST (0x1000) + 18 (0x12) = LVM_HITTEST</summary>
    public const uint LVM_HITTEST = 0x1012;

    public const uint WAIT_OBJECT_0 = 0;

    // ================= 结构 =================
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LVHITTESTINFO
    {
        public POINT pt;
        public uint flags;
        public int iItem;
        public int iSubItem;
        public int iGroup;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }
}

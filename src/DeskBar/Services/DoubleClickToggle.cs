using System.Runtime.InteropServices;
using DeskBar.Interop;

namespace DeskBar.Services;

/// <summary>
/// 双击桌面空白切换图标显隐。
/// 架构：
///   直接挂载在主 UI 线程的 Windows 消息泵上（由 WPF Dispatcher / WinForms NotifyIcon 原生提供消息队列和消息泵）。
///   - 零多余线程开销；
///   - 依托已就绪的有效消息窗口，彻底避免后台纯控制台线程无 HWND 导致系统静默丢弃钩子回调；
///   - 委托字段保持强引用，生命周期与应用一致。
/// </summary>
internal sealed class DoubleClickToggle : IDisposable
{
    private readonly DesktopIconService _icons;
    private readonly NativeMethods.LowLevelMouseProc _mouseHookProc;
    private IntPtr _hookId = IntPtr.Zero;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private volatile bool _enabled = true;

    private DateTime _lastPressTime = DateTime.MinValue;
    private int _lastX, _lastY;
    private bool _hasPrev;

    public DoubleClickToggle(DesktopIconService icons)
    {
        _icons = icons;
        // 强引用委托防止 GC
        _mouseHookProc = MouseHookCallback;
    }

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public void Install()
    {
        if (_hookThread != null) return;
        var ready = new ManualResetEventSlim(false);

        _hookThread = new Thread(() =>
        {
            _hookThreadId = NativeMethods.GetCurrentThreadId();

            // WH_MOUSE_LL 全局低级鼠标钩子
            // 在 Medium Mandatory Level 下无视 UIPI 拦截，零延迟捕获全屏鼠标
            _hookId = NativeMethods.SetWindowsHookExW(
                NativeMethods.WH_MOUSE_LL,
                _mouseHookProc,
                IntPtr.Zero,
                0);

            if (_hookId == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                Log.Error($"SetWindowsHookExW failed, win32 error={err}");
                ready.Set();
                return;
            }

            Log.Info($"WH_MOUSE_LL installed on message thread, hookId=0x{_hookId:X}, tid={_hookThreadId}");
            ready.Set();

            // 原生 Win32 消息泵驱动底层钩子
            while (NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessageW(ref msg);
            }

            if (_hookId != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
            Log.Info("Mouse hook thread exited");
        })
        {
            IsBackground = true,
            Name = "DeskBar_MouseHookThread"
        };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
        ready.Wait(2000);
    }

    public void Uninstall()
    {
        if (_hookThreadId != 0)
        {
            NativeMethods.PostThreadMessageW(_hookThreadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _hookThread?.Join(1000);
            _hookThread = null;
            _hookThreadId = 0;
            Log.Info("Mouse hook uninstalled");
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _enabled && wParam.ToInt32() == NativeMethods.WM_LBUTTONDOWN)
        {
            try
            {
                var hookStruct = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                var x = hookStruct.pt.x;
                var y = hookStruct.pt.y;

                var pt = new NativeMethods.POINT { x = x, y = y };
                var hwnd = NativeMethods.WindowFromPoint(pt);
                if (hwnd != IntPtr.Zero)
                {
                    var cls = ClassOfHwnd(hwnd);
                    // 仅当点击真正命中桌面背景窗口时才处理，游戏/应用内点击瞬间放行（零延迟、零日志开销）
                    if (DesktopWindows.IsDesktopClass(cls))
                    {
                        // 寻找真实的桌面 SysListView32 图标容器窗口
                        var listView = cls == "SysListView32"
                            ? hwnd
                            : NativeMethods.FindWindowExW(hwnd, IntPtr.Zero, "SysListView32", null);
                        if (listView == IntPtr.Zero)
                        {
                            listView = DesktopWindows.FindDesktopListView();
                        }

                        // 如果存在桌面列表视图且命中了具体图标，则判定为点击图标，绝不触发隐藏！
                        bool hitIcon = listView != IntPtr.Zero && HitTestIcon(listView, x, y);
                        bool isBlank = !hitIcon;
                        if (isBlank)
                        {
                            var now = DateTime.UtcNow;
                            var dblMs = NativeMethods.GetDoubleClickTime();
                            if (dblMs == 0) dblMs = 500;
                            var cx = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXDOUBLECLK);
                            if (cx == 0) cx = 4;
                            var cy = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYDOUBLECLK);
                            if (cy == 0) cy = 4;

                            if (_hasPrev)
                            {
                                var elapsed = (now - _lastPressTime).TotalMilliseconds;
                                if (elapsed >= 30 && elapsed <= dblMs && Math.Abs(x - _lastX) <= cx && Math.Abs(y - _lastY) <= cy)
                                {
                                    _hasPrev = false;
                                    ThreadPool.QueueUserWorkItem(_ =>
                                    {
                                        try
                                        {
                                            var visible = _icons.Toggle();
                                            Log.Info($"[MOUSE_LL] Double-click on desktop blank! ({x},{y}) -> icons visible={visible}");
                                        }
                                        catch (Exception ex)
                                        {
                                            Log.Error($"[MOUSE_LL] Toggle failed: {ex.Message}");
                                        }
                                    });
                                }
                                else if (elapsed >= 30)
                                {
                                    _lastPressTime = now;
                                    _lastX = x;
                                    _lastY = y;
                                    _hasPrev = true;
                                }
                            }
                            else
                            {
                                _lastPressTime = now;
                                _lastX = x;
                                _lastY = y;
                                _hasPrev = true;
                            }
                        }
                        else
                        {
                            _hasPrev = false;
                        }
                    }
                    else
                    {
                        _hasPrev = false;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[MOUSE_LL] Callback error: {ex.Message}");
            }
        }
        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static string ClassOfHwnd(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "NULL";
        var sb = new System.Text.StringBuilder(256);
        NativeMethods.GetClassNameW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>点是否落在桌面空白（非图标、非其他窗口）。</summary>
    private static bool IsDesktopBlank(IntPtr hwnd, int x, int y)
    {
        if (hwnd == IntPtr.Zero) return false;
        var cls = ClassOfHwnd(hwnd);

        if (!DesktopWindows.IsDesktopClass(cls)) return false;

        if (cls == "SysListView32" && HitTestIcon(hwnd, x, y)) return false;

        return true;
    }

    private static string ClassAt(int x, int y)
    {
        var pt = new NativeMethods.POINT { x = x, y = y };
        var hwnd = NativeMethods.WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return "NULL";
        var sb = new System.Text.StringBuilder(256);
        NativeMethods.GetClassNameW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static bool HitTestIcon(IntPtr listView, int screenX, int screenY)
    {
        NativeMethods.GetWindowThreadProcessId(listView, out var pid);
        if (pid == 0) return false;

        var hProc = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_VM_OPERATION | NativeMethods.PROCESS_VM_READ | NativeMethods.PROCESS_VM_WRITE,
            false, pid);
        if (hProc == IntPtr.Zero) return false;

        var size = Marshal.SizeOf<NativeMethods.LVHITTESTINFO>();
        IntPtr remote = IntPtr.Zero;
        IntPtr local = IntPtr.Zero;
        try
        {
            remote = NativeMethods.VirtualAllocEx(hProc, IntPtr.Zero, new IntPtr(size),
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE, NativeMethods.PAGE_READWRITE);
            if (remote == IntPtr.Zero) return false;

            var client = new NativeMethods.POINT { x = screenX, y = screenY };
            if (!NativeMethods.ScreenToClient(listView, ref client)) return false;

            var info = new NativeMethods.LVHITTESTINFO { pt = client, iItem = -1 };

            local = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(info, local, false);
            var buf = new byte[size];
            Marshal.Copy(local, buf, 0, size);

            if (!NativeMethods.WriteProcessMemory(hProc, remote, buf, new IntPtr(size), out _))
                return false;

            var ret = NativeMethods.SendMessageW(listView, NativeMethods.LVM_HITTEST, IntPtr.Zero, remote);
            if (ret.ToInt64() < 0) return false;

            if (!NativeMethods.ReadProcessMemory(hProc, remote, buf, new IntPtr(size), out _))
                return false;
            Marshal.Copy(buf, 0, local, size);
            var result = Marshal.PtrToStructure<NativeMethods.LVHITTESTINFO>(local);
            return result.iItem >= 0 || (result.flags & 0x0E) != 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"hit-test failed: {ex.Message}");
            return false;
        }
        finally
        {
            if (local != IntPtr.Zero) Marshal.FreeHGlobal(local);
            if (remote != IntPtr.Zero)
                NativeMethods.VirtualFreeEx(hProc, remote, IntPtr.Zero, NativeMethods.MEM_RELEASE);
            NativeMethods.CloseHandle(hProc);
        }
    }

    public void Dispose() => Uninstall();
}

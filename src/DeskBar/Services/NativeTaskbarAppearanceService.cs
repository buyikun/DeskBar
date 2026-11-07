using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using DeskBar.Interop;

namespace DeskBar.Services;

public enum NativeTaskbarBrush
{
    Acrylic = 0,
    SolidColor = 1
}

[ComImport]
[Guid("5bcf9150-c28a-4ef2-913c-4c3ea2f5ead0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ITaskbarAppearanceService
{
    [PreserveSig]
    int SetTaskbarAppearance(IntPtr taskbar, NativeTaskbarBrush brush, uint color);

    [PreserveSig]
    int SetTaskbarBlur(IntPtr taskbar, uint color, float blurAmount);

    [PreserveSig]
    int ReturnTaskbarToDefaultAppearance(IntPtr taskbar);

    [PreserveSig]
    int SetTaskbarBorderVisibility(IntPtr taskbar, bool visible);

    [PreserveSig]
    int RestoreAllTaskbarsToDefault();

    [PreserveSig]
    int RestoreAllTaskbarsToDefaultWhenProcessDies(uint pid);

    [PreserveSig]
    int KillExplorerWhenPackageUninstalls([MarshalAs(UnmanagedType.LPWStr)] string packageFullName);
}

/// <summary>
/// 纯原生 Windows 11 任务栏外观引擎（完全脱离外部 TB 进程，通过 COM 接口无感直控 Explorer XAML）
/// </summary>
public static class NativeTaskbarAppearanceService
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InjectExplorerTAPDelegate(IntPtr window, ref Guid riid, out ITaskbarAppearanceService ppv);

    private static readonly Guid IID_ITaskbarAppearanceService = new("5bcf9150-c28a-4ef2-913c-4c3ea2f5ead0");
    private static ITaskbarAppearanceService? _service;
    private static IntPtr _hModule = IntPtr.Zero;
    private static InjectExplorerTAPDelegate? _injectFunc;
    private static readonly object _lock = new();

    private static string _currentStyle = "acrylic";
    private static string _currentTone = "natural";
    private static int _currentBlur = 18;
    // SetTaskbarBlur 是否真的被 ExplorerTAP.dll 实现（null = 还没探测过）。
    // ExplorerTAP 的导出集合因版本而异：有的版本只有 SetTaskbarAppearance，
    // 调用不存在的导出在 COM 里表现为返回失败而不是抛异常，光看返回值不探测就会静默丢外观。
    private static bool? _blurSupported;
    private static System.Threading.Timer? _keepAliveTimer;

    // 注入失败的退避与状态。注入可能因 ExplorerTAP.dll 缺失/版本不匹配/Explorer 正在重启
    // 而失败（与权限无关：Explorer 与本进程同为 Medium IL，同级别注入是允许的），
    // 而任务栏几何扫描是 100ms 级的循环，如果每次调用来问「能用吗」都真去注入一次，
    // 既会把日志刷成上万行（实测该日志一度膨胀到无法检索），也会拖慢扫描循环本身。
    // 因此失败后 30 秒内一律直接回答「不可用」。
    private const int InjectRetryBackoffMs = 30000;
    private static volatile bool _injectFailed;
    private static int _lastInjectFailureTick;
    private static int _lastInjectFailureLogTick;

    /// <summary>
    /// 外观引擎是否真的接管了 Explorer 任务栏。
    /// 岛模式的区域裁剪必须以此为前提：注入失败时任务栏仍是原生不透明层，
    /// 裁剪只会把开始区/时钟区挖掉，而没有任何磨砂层来补偿视觉。
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            if (_service != null) return true;
            if (_injectFailed &&
                unchecked(Environment.TickCount - _lastInjectFailureTick) < InjectRetryBackoffMs)
            {
                return false;
            }
            try { return EnsureInitialized() && _service != null; }
            catch { return false; }
        }
    }

    /// <summary>
    /// 当前进程是否以管理员身份运行。
    /// 注意：任务栏外观接管并不需要管理员权限 —— Explorer 与本进程同为 Medium IL，
    /// 同级别注入本就是允许的。保留此属性仅供诊断信息展示，不再作为功能前提。
    /// </summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(identity)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// 记录一次注入失败并开启退避窗口。日志按退避周期限流，
    /// 保证「任务栏为什么没被接管」这件事仍然可见，但不会淹没整个日志。
    /// </summary>
    private static void MarkInjectFailure(string message)
    {
        _injectFailed = true;
        int now = Environment.TickCount;
        _lastInjectFailureTick = now;
        if (_lastInjectFailureLogTick == 0 ||
            unchecked(now - _lastInjectFailureLogTick) >= InjectRetryBackoffMs)
        {
            _lastInjectFailureLogTick = now;
            Log.Error(message +
                      " — ExplorerTAP 注入失败，已退回整条磨砂模式（居中止岛需要注入成功才生效）。" +
                      "常见原因是 ExplorerTAP.dll 缺失或版本与当前 Explorer 不匹配，或 Explorer 正在重启；" +
                      "与是否以管理员身份运行无关。");
        }
    }

    static NativeTaskbarAppearanceService()
    {
        // 启动后台轻量自愈心跳（每 3 秒校验一次任务栏句柄与外观，防止 Explorer 重绘或休眠唤醒后掉色）
        _keepAliveTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                RefreshCurrentAppearance();
            }
            catch { }
        }, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    private static bool EnsureInitialized()
    {
        lock (_lock)
        {
            if (_service != null) return true;

            // 退避窗口内不再重复注入 Explorer。
            if (_injectFailed &&
                unchecked(Environment.TickCount - _lastInjectFailureTick) < InjectRetryBackoffMs)
            {
                return false;
            }

            try
            {
                if (_hModule == IntPtr.Zero)
                {
                    var candidates = new System.Collections.Generic.List<string>();
                    if (!string.IsNullOrEmpty(Environment.ProcessPath))
                    {
                        var dir = Path.GetDirectoryName(Environment.ProcessPath);
                        if (!string.IsNullOrEmpty(dir)) candidates.Add(Path.Combine(dir, "ExplorerTAP.dll"));
                    }
                    candidates.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExplorerTAP.dll"));
                    candidates.Add(Path.Combine(AppContext.BaseDirectory, "ExplorerTAP.dll"));
                    candidates.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NativeBinaries", "ExplorerTAP.dll"));
                    candidates.Add(Path.Combine(AppContext.BaseDirectory, "NativeBinaries", "ExplorerTAP.dll"));
                    candidates.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "TranslucentTB", "ExplorerTAP.dll"));
                    candidates.Add(@"D:\app\DeskBar\ExplorerTAP.dll");

                    string? dllPath = null;
                    foreach (var c in candidates)
                    {
                        if (File.Exists(c))
                        {
                            dllPath = c;
                            break;
                        }
                    }

                    if (string.IsNullOrEmpty(dllPath))
                    {
                        Log.Error($"ExplorerTAP.dll not found in any candidate path. BaseDir={AppDomain.CurrentDomain.BaseDirectory}, ProcessPath={Environment.ProcessPath}");
                        return false;
                    }

                    Log.Info($"Loading ExplorerTAP.dll from: {dllPath}");
                    _hModule = NativeMethods.LoadLibrary(dllPath);
                    if (_hModule == IntPtr.Zero)
                    {
                        Log.Error($"LoadLibrary ExplorerTAP.dll failed from {dllPath}, error={Marshal.GetLastWin32Error()}");
                        return false;
                    }
                }

                if (_injectFunc == null)
                {
                    var proc = NativeMethods.GetProcAddress(_hModule, "InjectExplorerTAP");
                    if (proc == IntPtr.Zero)
                    {
                        Log.Error("GetProcAddress InjectExplorerTAP failed");
                        return false;
                    }
                    _injectFunc = Marshal.GetDelegateForFunctionPointer<InjectExplorerTAPDelegate>(proc);
                }

                var taskbar = NativeMethods.FindWindowW("Shell_TrayWnd", null);
                if (taskbar == IntPtr.Zero)
                {
                    Log.Error("Shell_TrayWnd not found");
                    return false;
                }

                var guid = IID_ITaskbarAppearanceService;
                int hr = _injectFunc(taskbar, ref guid, out var service);
                if (hr >= 0 && service != null)
                {
                    _service = service;
                    _injectFailed = false;
                    _blurSupported = null; // 新连接要重新探测，不同 Explorer 会话可能加载了不同版本的 TAP
                    // 设置退出时恢复，保障极端异常退出时系统任务栏不损坏
                    try
                    {
                        _service.RestoreAllTaskbarsToDefaultWhenProcessDies((uint)Process.GetCurrentProcess().Id);
                    }
                    catch { }
                    Log.Info("NativeTaskbarAppearanceService initialized successfully via COM.");
                    return true;
                }
                else
                {
                    MarkInjectFailure($"InjectExplorerTAP returned 0x{hr:X8}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                MarkInjectFailure($"EnsureInitialized exception: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// 应用外观设置：style ("acrylic" / "transparent" / "default")，tone ("natural" / "light" / "dark" / "auto")
    /// </summary>
    public static void Apply(string style, string tone, int blur = 18)
    {
        _currentStyle = style?.ToLowerInvariant() ?? "acrylic";
        _currentTone = tone?.ToLowerInvariant() ?? "natural";
        _currentBlur = blur;
        RefreshCurrentAppearance();
    }

    /// <summary>
    /// 原生重绘并保持当前任务栏外观
    /// </summary>
    public static void RefreshCurrentAppearance()
    {
        var taskbar = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return;

        if (!EnsureInitialized() || _service == null) return;

        try
        {
            if (_currentStyle == "transparent")
            {
                // 全透明：SolidColor 配合全透明 Alpha
                _service.SetTaskbarAppearance(taskbar, NativeTaskbarBrush.SolidColor, 0x00000000);
                _service.SetTaskbarBorderVisibility(taskbar, false);
            }
            else if (_currentStyle == "acrylic")
            {
                uint color = GetColorForTone(_currentTone);
                // 优先走可调模糊度接口；但 ExplorerTAP.dll 这个导出的实现并非所有版本都认这条路径，
                // 一旦它返回失败，任务栏会彻底失去磨砂层（看起来像"外观掉了"），
                // 所以必须探测一次并永久记住结果，失败时回退到旧的 SetTaskbarAppearance。
                if (_blurSupported != false)
                {
                    int blurHr = _service.SetTaskbarBlur(taskbar, color, (float)_currentBlur);
                    if (blurHr >= 0)
                    {
                        if (_blurSupported == null)
                        {
                            _blurSupported = true;
                            Log.Info($"SetTaskbarBlur available, blur={_currentBlur}");
                        }
                    }
                    else
                    {
                        _blurSupported = false;
                        Log.Error($"SetTaskbarBlur returned 0x{blurHr:X8}, falling back to SetTaskbarAppearance(Acrylic) — 模糊度滑块将不再影响任务栏");
                        _service.SetTaskbarAppearance(taskbar, NativeTaskbarBrush.Acrylic, color);
                    }
                }
                else
                {
                    _service.SetTaskbarAppearance(taskbar, NativeTaskbarBrush.Acrylic, color);
                }
                _service.SetTaskbarBorderVisibility(taskbar, false);
            }
            else
            {
                _service.ReturnTaskbarToDefaultAppearance(taskbar);
                _service.SetTaskbarBorderVisibility(taskbar, true);
            }
        }
        catch (COMException)
        {
            // 如果 Explorer 发生过重启导致 COM 断开，重置服务引用以便下次自动重连自愈
            lock (_lock)
            {
                _service = null;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"RefreshCurrentAppearance error: {ex.Message}");
        }
    }

    public static uint GetColorForTone(string tone)
    {
        return tone switch
        {
            "softmist" => 0x25525252, // 浅色：柔雾 (减少白色覆盖)
            "sheertights" => 0x25323232, // 深色：黑丝 (沉稳透感)
            "light" => 0xA0FFFFFF,    // 白玉磨砂毛玻璃
            "dark" => 0x90202020,     // 黑曜石磨砂毛玻璃
            "natural" => 0x00000000,  // 图中原味透出壁纸
            // 注意加 u 后缀：0xA0FFFFFF 这类高位字面量是 uint，而 0x25525252 是 int，
            // 混用会让整个 switch 表达式的类型推断失败（CS0029）。
            "auto" => IsSystemInLightTheme() ? 0x25525252u : 0x25323232u,
            _ => 0x00000000
        };
    }

    public static bool IsSystemInLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false);
            if (key?.GetValue("SystemUsesLightTheme") is int v)
            {
                return v == 1;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 退出时还原任务栏
    /// </summary>
    public static void RestoreDefault()
    {
        try
        {
            var taskbar = NativeMethods.FindWindowW("Shell_TrayWnd", null);
            if (taskbar != IntPtr.Zero && _service != null)
            {
                _service.ReturnTaskbarToDefaultAppearance(taskbar);
                _service.SetTaskbarBorderVisibility(taskbar, true);
            }
        }
        catch { }
    }
}

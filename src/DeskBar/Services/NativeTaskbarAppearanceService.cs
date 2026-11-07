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
    private static System.Threading.Timer? _keepAliveTimer;

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
                    Log.Error($"InjectExplorerTAP returned 0x{hr:X8}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"EnsureInitialized exception: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// 应用外观设置：style ("acrylic" / "transparent" / "default")，tone ("natural" / "light" / "dark" / "auto")
    /// </summary>
    public static void Apply(string style, string tone)
    {
        _currentStyle = style?.ToLowerInvariant() ?? "acrylic";
        _currentTone = tone?.ToLowerInvariant() ?? "natural";
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
                _service.SetTaskbarAppearance(taskbar, NativeTaskbarBrush.Acrylic, color);
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
            "light" => 0xA0FFFFFF,    // 白玉磨砂毛玻璃（高反射阻断壁纸杂色）
            "dark" => 0x90202020,     // 黑曜石磨砂毛玻璃（沉稳高对比）
            "natural" => 0x00000000,  // 图中原味透出壁纸自然毛玻璃
            "auto" => IsSystemInLightTheme() ? 0xA0FFFFFF : 0x90202020,
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

using Microsoft.Win32;
using DeskBar.Interop;

namespace DeskBar.Services;

/// <summary>
/// 桌面图标显隐。
/// 双路径策略（本机实测支撑）：
///   - 状态：读 HKCU\...\Explorer\Advanced\HideIcons（1 = 已隐藏），另以 ListView 可见性作交叉校验；
///   - 切换/显式设置：对 SysListView32 执行 ShowWindow(SW_HIDE / SW_SHOW) 后失效重绘，日志留痕（SW 结果、Win32 错误码、前后可见性）；
///   - 找不到桌面 ListView 时只告警，不改变任何状态。
/// </summary>
internal sealed class DesktopIconService
{
    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string HideIconsValue = "HideIcons";

    /// <summary>从注册表读当前状态。true = 图标可见。</summary>
    public bool? ReadVisibleFromRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AdvancedKey, writable: false);
            if (key?.GetValue(HideIconsValue) is not int hide) return null;
            return hide == 0;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从 ListView 可见性读状态（作为交叉校验）。</summary>
    public bool IsListViewVisible()
    {
        var lv = DesktopWindows.FindDesktopListView();
        return lv != IntPtr.Zero && NativeMethods.IsWindowVisible(lv);
    }

    /// <summary>
    /// 切换图标显隐。返回切换后的“是否可见”。
    /// </summary>
    public bool Toggle()
    {
        var lv = DesktopWindows.FindDesktopListView();
        if (lv == IntPtr.Zero)
        {
            Log.Warn("Toggle: desktop listview not found; cannot toggle");
            return true;
        }

        var currentlyVisible = NativeMethods.IsWindowVisible(lv);
        var targetCmd = currentlyVisible ? NativeMethods.SW_HIDE : NativeMethods.SW_SHOW;
        
        var swRes = NativeMethods.ShowWindow(lv, targetCmd);
        var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        NativeMethods.InvalidateRect(lv, IntPtr.Zero, true);
        NativeMethods.UpdateWindow(lv);

        var newVisible = NativeMethods.IsWindowVisible(lv);
        Log.Info($"Toggle: ShowWindow(0x{lv:X8}, {targetCmd}) => swRes={swRes}, err={err}, visibleBefore={currentlyVisible}, visibleAfter={newVisible}");
        return newVisible;
    }

    /// <summary>显式设置可见性（用于设置面板的开关）。</summary>
    public bool SetVisible(bool visible)
    {
        var lv = DesktopWindows.FindDesktopListView();
        if (lv == IntPtr.Zero) return visible;

        var current = NativeMethods.IsWindowVisible(lv);
        if (current == visible) return current;
        return Toggle();
    }
}

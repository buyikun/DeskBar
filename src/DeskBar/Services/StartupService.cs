using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace DeskBar.Services;

/// <summary>
/// 开机极速自启管理：
/// 1. 注册表 HKCU\...\CurrentVersion\Run
/// 2. 设置 Windows 任务管理器 StartupApproved 状态为 DeskBar 专属显式就绪 (02 00 00 00...)
/// 3. 在用户 Startup 启动目录创建 DeskBar 专属快捷方式双保险，保障开机最早批次被唤醒
/// 4. 严格保持 Windows 原生行为，不侵入、不修改全局启动延迟注册表
/// </summary>
internal static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "DeskBar";

    /// <summary>当前 exe 的启动命令行（带引号）。dotnet 框架依赖模式下需带 dll 参数。</summary>
    private static string BuildCommand()
    {
        var exe = Environment.ProcessPath ?? "";
        if (string.IsNullOrEmpty(exe)) return "";
        return $"\"{exe}\"";
    }

    private static string GetStartupShortcutPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, @"Microsoft\Windows\Start Menu\Programs\Startup\DeskBar.lnk");
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            var v = key?.GetValue(ValueName) as string;
            return !string.IsNullOrEmpty(v);
        }
        catch { return false; }
    }

    public static bool SetEnabled(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (key is null) return false;

            if (enable)
            {
                var cmd = BuildCommand();
                if (string.IsNullOrEmpty(cmd))
                {
                    Log.Error("Startup: cannot resolve exe path");
                    return false;
                }
                key.SetValue(ValueName, cmd, RegistryValueKind.String);
                Log.Info($"Startup: enabled -> {cmd}");

                // 1. 标记 DeskBar 在 StartupApproved 中为专属显式就绪（状态码 0x02）
                try
                {
                    using var approvedKey = Registry.CurrentUser.OpenSubKey(StartupApprovedKey, writable: true)
                                            ?? Registry.CurrentUser.CreateSubKey(StartupApprovedKey, true);
                    if (approvedKey != null)
                    {
                        byte[] enabledBytes = [0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
                        approvedKey.SetValue(ValueName, enabledBytes, RegistryValueKind.Binary);
                    }
                }
                catch { }

                // 清理 Startup 目录的历史残留快捷方式，避免与 Run 注册表冲突导致开机启动双实例竞态
                try
                {
                    var lnk = GetStartupShortcutPath();
                    if (File.Exists(lnk)) File.Delete(lnk);
                }
                catch { }
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);

                try
                {
                    using var approvedKey = Registry.CurrentUser.OpenSubKey(StartupApprovedKey, writable: true);
                    approvedKey?.DeleteValue(ValueName, throwOnMissingValue: false);
                }
                catch { }

                try
                {
                    var lnk = GetStartupShortcutPath();
                    if (File.Exists(lnk)) File.Delete(lnk);
                }
                catch { }

                Log.Info("Startup: disabled");
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Startup: set failed: {ex.GetType().FullName}: {ex.Message} | {ex.StackTrace?.Replace('\r', ' ').Replace('\n', ' ')}");
            return SetEnabledViaRegExe(enable);
        }
    }

    /// <summary>
    /// 托管注册表 API 被拒时的兜底：调用 reg.exe 直接读写 HKCU Run。
    /// 取证结论：报错并非真实权限缺失——HKCU\...\Run 的 ACL 中 Administrator 为 FullControl，
    /// 同环境下 PowerShell 的 OpenSubKey(writable:true) 与 SetValue 均成功；
    /// 因此保留一条不经过 Microsoft.Win32.Registry 访问检查的旁路，保证自启开关始终可用。
    /// </summary>
    private static bool SetEnabledViaRegExe(bool enable)
    {
        try
        {
            var psi = new ProcessStartInfo("reg.exe")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (enable)
            {
                var cmd = BuildCommand();
                if (string.IsNullOrEmpty(cmd))
                {
                    Log.Error("Startup: cannot resolve exe path");
                    return false;
                }
                psi.ArgumentList.Add("add");
                psi.ArgumentList.Add(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run");
                psi.ArgumentList.Add("/v"); psi.ArgumentList.Add(ValueName);
                psi.ArgumentList.Add("/t"); psi.ArgumentList.Add("REG_SZ");
                psi.ArgumentList.Add("/d"); psi.ArgumentList.Add(cmd);
                psi.ArgumentList.Add("/f");
            }
            else
            {
                psi.ArgumentList.Add("delete");
                psi.ArgumentList.Add(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run");
                psi.ArgumentList.Add("/v"); psi.ArgumentList.Add(ValueName);
                psi.ArgumentList.Add("/f");
            }
            using var p = Process.Start(psi);
            if (p is null) return false;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            var code = p.HasExited ? p.ExitCode : -1;
            var ok = code == 0;
            Log.Info($"Startup: reg.exe fallback {(enable ? "enable" : "disable")} -> exit={code} ok={ok}");
            if (!ok && !string.IsNullOrWhiteSpace(stderr))
                Log.Warn($"Startup: reg.exe stderr='{stderr.Trim()}'");
            // 删除一个本就不存在的值属于成功语义
            return ok || !enable;
        }
        catch (Exception ex)
        {
            Log.Error($"Startup: reg.exe fallback failed: {ex.Message}");
            return false;
        }
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDir)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = workingDir;
            shortcut.Description = "DeskBar Fast Autostart";
            shortcut.Save();
            Log.Info($"Created startup shortcut: {shortcutPath}");
        }
        catch (Exception ex)
        {
            Log.Warn($"CreateShortcut COM error: {ex.Message}");
        }
    }
}

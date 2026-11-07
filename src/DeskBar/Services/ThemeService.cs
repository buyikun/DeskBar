using Microsoft.Win32;

namespace DeskBar.Services;

/// <summary>
/// 跟随系统主题。
/// 关键：用 RegNotifyChangeKeyValue 监听 Themes\Personalize，而不是轮询。
/// 参考 AutoDarkMode 的 RegistryHandler 思路（GPL-3.0，仅借鉴机制，未复制代码）。
/// </summary>
internal sealed class ThemeService : IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsLight = "AppsUseLightTheme";
    private const string SystemLight = "SystemUsesLightTheme";

    private readonly object _gate = new();
    private Thread? _watchThread;
    private volatile bool _running;
    private CancellationTokenSource? _cts;
    private System.Threading.Timer? _scheduleTimer;
    private volatile bool _autoTimeEnabled = true;
    private TimeSpan _lightStart = new(7, 0, 0);
    private TimeSpan _darkStart = new(19, 0, 0);

    /// <summary>主题变化时触发（参数为 true = 浅色）。在后台线程上触发。</summary>
    public event Action<bool>? ThemeChanged;

    public bool AutoTimeEnabled
    {
        get => _autoTimeEnabled;
        set
        {
            _autoTimeEnabled = value;
            if (value) CheckTimeAndApply();
        }
    }

    public TimeSpan LightStart
    {
        get => _lightStart;
        set
        {
            _lightStart = value;
            if (_autoTimeEnabled) CheckTimeAndApply();
        }
    }

    public TimeSpan DarkStart
    {
        get => _darkStart;
        set
        {
            _darkStart = value;
            if (_autoTimeEnabled) CheckTimeAndApply();
        }
    }

    public void StartScheduleTimer(int initialDelayMs = 6000)
    {
        _scheduleTimer?.Dispose();
        // 开机或启动初期避开高峰，延迟数秒后再执行首次时间检查与切换
        _scheduleTimer = new System.Threading.Timer(_ => CheckTimeAndApply(), null, initialDelayMs, 30000);
    }

    public void CheckTimeAndApply()
    {
        if (!_autoTimeEnabled) return;
        try
        {
            var now = DateTime.Now.TimeOfDay;
            bool shouldBeLight;
            if (_lightStart < _darkStart)
            {
                shouldBeLight = now >= _lightStart && now < _darkStart;
            }
            else
            {
                shouldBeLight = !(now >= _darkStart || now < _lightStart);
            }

            var currentIsLight = GetSystemUsesLightTheme();
            if (shouldBeLight != currentIsLight)
            {
                Log.Info($"AutoTheme: time {now:hh\\:mm} reached, auto switching system to {(shouldBeLight ? "Light" : "Dark")}");
                SetTheme(shouldBeLight);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"CheckTimeAndApply failed: {ex.Message}");
        }
    }

    public bool IsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, false);
            if (key?.GetValue(SystemLight) is int v) return v != 0;
        }
        catch { }
        return true;
    }

    public bool GetSystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, false);
            if (key?.GetValue(SystemLight) is int v) return v != 0;
        }
        catch { }
        return true;
    }

    public bool GetAppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, false);
            if (key?.GetValue(AppsLight) is int v) return v != 0;
        }
        catch { }
        return true;
    }

    /// <summary>写入浅/深色并广播 ImmersiveColorSet 让全系统刷新。</summary>
    public void SetTheme(bool light)
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, writable: true))
            {
                if (key is not null)
                {
                    key.SetValue(SystemLight, light ? 1 : 0, RegistryValueKind.DWord);
                    key.SetValue(AppsLight, light ? 1 : 0, RegistryValueKind.DWord);
                }
            }
            BroadcastThemeChange();
            Log.Info($"SetTheme(light={light}) applied");
        }
        catch (Exception ex)
        {
            Log.Error($"SetTheme failed: {ex.Message}");
        }
    }

    /// <summary>广播 WM_SETTINGCHANGE + "ImmersiveColorSet"，这是让资源管理器等立即重绘的标准做法。</summary>
    public static void BroadcastThemeChange()
    {
        // 异步后台广播，绝不阻塞 UI/启动主线程（HWND_BROADCAST 在开机或系统高负载时可能耗时 1~3 秒）
        Task.Run(() =>
        {
            var str = System.Runtime.InteropServices.Marshal.StringToHGlobalUni("ImmersiveColorSet");
            try
            {
                Interop.NativeMethods.SendMessageTimeoutW(
                    Interop.NativeMethods.HWND_BROADCAST,
                    Interop.NativeMethods.WM_SETTINGCHANGE,
                    IntPtr.Zero, str,
                    Interop.NativeMethods.SMTO_ABORTIFHUNG, 1000, out _);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(str);
            }
        });
    }

    /// <summary>启动注册表变更监听（事件驱动，非轮询）。</summary>
    public void StartWatching()
    {
        if (_running) return;
        _running = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _watchThread = new Thread(() => WatchLoop(ct))
        {
            IsBackground = true,
            Name = "DeskBar.ThemeWatch"
        };
        _watchThread.Start();
        Log.Info("ThemeService: registry watch started");
    }

    private void WatchLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            IntPtr hKey = IntPtr.Zero;
            var evt = IntPtr.Zero;
            try
            {
                var rc = Interop.NativeMethods.RegOpenKeyExW(
                    new IntPtr(Interop.NativeMethods.HKEY_CURRENT_USER), PersonalizeKey, 0,
                    Interop.NativeMethods.KEY_NOTIFY | Interop.NativeMethods.KEY_QUERY_VALUE, out hKey);
                if (rc != 0)
                {
                    Log.Warn($"ThemeService: RegOpenKeyExW failed rc={rc}");
                    Thread.Sleep(2000);
                    continue;
                }

                evt = Interop.NativeMethods.CreateEventW(IntPtr.Zero, false, false, null);
                var rc2 = Interop.NativeMethods.RegNotifyChangeKeyValue(
                    hKey, false, Interop.NativeMethods.REG_NOTIFY_CHANGE_LAST_SET, evt, true);
                if (rc2 != 0)
                {
                    Log.Warn($"ThemeService: RegNotifyChangeKeyValue failed rc={rc2}");
                    Thread.Sleep(2000);
                    continue;
                }

                var signaled = Interop.NativeMethods.WaitForSingleObject(evt, 500);
                if (ct.IsCancellationRequested) break;

                if (signaled == 0)
                {
                    // 稍等一下让写入落盘
                    Thread.Sleep(120);
                    var light = GetSystemUsesLightTheme();
                    Log.Info($"ThemeService: change detected, light={light}");
                    ThemeChanged?.Invoke(light);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"ThemeService watch error: {ex.Message}");
                Thread.Sleep(1000);
            }
            finally
            {
                if (evt != IntPtr.Zero) Interop.NativeMethods.CloseHandle(evt);
                if (hKey != IntPtr.Zero) Interop.NativeMethods.RegCloseKey(hKey);
            }
        }
    }

    public void Dispose()
    {
        _running = false;
        _scheduleTimer?.Dispose();
        _scheduleTimer = null;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}

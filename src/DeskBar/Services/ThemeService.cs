using Microsoft.Win32;

namespace DeskBar.Services;

/// <summary>
/// 主题时间表 + 系统深浅色跟随。
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
    private volatile bool _applySystemTheme = true;
    private TimeSpan _dayStart = new(7, 0, 0);
    private TimeSpan _nightStart = new(19, 0, 0);
    private ThemePlan _dayPlan = new() { IsLight = true, Tone = "softmist" };
    private ThemePlan _nightPlan = new() { IsLight = false, Tone = "sheertights" };
    private ThemePlan? _appliedPlan;

    /// <summary>系统深浅色被外部改动时触发（参数为 true = 浅色）。在后台线程上触发。</summary>
    public event Action<bool>? ThemeChanged;

    /// <summary>时段方案生效时触发：自动切换或用户改了白天/夜晚配置都会走这里。在定时器/后台线程上触发。</summary>
    public event Action<ThemePlan>? PlanApplied;

    public bool AutoTimeEnabled
    {
        get => _autoTimeEnabled;
        set
        {
            _autoTimeEnabled = value;
            if (value) CheckTimeAndApply();
            else _appliedPlan = null;
        }
    }

    /// <summary>自动切换时是否连 Windows 全局深浅色一起改。</summary>
    public bool ApplySystemTheme
    {
        get => _applySystemTheme;
        set => _applySystemTheme = value;
    }

    public TimeSpan LightStart
    {
        get => _dayStart;
        set
        {
            _dayStart = value;
            if (_autoTimeEnabled) CheckTimeAndApply();
        }
    }

    public TimeSpan DarkStart
    {
        get => _nightStart;
        set
        {
            _nightStart = value;
            if (_autoTimeEnabled) CheckTimeAndApply();
        }
    }

    /// <summary>白天时段的方案（是否浅色 + 任务栏色调）。</summary>
    public ThemePlan DayPlan
    {
        get => _dayPlan;
        set
        {
            _dayPlan = value;
            if (_autoTimeEnabled) CheckTimeAndApply();
        }
    }

    /// <summary>夜晚时段的方案。</summary>
    public ThemePlan NightPlan
    {
        get => _nightPlan;
        set
        {
            _nightPlan = value;
            if (_autoTimeEnabled) CheckTimeAndApply();
        }
    }

    /// <summary>给定时刻应生效的方案（供设置页展示“当前时段”）。</summary>
    public ThemePlan PlanAt(DateTime when) => ThemeSchedule.Resolve(
        when.TimeOfDay, _dayStart, _nightStart, _dayPlan, _nightPlan);

    /// <summary>当前时刻是否处于白天段。</summary>
    public bool IsDayNow => ThemeSchedule.IsDay(DateTime.Now.TimeOfDay, _dayStart, _nightStart);

    public (DateTime At, ThemePlan Plan) NextSwitch() => ThemeSchedule.NextSwitch(
        DateTime.Now, _dayStart, _nightStart, _dayPlan, _nightPlan);

    /// <summary>下一次切换前需要等待的时长；关掉自动切换时返回 null。</summary>
    public TimeSpan? TimeUntilNextSwitch()
        => _autoTimeEnabled ? NextSwitch().At - DateTime.Now : null;

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
            var plan = PlanAt(DateTime.Now);

            // 短路条件除了「方案没变」，还要求「系统当前深浅色已经和方案一致」。
            // 只看方案会在写注册表失败（非提权、被组策略挡住）后永远不再重试。
            var systemMatches = !_applySystemTheme || GetSystemUsesLightTheme() == plan.IsLight;
            if (_appliedPlan is not null && _appliedPlan.Equals(plan) && systemMatches) return;

            // 只在真的需要时才写注册表并广播，避免每 30 秒无谓唤醒整个系统
            if (_applySystemTheme && !systemMatches)
            {
                Log.Info($"AutoTheme: switched to {(plan.IsLight ? "Light" : "Dark")} plan={plan}");
                SetTheme(plan.IsLight);
            }

            _appliedPlan = plan;
            PlanApplied?.Invoke(plan);
        }
        catch (Exception ex)
        {
            Log.Error($"CheckTimeAndApply failed: {ex.Message}");
        }
    }

    /// <summary>用户点「立即应用」时强制走一遍，忽略“方案未变化”的短路。</summary>
    public void ForceApplyNow()
    {
        _appliedPlan = null;
        CheckTimeAndApply();
        if (_appliedPlan is null)
        {
            // 自动切换是关的：仍然把当前时段方案交给界面
            var plan = PlanAt(DateTime.Now);
            _appliedPlan = plan;
            PlanApplied?.Invoke(plan);
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

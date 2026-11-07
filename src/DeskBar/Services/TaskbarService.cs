using DeskBar.Interop;

namespace DeskBar.Services;

/// <summary>任务栏控制：自动隐藏（SHAppBarMessage）与按钮居中（TaskbarAl 注册表）。</summary>
internal static class TaskbarService
{
    /// <summary>读取自动隐藏状态位掩码。</summary>
    public static int GetAutoHideState()
    {
        var abd = MakeAppBarData();
        var r = NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETSTATE, ref abd);
        return r.ToInt32();
    }

    public static bool IsAutoHideActive() =>
        (GetAutoHideState() & NativeMethods.ABS_AUTOHIDE) != 0;

    /// <summary>设置自动隐藏；返回原始状态用于还原。</summary>
    public static int? SetAutoHide(bool enable, int? originalState)
    {
        var abd = MakeAppBarData();
        if (enable)
        {
            var abd2 = MakeAppBarData();
            var original = NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETSTATE, ref abd2).ToInt32();
            abd.lParam = new IntPtr(NativeMethods.ABS_AUTOHIDE | NativeMethods.ABS_ALWAYSONTOP);
            NativeMethods.SHAppBarMessage(NativeMethods.ABM_SETSTATE, ref abd);
            Log.Info($"Taskbar autohide enabled (was {original})");
            return original;
        }
        else
        {
            var flags = originalState ?? NativeMethods.ABS_ALWAYSONTOP;
            if (flags == 0) flags = NativeMethods.ABS_ALWAYSONTOP; // 不写裸 0
            abd.lParam = new IntPtr(flags | NativeMethods.ABS_ALWAYSONTOP);
            NativeMethods.SHAppBarMessage(NativeMethods.ABM_SETSTATE, ref abd);
            Log.Info($"Taskbar autohide restored to {flags}");
            return null;
        }
    }

    private static NativeMethods.APPBARDATA MakeAppBarData()
    {
        var tray = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        return new NativeMethods.APPBARDATA
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.APPBARDATA>(),
            hWnd = tray,
            uCallbackMessage = 0,
            uEdge = 0,
            rc = default,
            lParam = IntPtr.Zero
        };
    }

    /// <summary>Win11 任务栏按钮居中：HKCU\...\Explorer\Advanced\TaskbarAl（0=左，1=居中）。</summary>
    public static bool? GetCentered()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", false);
            if (key?.GetValue("TaskbarAl") is int v) return v == 1;
        }
        catch { }
        return null;
    }

    public static bool SetCentered(bool centered)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", true);
            if (key is null)
            {
                Log.Error("TaskbarAl: cannot open Advanced key for write");
                return false;
            }
            key.SetValue("TaskbarAl", centered ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
            Log.Info($"TaskbarAl set to {(centered ? 1 : 0)}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"TaskbarAl set failed: {ex.Message}");
            return false;
        }
    }

    public static void ApplyStyle(string style, string? tone = null)
    {
        tone ??= App.Settings?.AcrylicTone ?? "natural";
        Log.Info($"TaskbarService.ApplyStyle: style={style}, tone={tone}");

        switch (style)
        {
            case "acrylic":
                StartWatchOnly();
                RestoreTaskbarRegion();
                NativeTaskbarAppearanceService.Apply("acrylic", tone);
                break;
            case "clear":
                StartWatchOnly();
                RestoreTaskbarRegion();
                NativeTaskbarAppearanceService.Apply("transparent", tone);
                break;
            case "island_acrylic":
                NativeTaskbarAppearanceService.Apply("acrylic", tone);
                StartIslandWatch();
                break;
            case "island_clear":
                NativeTaskbarAppearanceService.Apply("transparent", tone);
                StartIslandWatch();
                break;
            default:
                StartWatchOnly();
                RestoreTaskbarRegion();
                NativeTaskbarAppearanceService.Apply("default", tone);
                break;
        }
    }

    private static System.Windows.Automation.AutomationElement? _cachedFrame;
    private static System.Windows.Automation.AutomationElement? _cachedNotify;

    // 缓动动画与防裁切缓冲引擎
    private static System.Threading.Timer? _islandWatchTimer;
    private static System.Threading.Timer? _animTimer;
    private static volatile bool _islandActive = false;
    // 扫描/缓动循环是否在跑。整条模式（全透明 / 全亚克力）下不裁剪任务栏，
    // 但中间应用区与托盘区的边界仍要持续维护，供描边窗口贴边绘制。
    private static volatile bool _watchActive = false;
    private static readonly object _animLock = new();

    private static double _curC1 = -1, _curC2 = -1, _curT1 = -1, _curT2 = -1;
    private static int _targetC1 = -1, _targetC2 = -1, _targetT1 = -1, _targetT2 = -1;
    private static int _pendingShrinkC1 = -1, _pendingShrinkC2 = -1;
    private static DateTime _shrinkDelayUntil = DateTime.MinValue;
    private static int _renderedC1 = -1, _renderedC2 = -1, _renderedT1 = -1, _renderedT2 = -1;

    // UIA 扫描回来的边界天然带 ±10~20px 抖动（鼠标悬停高亮、通知闪烁、截图工具的全屏遮罩
    // 都会让结果漂移），直接采纳会让浮岛边界持续抽动。故做两层过滤：
    //   1) 变化幅度小于 DeadZonePx 的一律忽略；
    //   2) 更大的变化需要连续 StableTicksNeeded 次扫描结果一致才采纳。
    private const int DeadZonePx = 8;
    private const int StableTicksNeeded = 2;
    // 扩张（新开应用）必须跟手，用户眼睛盯着图标出现，任何延迟都会被读成"卡"。
    // 收缩（关应用）才是需要防抖的方向：图标收拢时 Windows 自己还要平移居中。
    private const int ExpandDeadZonePx = 2;
    private const int ExpandStableTicksNeeded = 1;
    private static int _stableC1 = -1, _stableC2 = -1;
    private static int _noCenterRounds;
    private static int _stableCount = 0;

    public static void StartIslandWatch()
    {
        _islandActive = true;
        _watchActive = true;
        Log.Info("StartIslandWatch called, starting timers");
        StartTimers();
    }

    /// <summary>
    /// 整条模式（全透明 / 全亚克力）下启动同一套扫描与缓动循环，只维护几何信息、不裁剪任务栏。
    /// 这样两组功能区边界始终是最新的，描边窗口才能贴着它们画 1px 细边框。
    /// </summary>
    public static void StartWatchOnly()
    {
        _islandActive = false;
        _watchActive = true;
        Log.Info("StartWatchOnly called, geometry-only watch");
        StartTimers();
    }

    private static void StartTimers()
    {
        _islandWatchTimer?.Dispose();
        _animTimer?.Dispose();

        _curC1 = _curC2 = _curT1 = _curT2 = -1;
        _targetC1 = _targetC2 = _targetT1 = _targetT2 = -1;
        _renderedC1 = _renderedC2 = _renderedT1 = _renderedT2 = -1;
        _stableC1 = _stableC2 = -1;
        _stableCount = 0;
        _shrinkDelayUntil = DateTime.MinValue;

        // 100ms 一轮：UIA 的噪声由死区 + 连续确认挡住，间隔本身不必再拖长
        _islandWatchTimer = new System.Threading.Timer(_ => ScanTaskbarElements(), null, 0, 100);
        // 16ms (~60fps) 丝滑平滑插值缓动循环
        _animTimer = new System.Threading.Timer(_ => OnAnimTick(), null, 0, 16);
    }

    public static void StopTranslucentTB()
    {
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("TranslucentTB");
            foreach (var p in procs)
            {
                try { p.Kill(); } catch { }
            }
            Log.Info("TranslucentTB stopped");
        }
        catch (Exception ex)
        {
            Log.Error($"StopTranslucentTB error: {ex.Message}");
        }
    }

    public static void StopIslandWatch()
    {
        _islandActive = false;
        _watchActive = false;
        _islandWatchTimer?.Dispose();
        _islandWatchTimer = null;
        _animTimer?.Dispose();
        _animTimer = null;
        _cachedFrame = null;
        _cachedNotify = null;
        RestoreTaskbarRegion();
    }

    public static void CleanUp()
    {
        StopIslandWatch();
        RestoreTaskbarRegion();
        StopTranslucentTB();
    }

    public static void RestoreTaskbarRegion()
    {
        var tray = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero)
        {
            NativeMethods.SetWindowRgn(tray, IntPtr.Zero, true);
        }
        _renderedC1 = _renderedC2 = _renderedT1 = _renderedT2 = -1;
        _lastRegionSig = "";
    }

    private static bool IsFullscreenForeground()
    {
        try
        {
            var fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;

            var tray = NativeMethods.FindWindowW("Shell_TrayWnd", null);
            if (fg == tray) return false;

            var sb = new System.Text.StringBuilder(256);
            NativeMethods.GetClassNameW(fg, sb, 256);
            var cls = sb.ToString();
            if (cls is "Progman" or "WorkerW" or "XamlWindow" or "Windows.UI.Core.CoreWindow" or "Shell_SecondaryTrayWnd" or "Shell_TrayWnd")
                return false;

            NativeMethods.GetWindowThreadProcessId(fg, out var pid);
            if (pid != 0)
            {
                try
                {
                    using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                    if (proc.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
                catch { }
            }

            if (NativeMethods.GetWindowRect(fg, out var rc))
            {
                int screenW = NativeMethods.GetSystemMetrics(0); // SM_CXSCREEN
                int screenH = NativeMethods.GetSystemMetrics(1); // SM_CYSCREEN
                if (rc.left <= 0 && rc.top <= 0 && rc.right >= screenW && rc.bottom >= screenH)
                {
                    Log.Info($"Fullscreen detected: [{cls}] '{sb}', rc=[{rc.left},{rc.top},{rc.right},{rc.bottom}] vs screen=[{screenW},{screenH}]");
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 不依赖 UIA 的兜底几何：直接读任务栏子窗口矩形。
    /// 中间图标组 = Start / ReBarWindow32 / MSTaskSwWClass 三者并集；托盘区 = TrayNotifyWnd。
    /// 返回值换算成相对任务栏左边缘的坐标，与 UIA 分支保持同一约定。
    /// </summary>
    private static bool TryGetTaskbarChildRects(out int cLeft, out int cRight, out int tLeft, out int tRight)
    {
        cLeft = cRight = tLeft = tRight = 0;
        var tray = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return false;
        if (!NativeMethods.GetWindowRect(tray, out var trayRc)) return false;

        int minL = int.MaxValue, maxR = int.MinValue;
        foreach (var cls in new[] { "Start", "ReBarWindow32", "MSTaskSwWClass" })
        {
            var h = NativeMethods.FindWindowExW(tray, IntPtr.Zero, cls, null);
            if (h == IntPtr.Zero) continue;
            if (!NativeMethods.GetWindowRect(h, out var rc)) continue;
            if (rc.right <= rc.left) continue;
            if (rc.left < minL) minL = rc.left;
            if (rc.right > maxR) maxR = rc.right;
        }
        if (minL == int.MaxValue || maxR <= minL) return false;

        var notify = NativeMethods.FindWindowExW(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        int nL = 0, nR = 0;
        if (notify != IntPtr.Zero && NativeMethods.GetWindowRect(notify, out var nr) && nr.right > nr.left)
        {
            nL = nr.left;
            nR = nr.right;
        }

        cLeft = minL - trayRc.left;
        cRight = maxR - trayRc.left;
        tLeft = nL - trayRc.left;
        tRight = nR - trayRc.left;
        return tRight > tLeft;
    }

    public static void ScanTaskbarElements()
    {
        if (!_watchActive) return;
        // 若当前处于全屏独占游戏或全屏播放中，任务栏完全不可见，立即挂起后台 UIA 检测，彻底 0 开销
        if (IsFullscreenForeground()) return;
        try
        {
            var tray = NativeMethods.FindWindowW("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return;

            NativeMethods.RECT trayRc;
            NativeMethods.GetWindowRect(tray, out trayRc);
            int trayH = trayRc.bottom - trayRc.top;
            int trayW = trayRc.right - trayRc.left;
            if (trayH <= 0 || trayW <= 0) return;

            int cLeft = 0, cRight = 0, tLeft = 0, tRight = 0;
            bool hasValidCenter = false;
            try
            {
                if (_cachedFrame == null || _cachedNotify == null)
                {
                    var desktop = System.Windows.Automation.AutomationElement.RootElement;
                    var condTray = new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ClassNameProperty, "Shell_TrayWnd");
                    var trayEl = desktop.FindFirst(System.Windows.Automation.TreeScope.Children, condTray);
                    if (trayEl != null)
                    {
                        var condFrame = new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ClassNameProperty, "Taskbar.TaskbarFrameAutomationPeer");
                        _cachedFrame = trayEl.FindFirst(System.Windows.Automation.TreeScope.Descendants, condFrame);

                        var condNotify = new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ClassNameProperty, "TrayNotifyWnd");
                        _cachedNotify = trayEl.FindFirst(System.Windows.Automation.TreeScope.Children, condNotify);
                    }
                }

                if (_cachedFrame != null)
                {
                    var items = _cachedFrame.FindAll(System.Windows.Automation.TreeScope.Children, System.Windows.Automation.Condition.TrueCondition);
                    double minL = double.MaxValue, maxR = double.MinValue;
                    int validCount = 0;
                    foreach (System.Windows.Automation.AutomationElement it in items)
                    {
                        try
                        {
                            var r = it.Current.BoundingRectangle;
                            if (r.Width > 0 && r.Height > 0)
                            {
                                if (r.Left < minL) minL = r.Left;
                                if (r.Right > maxR) maxR = r.Right;
                                validCount++;
                            }
                        }
                        catch { }
                    }
                    if (validCount > 0 && minL < maxR)
                    {
                        cLeft = (int)minL - trayRc.left;
                        cRight = (int)maxR - trayRc.left;
                        if (cRight > cLeft)
                        {
                            hasValidCenter = true;
                        }
                    }
                }

                if (_cachedNotify != null)
                {
                    var r = _cachedNotify.Current.BoundingRectangle;
                    tLeft = (int)r.Left - trayRc.left;
                    tRight = (int)r.Right - trayRc.left;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"UIA scan failed: {ex.Message}");
                _cachedFrame = null;
                _cachedNotify = null;
            }

            // 休眠唤醒或锁屏期间若未检测到有效按钮，直接跳过本轮更新，保持原有安全显示状态，严防切出空白任务栏
            if (!hasValidCenter)
            {
                // UIA 在开机初期或任务栏重建后可能长时间给不出按钮矩形，而 _cachedFrame/_cachedNotify
                // 只在异常分支清空：一旦拿到「合法但陈旧」的元素，validCount 恒为 0 就会静默永久失效
                //（_rendered* 永远 -1 → 浮岛与细边框全部不出现）。这里做两级自愈。
                _noCenterRounds++;
                if (_noCenterRounds >= 10)
                {
                    _cachedFrame = null;
                    _cachedNotify = null;
                }
                if (_noCenterRounds >= 20 && TryGetTaskbarChildRects(out int fc1, out int fc2, out int ft1, out int ft2))
                {
                    cLeft = fc1;
                    cRight = fc2;
                    tLeft = ft1;
                    tRight = ft2;
                    hasValidCenter = true;
                    if (_noCenterRounds == 20)
                    {
                        Log.Warn("UIA taskbar scan produced no center for 20 rounds, falling back to Win32 child rects");
                    }
                }
                if (!hasValidCenter)
                {
                    return;
                }
            }
            else
            {
                _noCenterRounds = 0;
            }

            if (tLeft <= 0 || tRight <= tLeft)
            {
                tLeft = trayW - 250;
                tRight = trayW;
            }

            int pad = 5;
            int desiredC1 = Math.Max(0, cLeft - pad);
            int desiredC2 = Math.Min(trayW, cRight + pad);
            int desiredT1 = Math.Max(0, tLeft - 6);
            int desiredT2 = Math.Min(trayW, tRight - 4);

            lock (_animLock)
            {
                // 拖拽托盘图标 / 在任务栏上拖动应用时鼠标左键一直按着，这期间 UIA 给出的
                // 边界会在 1~21px 之间反复跳。只要这期间采纳任何一个值，边框就会跟着抽。
                // 所以按住左键期间彻底冻结，松手后立刻用最新扫描值平滑到位。
                bool leftDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
                if (leftDown && _curC1 >= 0)
                {
                    _stableC1 = _stableC2 = -1;
                    _stableCount = 0;
                    return;
                }

                // 初次启动：直接赋值到位，避免突兀初动
                if (_curC1 < 0)
                {
                    _curC1 = _targetC1 = desiredC1;
                    _curC2 = _targetC2 = desiredC2;
                    _curT1 = _targetT1 = desiredT1;
                    _curT2 = _targetT2 = desiredT2;
                    ApplyRegionImmediate((int)_curC1, (int)_curC2, (int)_curT1, (int)_curT2, tray, trayW, trayH);
                    return;
                }

                // 扩张方向（新应用出现）走轻量过滤，做到眼睛看到图标的同时边框已经张开。
                // 注意：必须「两侧同时向外」才算扩张。旧写法是「任一侧向外即是扩张」，
                // UIA 抖动时光是 c1 抖小 1px 就走了快通道，死区与连续确认全部失效，
                // 于是拖拽托盘图标期间边框每 100ms 抽一次（用户看到的剧烈抖动）。
                bool isExpandCandidate = desiredC1 < _targetC1 && desiredC2 > _targetC2;
                int deadZone = isExpandCandidate ? ExpandDeadZonePx : DeadZonePx;
                int stableNeeded = isExpandCandidate ? ExpandStableTicksNeeded : StableTicksNeeded;

                // 抖动过滤 1：变化落在死区内就当成没变，托盘段照旧跟随
                if (Math.Abs(desiredC1 - _targetC1) <= deadZone &&
                    Math.Abs(desiredC2 - _targetC2) <= deadZone)
                {
                    _stableC1 = _stableC2 = -1;
                    _stableCount = 0;
                    ApplyTrayTargets(desiredT1, desiredT2);
                    return;
                }

                // 抖动过滤 2：连续 stableNeeded 次扫描得到同一组边界才算真实变化
                if (desiredC1 == _stableC1 && desiredC2 == _stableC2)
                {
                    _stableCount++;
                }
                else
                {
                    _stableC1 = desiredC1;
                    _stableC2 = desiredC2;
                    _stableCount = 1;
                }

                if (_stableCount < stableNeeded)
                {
                    return;
                }

                _stableC1 = _stableC2 = -1;
                _stableCount = 0;

                Log.Info($"Target bounds update: c1={desiredC1}, c2={desiredC2} (was target c1={_targetC1}, c2={_targetC2})");

                ApplyTrayTargets(desiredT1, desiredT2);

                // 判断是否是宽度扩展（打开新应用）
                bool isExpanding = desiredC1 < _targetC1 || desiredC2 > _targetC2;
                // 判断是否是宽度收缩（关闭应用）
                bool isShrinking = desiredC1 > _targetC1 && desiredC2 < _targetC2;

                if (isExpanding)
                {
                    // 立即向外扩展，取消任何待收缩延迟
                    _targetC1 = desiredC1;
                    _targetC2 = desiredC2;
                    _shrinkDelayUntil = DateTime.MinValue;
                }
                else if (isShrinking)
                {
                    // 应用退出：给 Windows 11 图标预留 160ms 平移居中时间，暂不缩小外框
                    _pendingShrinkC1 = desiredC1;
                    _pendingShrinkC2 = desiredC2;
                    if (_shrinkDelayUntil == DateTime.MinValue)
                    {
                        _shrinkDelayUntil = DateTime.UtcNow.AddMilliseconds(160);
                    }
                }
                else
                {
                    // 仅有细微居中位置微调
                    _targetC1 = desiredC1;
                    _targetC2 = desiredC2;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"ScanTaskbarElements error: {ex.Message}");
        }
    }

    private static void OnAnimTick()
    {
        if (!_watchActive) return;
        try
        {
            var tray = NativeMethods.FindWindowW("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return;

            NativeMethods.RECT trayRc;
            NativeMethods.GetWindowRect(tray, out trayRc);
            int trayH = trayRc.bottom - trayRc.top;
            int trayW = trayRc.right - trayRc.left;
            if (trayH <= 0 || trayW <= 0) return;

            int rC1, rC2, rT1, rT2;
            lock (_animLock)
            {
                if (_curC1 < 0) return;

                // 检查退出应用时的缩回延迟是否已到期
                if (_shrinkDelayUntil != DateTime.MinValue && DateTime.UtcNow >= _shrinkDelayUntil)
                {
                    _targetC1 = _pendingShrinkC1;
                    _targetC2 = _pendingShrinkC2;
                    _shrinkDelayUntil = DateTime.MinValue;
                }

                // 缓动插值：系数 0.26，兼顾毫秒级响应与弹性质感
                const double alpha = 0.26;
                _curC1 += (_targetC1 - _curC1) * alpha;
                _curC2 += (_targetC2 - _curC2) * alpha;
                _curT1 += (_targetT1 - _curT1) * alpha;
                _curT2 += (_targetT2 - _curT2) * alpha;

                // 吸附阈值（差异小于 0.4px 时精准到位）
                if (Math.Abs(_targetC1 - _curC1) < 0.4) _curC1 = _targetC1;
                if (Math.Abs(_targetC2 - _curC2) < 0.4) _curC2 = _targetC2;
                if (Math.Abs(_targetT1 - _curT1) < 0.4) _curT1 = _targetT1;
                if (Math.Abs(_targetT2 - _curT2) < 0.4) _curT2 = _targetT2;

                rC1 = (int)Math.Round(_curC1);
                rC2 = (int)Math.Round(_curC2);
                rT1 = (int)Math.Round(_curT1);
                rT2 = (int)Math.Round(_curT2);

                if (rC1 == _renderedC1 && rC2 == _renderedC2 && rT1 == _renderedT1 && rT2 == _renderedT2)
                {
                    return;
                }

                _renderedC1 = rC1;
                _renderedC2 = rC2;
                _renderedT1 = rT1;
                _renderedT2 = rT2;
            }

            ApplyRegionImmediate(rC1, rC2, rT1, rT2, tray, trayW, trayH);
        }
        catch (Exception ex)
        {
            Log.Error($"OnAnimTick error: {ex.Message}");
        }
    }

    // 预计算的超平滑顶角内缩曲线（Fluent 风格 Squircle 平滑过渡，彻底消除原生 GDI 粗糙 3px 折角与阶梯锯齿）
    // 黄金自共轭对偶连续平滑圆角（消除 3px 悬崖跳阶与 45° 倒角直棱线，步进严格对偶镜面）
    private static readonly int[] SmoothCornerOffsets = new int[]
    {
        9, 7, 5, 4, 3, 2, 2, 1, 1, 0
    };

    private static IntPtr CreateSmoothTopRoundedRgn(int x1, int y1, int x2, int y2)
    {
        var offsets = SmoothCornerOffsets;
        int r = offsets.Length;
        int rectCount = r + (y2 > y1 + r ? 1 : 0);

        // RGNDATA header 32 字节 + 每个 RECT 16 字节
        byte[] data = new byte[32 + rectCount * 16];

        // Header: dwSize=32, iType=1(RDH_RECTANGLES), nCount=rectCount, nRgnSize=rectCount*16, rcBound=(x1, y1, x2, y2)
        Buffer.BlockCopy(BitConverter.GetBytes((uint)32), 0, data, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes((uint)1), 0, data, 4, 4);
        Buffer.BlockCopy(BitConverter.GetBytes((uint)rectCount), 0, data, 8, 4);
        Buffer.BlockCopy(BitConverter.GetBytes((uint)(rectCount * 16)), 0, data, 12, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(x1), 0, data, 16, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(y1), 0, data, 20, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(x2), 0, data, 24, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(y2), 0, data, 28, 4);

        int pos = 32;
        for (int i = 0; i < r; i++)
        {
            int dx = offsets[i];
            int rx1 = x1 + dx;
            int rx2 = x2 - dx;
            if (rx2 < rx1) rx2 = rx1;
            int ry1 = y1 + i;
            int ry2 = ry1 + 1;

            Buffer.BlockCopy(BitConverter.GetBytes(rx1), 0, data, pos, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(ry1), 0, data, pos + 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(rx2), 0, data, pos + 8, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(ry2), 0, data, pos + 12, 4);
            pos += 16;
        }

        if (y2 > y1 + r)
        {
            Buffer.BlockCopy(BitConverter.GetBytes(x1), 0, data, pos, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(y1 + r), 0, data, pos + 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(x2), 0, data, pos + 8, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(y2), 0, data, pos + 12, 4);
        }

        return NativeMethods.ExtCreateRegion(IntPtr.Zero, (uint)data.Length, data);
    }

    // 托盘浮岛左边缘的屏幕绝对坐标锚点（供音乐胶囊等外挂浮岛窗口精准停靠）
    // 实时托盘区左缘的 2px 死区缓存：TrayNotifyWnd 的窗口矩形由 explorer 直接维护，
    // 托盘图标增减/拖出时立刻变化，不经过 UIA 的跨进程 RPC、也不受扫描节流影响。
    // 但它的矩形偶尔会在两个相邻像素之间跳，用死区挡住 1~2px 的抖动。
    private static int _trayLeftRealtime = int.MinValue;

    private static int _anchorTrayLeft = int.MinValue;
    private static int _anchorTrayTop;
    private static int _anchorTrayHeight;

    // 任务栏自身窗口的屏幕矩形（供左侧硬件监控浮岛贴靠任务栏两端）
    private static int _taskbarLeft;
    private static int _taskbarTop;
    private static int _taskbarWidth;
    private static int _taskbarHeight;

    // 已下发的任务栏区域签名：相同就不再调用 SetWindowRgn。
    // 拖拽托盘图标时几何每 100ms 变一次，反复重设区域会让整条任务栏不停重绘
    //（表现就是浮岛「剧烈抖动」）。
    private static string _lastRegionSig = "";

    // 拖拽态判定：拖拽托盘图标期间鼠标左键一直按着，用它能可靠识别「正在拖」。
    private const int VK_LBUTTON = 0x01;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    /// <summary>获取任务栏自身窗口的屏幕矩形；返回 false 表示任务栏浮岛尚未生效。</summary>
    public static bool TryGetTaskbarBounds(out int left, out int top, out int width, out int height)
    {
        left = _taskbarLeft;
        top = _taskbarTop;
        width = _taskbarWidth;
        height = _taskbarHeight;
        if (_islandActive && _taskbarWidth > 0 && _taskbarHeight > 0) return true;

        // 整条模式（全透明 / 全亚克力）下没有浮岛扫描，直接查任务栏窗口，
        // 否则左侧监控浮岛在整条模式下会定位不到、停在旧位置。
        if (TryGetShellTaskbarRect(out int l, out int t, out int r, out int b))
        {
            left = l; top = t; width = r - l; height = b - t;
            return width > 0 && height > 0;
        }
        return false;
    }

    /// <summary>
    /// 获取托盘浮岛左边缘的屏幕坐标锚点，用于音乐胶囊等外挂浮岛定位。
    /// 返回 false 表示当前任务栏浮岛尚未生效（不可停靠）。
    /// </summary>
    public static bool TryGetTrayIslandAnchor(out int screenLeft, out int screenTop, out int height)
    {
        screenLeft = _anchorTrayLeft;
        screenTop = _anchorTrayTop;
        height = _anchorTrayHeight;

        if (_anchorTrayLeft == int.MinValue || _anchorTrayHeight <= 0) return false;
        if (_islandActive) return true;

        // 整条模式下扫描已停，但托盘区几何没变，缓存锚点仍然可用——
        // 只要它还落在当前任务栏矩形内就继续沿用，两种风格的停靠位置才完全一致。
        if (TryGetShellTaskbarRect(out int tbL, out int tbT, out int tbR, out int tbB))
        {
            screenTop = tbT;
            height = tbB - tbT;
            return screenLeft > tbL && screenLeft < tbR && height > 0;
        }
        return false;
    }

    /// <summary>
    /// 实时读取托盘区左缘（屏幕坐标）。这是唯一「托盘图标一变就立刻跟着变」的数据源：
    /// TrayNotifyWnd 由 explorer 自己维护窗口矩形，而 UIA 扫描每 100ms 一轮、还要过
    /// 死区与插值，托盘图标被拖出后胶囊要等近 1 秒才动（用户说的「不跟随」）。
    /// 返回的 screenLeft 带 2px 死区，避免矩形在相邻像素间跳动导致浮岛左右颤。
    /// </summary>
    public static bool TryGetTrayLeftRealtime(out int screenLeft, out int screenTop, out int screenHeight)
    {
        screenLeft = 0;
        screenTop = 0;
        screenHeight = 0;
        try
        {
            var tray = NativeMethods.FindWindowW("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return false;
            if (!NativeMethods.GetWindowRect(tray, out var tr)) return false;
            if (tr.bottom <= tr.top) return false;

            var notify = NativeMethods.FindWindowExW(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            if (notify == IntPtr.Zero) return false;
            if (!NativeMethods.IsWindowVisible(notify)) return false;
            if (!NativeMethods.GetWindowRect(notify, out var nr)) return false;
            if (nr.right <= nr.left) return false;

            int left = nr.left;
            if (_trayLeftRealtime != int.MinValue && Math.Abs(left - _trayLeftRealtime) <= 2)
            {
                left = _trayLeftRealtime;
            }
            else
            {
                _trayLeftRealtime = left;
            }

            screenLeft = left;
            screenTop = tr.top;
            screenHeight = tr.bottom - tr.top;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 直接向系统查询任务栏窗口矩形（不依赖浮岛是否激活）。
    /// 整条模式（全透明 / 全亚克力）下浮岛扫描已停，外挂浮岛靠它贴靠任务栏。
    /// </summary>
    public static bool TryGetShellTaskbarRect(out int left, out int top, out int right, out int bottom)
    {
        left = top = right = bottom = 0;
        try
        {
            var tray = NativeMethods.FindWindowW("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return false;
            if (!NativeMethods.GetWindowRect(tray, out var rc)) return false;
            left = rc.left; top = rc.top; right = rc.right; bottom = rc.bottom;
            return right > left && bottom > top;
        }
        catch { return false; }
    }

    /// <summary>当前是否处于浮岛（裁剪任务栏）模式。</summary>
    public static bool IsIslandActive => _islandActive;

    /// <summary>任务栏此刻是否正常显示在屏幕上（被自动隐藏推出屏幕时为 false）。</summary>
    public static bool IsTaskbarOnScreen()
    {
        try
        {
            var tray = NativeMethods.FindWindowW("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return false;
            if (!NativeMethods.IsWindowVisible(tray)) return false;
            if (!NativeMethods.GetWindowRect(tray, out var rc)) return false;
            int screenH = NativeMethods.GetSystemMetrics(1);   // SM_CYSCREEN
            if (rc.top >= screenH - 1 || rc.bottom <= 1) return false;
            return rc.right > rc.left && rc.bottom > rc.top;
        }
        catch { return false; }
    }

    /// <summary>
    /// 取「中间应用区」与「托盘区」在屏幕上的矩形（物理像素，已含与浮岛一致的内缩边距）。
    /// 整条模式下同样有效：那时虽然不裁剪任务栏，两组区域的几何仍由扫描循环持续维护。
    /// </summary>
    public static bool TryGetIslandRectsOnScreen(out int midLeft, out int midRight, out int trayLeft,
        out int trayRight, out int top, out int height)
    {
        midLeft = midRight = trayLeft = trayRight = top = height = 0;
        int c1 = _renderedC1, c2 = _renderedC2, t1 = _renderedT1, t2 = _renderedT2;
        if (c1 < 0 || c2 <= c1 || t1 < 0 || t2 <= t1) return false;
        if (!TryGetTaskbarBounds(out int left, out int tbTop, out _, out int tbHeight)) return false;
        if (tbHeight <= 0) return false;

        top = tbTop;
        height = tbHeight;
        midLeft = left + c1;
        midRight = left + c2;
        trayLeft = left + t1;
        trayRight = left + t2;
        return true;
    }

    private struct Exclusion
    {
        public string Key;
        public int L, T, R, B;
    }

    private static readonly object _exclLock = new object();
    private static readonly System.Collections.Generic.Dictionary<string, Exclusion> _exclusions =
        new System.Collections.Generic.Dictionary<string, Exclusion>();

    /// <summary>
    /// 登记一个「任务栏必须留空」的矩形（屏幕坐标）：整条模式下浮岛就落在这里。
    /// 只有矩形真的变了才重算任务栏区域，避免每秒重设区域把任务栏刷闪。
    /// </summary>
    public static void SetWidgetExclusion(string key, int l, int t, int r, int b)
    {
        bool changed = false;
        lock (_exclLock)
        {
            Exclusion cur;
            if (!_exclusions.TryGetValue(key, out cur) || cur.L != l || cur.T != t || cur.R != r || cur.B != b)
            {
                _exclusions[key] = new Exclusion { Key = key, L = l, T = t, R = r, B = b };
                changed = true;
            }
        }
        if (changed) ReapplyRegion();
    }

    /// <summary>读取某个浮窗当前登记的矩形（屏幕物理像素）。返回 false 表示它此刻不存在/已隐藏。</summary>
    public static bool TryGetWidgetRect(string key, out int l, out int t, out int r, out int b)
    {
        l = t = r = b = 0;
        lock (_exclLock)
        {
            if (!_exclusions.TryGetValue(key, out var e)) return false;
            l = e.L; t = e.T; r = e.R; b = e.B;
            return r > l;
        }
    }

    public static void ClearWidgetExclusion(string key)
    {
        bool changed = false;
        lock (_exclLock)
        {
            changed = _exclusions.Remove(key);
        }
        if (changed) ReapplyRegion();
    }

    private static Exclusion[] SnapshotExclusions()
    {
        lock (_exclLock)
        {
            var arr = new Exclusion[_exclusions.Count];
            _exclusions.Values.CopyTo(arr, 0);
            return arr;
        }
    }

    private static void ReapplyRegion()
    {
        try
        {
            if (_islandActive) return;
            var tray = NativeMethods.FindWindowW("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return;
            NativeMethods.RECT rc;
            if (!NativeMethods.GetWindowRect(tray, out rc)) return;
            ApplyRegionImmediate(_renderedC1, _renderedC2, _renderedT1, _renderedT2, tray,
                rc.right - rc.left, rc.bottom - rc.top);
        }
        catch { }
    }

    /// <summary>
    /// 托盘段边界的抖动过滤：拖拽托盘图标时 UIA 会在两个值之间来回跳，
    /// 旧写法无条件跟随，于是音乐胶囊与两条细边框被带着来回弹
    ///（用户看到的「系统信息组件和音乐组件剧烈抖动」）。
    /// </summary>
    private static void ApplyTrayTargets(int desiredT1, int desiredT2)
    {
        // 拖拽托盘图标时鼠标左键一直按着，而这段时间 UIA 给出的托盘区边界会在两个值之间
        // 来回跳；无条件跟随就会把音乐胶囊与两条细边框带着来回弹（用户看到的「剧烈抖动」）。
        // 这里只在「按住左键」期间冻结，松手立刻恢复正常跟随 —— 既不抖，也不会像
        // 「连续两轮一致」那样在持续抖动时永久冻住（那会变成「不跟随」）。
        bool dragging = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        if (dragging && _targetT1 >= 0) return;

        _targetT1 = desiredT1;
        _targetT2 = desiredT2;
    }

    private static void ApplyRegionImmediate(int c1, int c2, int t1, int t2, IntPtr tray, int trayW, int trayH)
    {
        // 记录托盘浮岛左边缘的屏幕锚点（t1 为相对任务栏左边缘的偏移）
        try
        {
            NativeMethods.RECT trc;
            if (NativeMethods.GetWindowRect(tray, out trc))
            {
                _anchorTrayLeft = trc.left + t1;
                _anchorTrayTop = trc.top;
                _anchorTrayHeight = trayH;

                _taskbarLeft = trc.left;
                _taskbarTop = trc.top;
                _taskbarWidth = trc.right - trc.left;
                _taskbarHeight = trayH;
            }
        }
        catch { }

        // 整条模式：任务栏铺满整条，但要把两个浮岛的位置挖掉。
        // 顶层窗口之间没有稳定的 z 序，点一下任务栏 explorer 就会把它抬到浮岛之上；
        // 浮岛模式本来就为浮岛挖了洞，所以不受影响，而整条模式如果不挖洞，
        // 浮岛就会被磨砂层整块盖住（表现就是「组件直接不见了」）。
        if (!_islandActive)
        {
            var excl = SnapshotExclusions();

            // 区域签名：整条模式下任务栏区域只由「挖洞矩形」决定。
            // 相同就直接返回 —— 拖拽托盘图标时几何每 100ms 变一次，
            // 反复 SetWindowRgn 会让整条任务栏不停重绘（浮岛看起来在剧烈抖动）。
            var sig = new System.Text.StringBuilder("F:");
            sig.Append(trayW).Append('x').Append(trayH).Append(':').Append(excl.Length);
            foreach (var e0 in excl)
            {
                sig.Append('|').Append(e0.L - _taskbarLeft).Append(',').Append(e0.T - _taskbarTop)
                   .Append(',').Append(e0.R - _taskbarLeft).Append(',').Append(e0.B - _taskbarTop);
            }
            string sigText = sig.ToString();
            if (sigText == _lastRegionSig) return;
            _lastRegionSig = sigText;
            Log.Debug($"apply taskbar region (full): {sigText}");

            if (excl.Length == 0)
            {
                // 没有浮岛需要留空：把区域还回默认矩形（等价于不裁剪）。
                // 长期挂着一个「全矩形区域」会让任务栏一直是区域窗口，重绘更重。
                NativeMethods.SetWindowRgn(tray, IntPtr.Zero, true);
                return;
            }

            var rgnFull = NativeMethods.CreateRectRgn(0, 0, trayW, trayH);
            try
            {
                foreach (var ex in excl)
                {
                    int l = ex.L - _taskbarLeft;
                    int t = ex.T - _taskbarTop;
                    int r = ex.R - _taskbarLeft;
                    int b = ex.B - _taskbarTop;
                    if (r <= l || b <= t) continue;
                    var hole = NativeMethods.CreateRectRgn(l, t, r, b);
                    if (hole != IntPtr.Zero)
                    {
                        NativeMethods.CombineRgn(rgnFull, rgnFull, hole, NativeMethods.RGN_DIFF);
                        NativeMethods.DeleteObject(hole);
                    }
                }
            }
            catch { }
            NativeMethods.SetWindowRgn(tray, rgnFull, true);
            return;
        }

        var isig = "I:" + c1 + "," + c2 + "," + t1 + "," + t2 + "," + trayW + "x" + trayH;
        if (isig == _lastRegionSig) return;
        _lastRegionSig = isig;

        var rgnCenter = CreateSmoothTopRoundedRgn(c1, 0, c2, trayH);
        var rgnTray = CreateSmoothTopRoundedRgn(t1, 0, t2, trayH);

        var rgnTotal = NativeMethods.CreateRectRgn(0, 0, 0, 0);
        NativeMethods.CombineRgn(rgnTotal, rgnCenter, rgnTray, NativeMethods.RGN_OR);
        NativeMethods.SetWindowRgn(tray, rgnTotal, true);

        NativeMethods.DeleteObject(rgnCenter);
        NativeMethods.DeleteObject(rgnTray);
    }
}

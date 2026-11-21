using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskBar.Services;

/// <summary>用户设置。持久化为 JSON（优先程序运行目录 settings.json，兼容读取 %APPDATA%\DeskBar\settings.json）。</summary>
internal sealed class AppSettings
{
    // ===== 通用 =====
    public bool DoubleClickToggle { get; set; } = true;
    public bool ShowTrayIcon { get; set; } = true;
    public bool RunAtStartup { get; set; } = false;

    // ===== 任务栏外观 =====
    public bool TranslucentTaskbar { get; set; } = true;
    public string TaskbarStyle { get; set; } = "island_acrylic"; // clear | acrylic | island_acrylic
    public string AcrylicTone { get; set; } = "natural"; // light | dark | natural | softmist | sheertights
    public int TaskbarBlurAmount { get; set; } = 18;

    // ===== 自动主题 =====
    public bool AutoTimeTheme { get; set; } = true;
    public string LightStartTime { get; set; } = "07:00"; // 白天开始
    public string DarkStartTime { get; set; } = "19:00";  // 夜晚开始

    /// <summary>白天时段使用浅色（false = 深色）。用户可自由组合，不再由代码隐式推导。</summary>
    public bool DayThemeLight { get; set; } = true;

    /// <summary>夜晚时段使用浅色（false = 深色）。</summary>
    public bool NightThemeLight { get; set; } = false;

    /// <summary>白天时段任务栏/浮岛使用的色调键。</summary>
    public string DayAcrylicTone { get; set; } = "softmist";

    /// <summary>夜晚时段任务栏/浮岛使用的色调键。</summary>
    public string NightAcrylicTone { get; set; } = "sheertights";

    /// <summary>自动切换时是否连 Windows 全局深浅色一起改（关闭则只影响 DeskBar 自身外观）。</summary>
    public bool AutoTimeApplySystemTheme { get; set; } = true;

    /// <summary>
    /// 联动系统主题时，是否写入 SystemUsesLightTheme（Windows 模式：任务栏、开始菜单、通知中心等系统界面）。
    /// 默认 true —— 与新增本项之前「两个值一起写」的行为一致，升级后外观不变。
    /// </summary>
    public bool AutoTimeApplyWindowsMode { get; set; } = true;

    /// <summary>
    /// 联动系统主题时，是否写入 AppsUseLightTheme（应用模式：资源管理器、设置等应用的深浅色）。
    /// 默认 true —— 同上，保证老配置升级后行为与改造前完全一致。
    /// </summary>
    public bool AutoTimeApplyAppsMode { get; set; } = true;

    /// <summary>
    /// 运行时覆盖色调：自动主题按时段接管时，由 App 写入当前时段的色调，不落盘。
    /// 为 null 表示色调由用户在「任务栏美化」页手动指定。
    /// </summary>
    [JsonIgnore]
    public string? RuntimeTone { get; set; }

    /// <summary>所有渲染路径应当使用的色调：时段接管优先，否则用用户手选值。</summary>
    [JsonIgnore]
    public string EffectiveTone => RuntimeTone ?? AcrylicTone;

    // ===== 音乐胶囊 =====
    public bool MusicCapsuleEnabled { get; set; } = true;
    public bool MusicCapsuleAutoHide { get; set; } = true;
    public string MusicCapsuleWhitelist { get; set; } = ""; // 逗号分隔，空为全部
    /// <summary>
    /// 设置页「正在播放」区块的显隐开关。它只决定那块列表画不画、刷不刷新，
    /// 不改变白名单本身的生效范围 —— 关掉之后白名单照样按 MusicCapsuleWhitelist 过滤。
    /// </summary>
    public bool MusicCapsuleLiveSourceEnabled { get; set; } = true;
    /// <summary>
    /// 用户手动固定的媒体来源（AppId）。空表示自动选择。
    /// 以前只存在内存里，重启就丢，用户每次开机都得重新点一遍。
    /// </summary>
    public string MusicCapsulePinnedSource { get; set; } = "";
    public int MusicCapsuleWidth { get; set; } = 250;
    public bool MusicCapsuleShowControls { get; set; } = true;
    /// <summary>
    /// 用户自备的歌词字体文件（.ttf / .otf / .ttc），空表示用系统默认字体。
    /// 不内置字体文件：MiSans 这类好看的可变字体动辄 10MB 以上，
    /// 而且许可往往不是能随本程序分发的（MiSans 就不适用 MIT），
    /// 由用户自行导入既避开体积也避开授权问题。
    /// </summary>
    public string MusicCapsuleLyricFontPath { get; set; } = "";

    // ===== 硬件监控浮岛 =====
    public bool SystemMonitorEnabled { get; set; } = true;
    public bool WirelessBatteryEnabled { get; set; } = true;
    public Dictionary<string, string> WirelessBatteryCustomNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> WirelessBatteryDeviceTypes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, WirelessBatteryHistoryEntry> WirelessBatteryHistory { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonIgnore] private readonly object _wirelessBatteryHistoryLock = new();

    /// <summary>
    /// 连续缺席多久才算「真的断开」（模式二据此隐藏胶囊）。
    /// 取 <see cref="WirelessBatteryService.DisconnectGrace"/>：单次漏读也只缺席一轮，不该让胶囊消失一轮。
    /// </summary>

    internal IReadOnlyList<WirelessBatteryHistoryEntry> GetWirelessBatteryHistory()
    {
        lock (_wirelessBatteryHistoryLock)
            return WirelessBatteryHistory.Values.Select(entry => entry.Copy()).ToArray();
    }

    internal IReadOnlyList<WirelessBatteryHistoryEntry> MergeWirelessBatteryHistory(IEnumerable<WirelessBatteryDevice> devices, bool scanComplete)
    {
        WirelessBatteryHistoryEntry[] merged;
        bool shouldSave;
        lock (_wirelessBatteryHistoryLock)
        {
            WirelessBatteryHistory ??= new Dictionary<string, WirelessBatteryHistoryEntry>(StringComparer.OrdinalIgnoreCase);
            bool changed = false;
            var presentDevices = new Dictionary<string, WirelessBatteryDevice>(StringComparer.OrdinalIgnoreCase);
            foreach (var device in devices)
            {
                if (string.IsNullOrWhiteSpace(device.Id)) continue;
                presentDevices[device.Id] = device;
                WirelessBatteryHistory.TryGetValue(device.Id, out var old);
                string name = string.IsNullOrWhiteSpace(device.Name) ? old?.Name ?? "无线设备" : device.Name;
                bool hasCurrentPercent = device.Percent is >= 0 and <= 100;
                int percent = hasCurrentPercent ? device.Percent : old?.Percent ?? -1;
                string connection = string.IsNullOrWhiteSpace(device.Connection) ? old?.Connection ?? "无线设备" : device.Connection;
                bool isConnected = device.IsConnected;
                bool connectionKnown = device.ConnectionKnown;
                bool stale = device.IsStale || (!hasCurrentPercent && old?.Percent >= 0);
                if (old is null)
                {
                    WirelessBatteryHistory[device.Id] = new WirelessBatteryHistoryEntry
                    {
                        Id = device.Id,
                        Name = name,
                        Type = device.Type,
                        Percent = percent,
                        Connection = connection,
                        LastSeenUtc = DateTime.UtcNow,
                        // 显示模式是用户设置；新设备用默认「连接时显示」。
                        DisplayMode = WirelessBatteryDisplayModes.Normalize(device.DisplayMode),
                        IsConnected = isConnected,
                        ConnectionKnown = connectionKnown,
                        IsStale = stale
                    };
                    changed = true;
                    continue;
                }

                // DisplayMode 不在这里改：它是用户设置，扫描只更新可测量的字段。
                bool persistentChanged = old.Name != name || old.Type != device.Type || old.Percent != percent || old.Connection != connection
                    || !old.IsConnected || old.MissingSinceUtc is not null;
                old.IsConnected = isConnected;
                old.ConnectionKnown = connectionKnown;
                old.IsStale = stale;
                old.MissingSinceUtc = null;   // 读到了：清掉缺席起点
                if (persistentChanged)
                {
                    old.Name = name;
                    old.Type = device.Type;
                    old.Percent = percent;
                    old.Connection = connection;
                    old.LastSeenUtc = DateTime.UtcNow;
                    changed = true;
                }
            }
            if (scanComplete)
            {
                foreach (var entry in WirelessBatteryHistory.Values)
                {
                    bool present = presentDevices.TryGetValue(entry.Id, out var current);
                    if (present)
                    {
                        bool known = current!.ConnectionKnown;
                        bool stale = current.IsStale || (current.Percent is < 0 or > 100 && entry.Percent >= 0);
                        if (!entry.IsConnected) { entry.IsConnected = true; changed = true; }
                        if (entry.MissingSinceUtc is not null) { entry.MissingSinceUtc = null; changed = true; }
                        entry.ConnectionKnown = known;
                        entry.IsStale = stale;
                        continue;
                    }

                    // 未读到：本轮读取器异常时不据此推断断开；完整扫描才启动/推进缺席宽限。
                    entry.ConnectionKnown = false;
                    entry.IsStale = entry.Percent >= 0;
                    if (scanComplete)
                    {
                        if (entry.MissingSinceUtc is null)
                        {
                            entry.MissingSinceUtc = DateTime.UtcNow;
                            changed = true;
                        }
                        else if (entry.IsConnected && DateTime.UtcNow - entry.MissingSinceUtc.Value >= WirelessBatteryService.DisconnectGrace)
                        {
                            entry.IsConnected = false;
                            changed = true;
                        }
                    }
                }
            }
            else
            {
                foreach (var entry in WirelessBatteryHistory.Values)
                {
                    if (presentDevices.TryGetValue(entry.Id, out var current))
                    {
                        entry.ConnectionKnown = current.ConnectionKnown;
                        entry.IsConnected = current.IsConnected;
                        entry.IsStale = current.IsStale || (current.Percent is < 0 or > 100 && entry.Percent >= 0);
                    }
                    else
                    {
                        entry.ConnectionKnown = false;
                        entry.IsStale = entry.Percent >= 0;
                    }
                }
            }
            merged = WirelessBatteryHistory.Values.Select(entry => entry.Copy()).ToArray();
            shouldSave = changed;
        }
        if (shouldSave) Save();
        return merged;
    }

    internal void SetWirelessBatteryDisplayMode(string id, string mode)
    {
        string normalized = WirelessBatteryDisplayModes.Normalize(mode);
        bool changed = false;
        lock (_wirelessBatteryHistoryLock)
        {
            if (WirelessBatteryHistory.TryGetValue(id, out var entry) && entry.DisplayMode != normalized)
            {
                entry.DisplayMode = normalized;
                entry.Hidden = false;   // 迁移用的旧字段，一旦有新值就清掉
                changed = true;
            }
        }
        if (changed) Save();
    }

    internal void RemoveWirelessBatteryHistory(string id)
    {
        bool changed;
        lock (_wirelessBatteryHistoryLock)
        {
            changed = WirelessBatteryHistory.Remove(id);
            changed |= WirelessBatteryCustomNames.Remove(id);
            changed |= WirelessBatteryDeviceTypes.Remove(id);
        }
        if (changed) Save();
    }

    internal string GetWirelessBatteryName(string id, string fallback)
    {
        lock (_wirelessBatteryHistoryLock)
            return WirelessBatteryCustomNames.TryGetValue(id, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
    }

    internal string GetWirelessBatteryType(string id, string fallback)
    {
        lock (_wirelessBatteryHistoryLock)
            return WirelessBatteryDeviceTypes.TryGetValue(id, out var value) && IsKnownWirelessBatteryType(value) ? value : fallback;
    }

    internal void SetWirelessBatteryName(string id, string value)
    {
        lock (_wirelessBatteryHistoryLock)
        {
            if (string.IsNullOrWhiteSpace(value)) WirelessBatteryCustomNames.Remove(id);
            else WirelessBatteryCustomNames[id] = value.Trim();
        }
    }

    internal void SetWirelessBatteryType(string id, string value)
    {
        lock (_wirelessBatteryHistoryLock)
        {
            if (!IsKnownWirelessBatteryType(value) || value == "auto") WirelessBatteryDeviceTypes.Remove(id);
            else WirelessBatteryDeviceTypes[id] = value;
        }
    }

    internal static bool IsKnownWirelessBatteryType(string? value) => value is "auto" or "headset" or "keyboard" or "mouse" or "controller" or "other";

    // ===== 更新 =====
    /// <summary>启动后是否自动检查一次更新（默认关闭，避免无谓的网络请求）。</summary>
    public bool AutoCheckUpdate { get; set; } = false;

    /// <summary>
    /// 下载加速前缀。GitHub 直链在国内经常连不上，默认走 ghproxy 类中转。
    /// 留空表示直连 https://github.com。用户可在设置页改成任意中转站。
    /// </summary>
    public string UpdateAccelPrefix { get; set; } = UpdateService.DefaultAccelPrefix;

    // ===== 诊断 =====
    public bool EnableDebugLog { get; set; } = false;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true
    };

    public static string SettingsDir => AppContext.BaseDirectory;

    public static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

    public static string LegacySettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskBar", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (s is not null)
                {
                    s.Normalize();
                    return s;
                }
            }
            else if (File.Exists(LegacySettingsPath))
            {
                var json = File.ReadAllText(LegacySettingsPath);
                var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (s is not null)
                {
                    s.Normalize();
                    try
                    {
                        Directory.CreateDirectory(SettingsDir);
                        File.WriteAllText(SettingsPath, json);
                        Log.Info($"migrated settings from {LegacySettingsPath} to {SettingsPath}");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"failed to migrate settings to base directory: {ex.Message}");
                    }
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"settings load failed, using defaults: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            lock (_wirelessBatteryHistoryLock)
            {
                Directory.CreateDirectory(SettingsDir);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
            }
            Log.Info("settings saved");
        }
        catch (Exception ex)
        {
            Log.Error($"settings save failed: {ex.Message}");
        }
    }

    /// <summary>把旧配置与非法值收敛到当前支持的取值集合。</summary>
    private void Normalize()
    {
        TaskbarStyle = TaskbarStyle switch
        {
            "clear" or "acrylic" or "island_acrylic" => TaskbarStyle,
            // island_clear（“部分透明”）与 clear 视觉重复，早已从选项移除
            _ => "island_acrylic"
        };

        // 旧版本里“随系统自动”是 AcrylicTone 的一个取值，靠读系统深浅色推导白天=柔雾、夜晚=黑丝。
        // 现在昼夜方案已显式成为字段，把这个隐式别名搬进方案里，避免用户看到“设置变了”。
        var tone = (AcrylicTone ?? "").ToLowerInvariant();
        if (tone == "auto")
        {
            DayAcrylicTone = "softmist";
            NightAcrylicTone = "sheertights";
            AcrylicTone = "natural";
            tone = "natural";
        }
        AcrylicTone = IsKnownTone(tone) ? tone : "natural";
        DayAcrylicTone = IsKnownTone(DayAcrylicTone) ? DayAcrylicTone : "softmist";
        NightAcrylicTone = IsKnownTone(NightAcrylicTone) ? NightAcrylicTone : "sheertights";

        TaskbarBlurAmount = Math.Clamp(TaskbarBlurAmount, 0, 100);
        MusicCapsuleWidth = Math.Clamp(MusicCapsuleWidth, 200, 520);
        MusicCapsuleWhitelist ??= "";
        MusicCapsulePinnedSource ??= "";
        // 字体文件可能被用户删掉/挪走，这里只做「路径存在与否」的轻量校验，
        // 真正的格式校验留给加载方（GlyphTypeface 对坏文件抛 FileFormatException）。
        MusicCapsuleLyricFontPath ??= "";
        MusicCapsuleLyricFontPath = MusicCapsuleLyricFontPath.Trim();
        WirelessBatteryCustomNames = new Dictionary<string, string>(WirelessBatteryCustomNames ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        WirelessBatteryDeviceTypes = new Dictionary<string, string>(WirelessBatteryDeviceTypes ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        WirelessBatteryHistory = new Dictionary<string, WirelessBatteryHistoryEntry>(WirelessBatteryHistory ?? new Dictionary<string, WirelessBatteryHistoryEntry>(), StringComparer.OrdinalIgnoreCase);
        foreach (var key in WirelessBatteryDeviceTypes.Keys.ToArray())
            if (!IsKnownWirelessBatteryType(WirelessBatteryDeviceTypes[key]) || WirelessBatteryDeviceTypes[key] == "auto") WirelessBatteryDeviceTypes.Remove(key);
        // 迁移旧版二态 Hidden：只在没有有效 DisplayMode 时转成 never；迁移后清掉旧标记。
        foreach (var entry in WirelessBatteryHistory.Values)
        {
            bool legacyHidden = entry.Hidden;
            bool hasDisplayMode = WirelessBatteryDisplayModes.IsKnown(entry.DisplayMode);
            if (legacyHidden && !hasDisplayMode)
                entry.DisplayMode = WirelessBatteryDisplayModes.Never;
            else
                entry.DisplayMode = WirelessBatteryDisplayModes.Normalize(entry.DisplayMode);
            entry.Hidden = false;
        }
        if (!TryNormalizeTime(LightStartTime, out var lt)) lt = TimeSpan.FromHours(7);
        if (!TryNormalizeTime(DarkStartTime, out var dt)) dt = TimeSpan.FromHours(19);
        LightStartTime = FormatTime(lt);
        DarkStartTime = FormatTime(dt);

        // 加速前缀：null 归一化为默认值；用户显式清空（空串）要保留，表示直连
        if (UpdateAccelPrefix is null) UpdateAccelPrefix = UpdateService.DefaultAccelPrefix;
        UpdateAccelPrefix = UpdateAccelPrefix.Trim();
        // 给了前缀却没带协议头，补 https 免得拼出相对地址
        if (UpdateAccelPrefix.Length > 0 && !UpdateAccelPrefix.Contains("://"))
            UpdateAccelPrefix = "https://" + UpdateAccelPrefix;
    }

    public static bool IsKnownTone(string? tone) => (tone ?? "").ToLowerInvariant() switch
    {
        "light" or "dark" or "natural" or "softmist" or "sheertights" => true,
        _ => false
    };

    /// <summary>解析 HH:mm（也接受 H:mm / HH:m 等宽松写法）。</summary>
    public static bool TryNormalizeTime(string? text, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!TimeSpan.TryParse(text.Trim(), out var parsed)) return false;
        if (parsed < TimeSpan.Zero || parsed >= TimeSpan.FromDays(1)) return false;

        // HH:mm 语义只需要时分，秒与毫秒一律截掉，避免 07:00:59 这种输入导致边界抖动
        value = new TimeSpan(parsed.Hours, parsed.Minutes, 0);
        return true;
    }

    public static string FormatTime(TimeSpan value) => $"{value.Hours:00}:{value.Minutes:00}";
}

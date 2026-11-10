using System.IO;
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
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
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

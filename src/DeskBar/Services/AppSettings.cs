using System.IO;
using System.Text.Json;

namespace DeskBar.Services;

/// <summary>鐢ㄦ埛璁剧疆銆傛寔涔呭寲涓?JSON锛堝搴旇皟鐮斿缓璁殑 settings.json 褰㈡€侊級銆?/summary>
internal sealed class AppSettings
{
    public bool DoubleClickToggle { get; set; } = true;
    public bool ShowTrayIcon { get; set; } = true;
    public bool RunAtStartup { get; set; } = false;
    public bool TranslucentTaskbar { get; set; } = true;
    public string TaskbarStyle { get; set; } = "island_acrylic"; // clear | acrylic | island_acrylic
    public string AcrylicTone { get; set; } = "natural"; // auto | light | dark | natural
    public bool AutoTimeTheme { get; set; } = true;
    public string LightStartTime { get; set; } = "07:00";
    public string DarkStartTime { get; set; } = "19:00";
    public string ThemeMode { get; set; } = "system"; // system | light | dark
    public bool EnableDebugLog { get; set; } = false; // 杩愯璋冭瘯鏃ュ織寮€鍏?(UI 鎺у埗)

    // 闊充箰鑳跺泭璁剧疆
    public bool MusicCapsuleEnabled { get; set; } = true; // 音乐胶囊总开关
    public bool MusicCapsuleAutoHide { get; set; } = true; // 无播放时自动平滑收缩隐藏
    public string MusicCapsuleWhitelist { get; set; } = ""; // 播放器白名单（逗号分隔，空为全部）
    public int MusicCapsuleMaxWidth { get; set; } = 220; // 胶囊最大宽度 (px)
    public bool MusicCapsuleShowControls { get; set; } = true; // 是否显示上一曲/暂停/下一曲控件

    // 硬件监控浮岛设置
    public bool SystemMonitorEnabled { get; set; } = true; // 任务栏左侧硬件监控浮岛

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true
    };

    public static string SettingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskBar");

    public static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (s is not null) return s;
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
}
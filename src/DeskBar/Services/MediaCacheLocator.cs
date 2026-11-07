using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DeskBar.Services;

/// <summary>音乐来源类型。决定歌词只从对应平台的缓存读取，避免跨平台串到别的版本。</summary>
internal enum MediaSourceKind
{
    Unknown = 0,
    QQMusic,
    Netease,
    Kugou,
    Kuwo,
    Soda
}

/// <summary>
/// 从 SMTC 的 SourceAppUserModelId 识别音源。
/// 对应 WaterSprite `cache-worker.cjs:7` 的按音源分流调度。
/// </summary>
internal static class MediaSourceDetector
{
    public static MediaSourceKind Detect(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return MediaSourceKind.Unknown;

        string lower = appId.ToLowerInvariant();
        if (lower.Contains("qqmusic") || lower.Contains("qq音乐")) return MediaSourceKind.QQMusic;
        if (lower.Contains("cloudmusic") || lower.Contains("netease") || lower.Contains("网易云"))
            return MediaSourceKind.Netease;
        if (lower.Contains("kugou") || lower.Contains("酷狗")) return MediaSourceKind.Kugou;
        if (lower.Contains("kuwo") || lower.Contains("kwmusic") || lower.Contains("酷我"))
            return MediaSourceKind.Kuwo;
        if (lower.Contains("sodamusic") || lower.Contains("汽水") || lower.Contains("luna"))
            return MediaSourceKind.Soda;
        return MediaSourceKind.Unknown;
    }

    public static string Describe(MediaSourceKind kind) => kind switch
    {
        MediaSourceKind.QQMusic => "QQ音乐",
        MediaSourceKind.Netease => "网易云音乐",
        MediaSourceKind.Kugou => "酷狗音乐",
        MediaSourceKind.Kuwo => "酷我音乐",
        MediaSourceKind.Soda => "汽水音乐",
        _ => "未知来源"
    };
}

/// <summary>
/// 播放器歌词缓存目录发现器。
/// 移植 WaterSprite `media-cache-paths.cjs` 的思路：注册表安装根 + 进程路径 + BFS 广度优先，
/// 取代原来「枚举盘符 + 硬编码相对路径」的猜测式做法。
/// 结果按音源缓存 60 秒，避免每首歌都重扫磁盘。
/// </summary>
internal static class MediaCacheLocator
{
    private const int MaxDirectories = 600;
    private const int MaxDepth = 4;
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private static readonly object _lock = new();
    private static readonly Dictionary<MediaSourceKind, (DateTime At, List<string> Dirs)> _memo = new();

    /// <summary>BFS 时跳过的噪音目录，对应 WaterSprite 的 skip 名单。</summary>
    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "log", "logs", "crash", "crashpad", "html",
        "qmbrowser", "cef", "res", "resources", "skin", "skins", "backup",
        "temp", "tmp", "cache", "dumps", "module", "plugins", "web", "ui"
    };

    /// <summary>版本号目录，如 "21.51"、"9.1.0"。安装根常在这些目录下，需要上溯一层。</summary>
    private static readonly Regex VersionDirRegex = new(@"^\d+(?:[._-]\d+){1,}", RegexOptions.Compiled);

    /// <summary>返回该音源所有已知的歌词缓存目录。</summary>
    public static IReadOnlyList<string> FindLyricDirs(MediaSourceKind kind)
    {
        if (kind == MediaSourceKind.Unknown) return Array.Empty<string>();

        lock (_lock)
        {
            if (_memo.TryGetValue(kind, out var hit) && DateTime.UtcNow - hit.At < Ttl)
                return hit.Dirs;
        }

        var found = new List<string>();
        try { Discover(kind, found); } catch (Exception ex) { Log.Debug($"cache locator: {kind} discover failed: {ex.Message}"); }

        var distinct = found
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(d => d.Length > 0)
            .ToList();

        lock (_lock) { _memo[kind] = (DateTime.UtcNow, distinct); }
        return distinct;
    }

    /// <summary>清空缓存，供设置变更后强制重扫。</summary>
    public static void Invalidate()
    {
        lock (_lock) { _memo.Clear(); }
    }

    private static string[] TargetNames(MediaSourceKind kind) => kind switch
    {
        MediaSourceKind.QQMusic => new[] { "QQMusicLyricNew", "QQMusicPicture" },
        MediaSourceKind.Kugou => new[] { "Lyric", "Lyrics" },
        MediaSourceKind.Kuwo => new[] { "Lyric", "Lyrics", "歌词" },
        _ => Array.Empty<string>()
    };

    private static void Discover(MediaSourceKind kind, List<string> found)
    {
        string[] targets = TargetNames(kind);
        if (targets.Length == 0) return;

        // 0) 先做一次廉价的「已知位置直查」。
        //    用户的 QQ 音乐常把缓存重定向到 D:\QQMusicCache 这类盘符根下的自建目录，
        //    它既不在系统目录里也不在安装目录里，BFS 起点覆盖不到。
        ProbeKnownPaths(targets, found);
        if (found.Count > 0)
        {
            Log.Debug($"cache locator: {MediaSourceDetector.Describe(kind)} lyric dirs = {string.Join(" | ", found)} (direct probe)");
            return;
        }

        var starts = new List<string>();
        starts.AddRange(CollectConfigPathRoots(kind));
        starts.AddRange(CollectInstallRoots(kind));
        starts.AddRange(CollectSystemRoots());

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Dir, int Depth)>();
        foreach (var start in starts)
        {
            string full;
            try { full = Path.GetFullPath(start); } catch { continue; }
            if (visited.Add(full)) queue.Enqueue((full, 0));
        }

        int scanned = 0;
        while (queue.Count > 0 && scanned < MaxDirectories)
        {
            var (dir, depth) = queue.Dequeue();
            scanned++;

            string name = Path.GetFileName(dir);
            if (targets.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase)))
            {
                found.Add(dir);
                continue; // 命中即停止下钻，缓存目录内部不再有同名子目录
            }

            if (depth >= MaxDepth) continue;

            string[] subs;
            try { subs = Directory.GetDirectories(dir); } catch { continue; }

            foreach (var sub in subs)
            {
                string subName = Path.GetFileName(sub);
                if (SkipNames.Contains(subName)) continue;
                if (visited.Add(sub)) queue.Enqueue((sub, depth + 1));
            }
        }

        if (found.Count > 0)
            Log.Debug($"cache locator: {MediaSourceDetector.Describe(kind)} lyric dirs = {string.Join(" | ", found)} (scanned {scanned})");
        else
            Log.Debug($"cache locator: {MediaSourceDetector.Describe(kind)} no lyric dir found (scanned {scanned})");
    }

    /// <summary>
    /// 已知位置直查：每个固定盘的根目录 + 若干常见的自建缓存根。
    /// 应对用户把播放器缓存重定向到 D:\QQMusicCache 这种「不在任何系统目录里」的情况。
    /// </summary>
    private static void ProbeKnownPaths(string[] targets, List<string> found)
    {
        var roots = new List<string>();

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                roots.Add(drive.RootDirectory.FullName);
            }
        }
        catch { }

        foreach (var baseRoot in roots.ToList())
        {
            // 盘符根下的自建缓存目录名。
            // QQ 音乐常重定向到 D:\QQMusicCache；酷狗/酷我装在 D 盘时也可能把歌词
            // 放在 D:\KuGouCache、D:\KuGou、D:\KwMusic 这类盘根自建目录里，
            // 它们同样不在系统目录和安装目录下，BFS 起点覆盖不到，必须直查。
            roots.Add(Path.Combine(baseRoot, "QQMusicCache"));
            roots.Add(Path.Combine(baseRoot, "QQMusic"));
            roots.Add(Path.Combine(baseRoot, "Music", "QQMusicCache"));

            roots.Add(Path.Combine(baseRoot, "KuGouCache"));
            roots.Add(Path.Combine(baseRoot, "KugouCache"));
            roots.Add(Path.Combine(baseRoot, "KuGou"));
            roots.Add(Path.Combine(baseRoot, "KuGou8"));

            roots.Add(Path.Combine(baseRoot, "KuwoCache"));
            roots.Add(Path.Combine(baseRoot, "KwMusic"));
            roots.Add(Path.Combine(baseRoot, "Kuwo"));
        }

        foreach (var root in roots)
        {
            foreach (var target in targets)
            {
                string candidate = Path.Combine(root, target);
                if (Directory.Exists(candidate)) found.Add(candidate);
            }
        }
    }

    /// <summary>
    /// 从播放器的配置文件里读出用户自定义的缓存路径。
    /// 对应 WaterSprite `media-cache-paths.cjs:22-34` 的 configPaths：
    /// 解析 .ini/.cfg/.xml/.json 中键名含 cache/lyric/picture/download/缓存/歌词 的值。
    /// QQ 音乐的 %APPDATA%\Tencent\QQMusic\WebkitCachePath.ini 里就写着
    /// `Path=D:\QQMusicCache\WebkitCache2`，据此可以推出 D:\QQMusicCache 这个真实缓存根。
    /// </summary>
    private static IEnumerable<string> CollectConfigPathRoots(MediaSourceKind kind)
    {
        var roots = new List<string>();

        string? configDir = kind switch
        {
            MediaSourceKind.QQMusic => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tencent", "QQMusic"),
            MediaSourceKind.Kugou => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KuGou"),
            MediaSourceKind.Kuwo => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KwMusic"),
            _ => null
        };
        if (configDir == null || !Directory.Exists(configDir)) return roots;

        string[] files;
        try { files = Directory.GetFiles(configDir, "*.*", SearchOption.TopDirectoryOnly); }
        catch { return roots; }

        foreach (var file in files)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext != ".ini" && ext != ".cfg" && ext != ".xml" && ext != ".json") continue;
            if (!(Path.GetFileNameWithoutExtension(file).ToLowerInvariant().Contains("cache")
                  || Path.GetFileNameWithoutExtension(file).ToLowerInvariant().Contains("path")
                  || Path.GetFileNameWithoutExtension(file).ToLowerInvariant().Contains("config")))
                continue;

            string text;
            try
            {
                if (new FileInfo(file).Length > 512 * 1024) continue;
                text = File.ReadAllText(file);
            }
            catch { continue; }

            foreach (Match m in ConfigPathValueRegex.Matches(text))
            {
                string raw = m.Groups[1].Value.Trim().Trim('"');
                if (raw.Length == 0 || raw.Contains('%')) continue;
                try { if (!Path.IsPathRooted(raw)) continue; } catch { continue; }

                // 记下该路径本身与它的两级父目录，交给后面的 BFS 展开
                AddRoot(roots, raw);
                try
                {
                    var parent = Directory.GetParent(raw.TrimEnd('\\', '/'))?.Parent;
                    if (parent != null) AddRoot(roots, parent.FullName);
                }
                catch { }
            }
        }

        return roots;
    }

    /// <summary>从配置文本里抠出路径值：支持 ini 的 `key=value` 与 XML/JSON 的 `"key":"value"`。</summary>
    private static readonly Regex ConfigPathValueRegex = new(
        @"(?im)^\s*[\w\u4e00-\u9fff]*(?:path|dir|cache|download|缓存|歌词)[\w\u4e00-\u9fff]*\s*=\s*([^\r\n]+)",
        RegexOptions.Compiled);

    /// <summary>注册表 + 运行中进程 + 卸载表，找出播放器安装目录。</summary>
    private static IEnumerable<string> CollectInstallRoots(MediaSourceKind kind)
    {
        var roots = new List<string>();

        string[] exeNames = kind switch
        {
            MediaSourceKind.QQMusic => new[] { "QQMusic.exe" },
            MediaSourceKind.Kugou => new[] { "KGMusic.exe", "KuGou.exe" },
            MediaSourceKind.Kuwo => new[] { "KwMusic.exe", "KwMusic2013.exe" },
            _ => Array.Empty<string>()
        };
        if (exeNames.Length == 0) return roots;

        // 1) 正在运行的播放器进程
        foreach (var exe in exeNames)
        {
            string procName = Path.GetFileNameWithoutExtension(exe);
            try
            {
                foreach (var p in Process.GetProcessesByName(procName))
                {
                    using (p)
                    {
                        try
                        {
                            string? file = p.MainModule?.FileName;
                            if (!string.IsNullOrEmpty(file)) AddRoot(roots, file);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        // 2) App Paths 注册表（HKCU/HKLM × 64/32 位视图）
        foreach (var exe in exeNames)
        {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                        using var key = baseKey.OpenSubKey(
                            $@"Software\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
                        if (key == null) continue;

                        AddRoot(roots, key.GetValue(null) as string);
                        AddRoot(roots, key.GetValue("Path") as string);
                    }
                    catch { }
                }
            }
        }

        // 3) 厂商配置键里带路径/缓存字样的值
        foreach (var vendorPath in VendorConfigPaths(kind))
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(vendorPath);
                if (key == null) continue;
                foreach (var valueName in key.GetValueNames())
                {
                    string lower = valueName.ToLowerInvariant();
                    if (!(lower.Contains("path") || lower.Contains("dir") || lower.Contains("cache")
                          || lower.Contains("下载") || lower.Contains("缓存")))
                        continue;
                    AddRoot(roots, key.GetValue(valueName) as string);
                }
            }
            catch { }
        }

        // 4) 卸载表里的 InstallLocation（DisplayName 匹配品牌词）
        foreach (var keyword in BrandKeywords(kind))
        {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                        using var uninstall = baseKey.OpenSubKey(
                            @"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                        if (uninstall == null) continue;

                        foreach (var sub in uninstall.GetSubKeyNames())
                        {
                            try
                            {
                                using var entry = uninstall.OpenSubKey(sub);
                                string? display = entry?.GetValue("DisplayName") as string;
                                if (string.IsNullOrEmpty(display) ||
                                    !display.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                                    continue;

                                AddRoot(roots, entry!.GetValue("InstallLocation") as string);
                                AddRoot(roots, entry.GetValue("DisplayIcon") as string);
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
        }

        return roots;
    }

    private static string[] VendorConfigPaths(MediaSourceKind kind) => kind switch
    {
        MediaSourceKind.QQMusic => new[] { @"Software\Tencent\QQMusic", @"Software\Tencent\QQMusic\Config" },
        MediaSourceKind.Kugou => new[] { @"Software\KuGou\KuGou", @"Software\KuGou" },
        MediaSourceKind.Kuwo => new[] { @"Software\kuwo\KwMusic", @"Software\kuwo" },
        _ => Array.Empty<string>()
    };

    private static string[] BrandKeywords(MediaSourceKind kind) => kind switch
    {
        MediaSourceKind.QQMusic => new[] { "QQ音乐", "QQMusic" },
        MediaSourceKind.Kugou => new[] { "酷狗", "KuGou" },
        MediaSourceKind.Kuwo => new[] { "酷我", "Kuwo" },
        _ => Array.Empty<string>()
    };

    /// <summary>系统级用户目录，作为 BFS 起点。</summary>
    private static IEnumerable<string> CollectSystemRoots()
    {
        var list = new List<string>();
        void Add(Environment.SpecialFolder folder)
        {
            try
            {
                string p = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) list.Add(p);
            }
            catch { }
        }

        Add(Environment.SpecialFolder.ApplicationData);
        Add(Environment.SpecialFolder.LocalApplicationData);
        Add(Environment.SpecialFolder.CommonApplicationData);
        Add(Environment.SpecialFolder.MyMusic);
        Add(Environment.SpecialFolder.MyDocuments);
        Add(Environment.SpecialFolder.UserProfile);
        return list;
    }

    /// <summary>
    /// 把 exe 或目录路径规范化成 BFS 起点：去引号、展开环境变量、转目录，
    /// 并对版本号目录逐级上溯（播放器升级后会换版本子目录）。
    /// </summary>
    private static void AddRoot(ICollection<string> roots, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;

        try
        {
            string path = raw.Trim().Trim('"').Trim();
            int comma = path.IndexOf(',');
            if (comma > 0 && path.Substring(comma).TrimStart(',', ' ', '0').Length == 0)
                path = path.Substring(0, comma); // DisplayIcon 常带 ",0"

            path = Environment.ExpandEnvironmentVariables(path);
            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                path = Path.GetDirectoryName(path) ?? "";
            if (path.Length == 0 || !Path.IsPathRooted(path) || !Directory.Exists(path)) return;

            roots.Add(path);

            // 版本号目录上溯
            string? parent = Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(parent))
            {
                string parentName = Path.GetFileName(parent);
                if (!VersionDirRegex.IsMatch(parentName)) break;
                if (Directory.Exists(parent)) roots.Add(parent);
                parent = Path.GetDirectoryName(parent);
            }
        }
        catch { }
    }
}

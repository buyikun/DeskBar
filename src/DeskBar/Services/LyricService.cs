using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace DeskBar.Services;

/// <summary>逐字/逐词时间戳，来自 QRC / YRC 这类带字级时间的歌词格式。</summary>
internal sealed class LyricWord
{
    public TimeSpan Start { get; init; }
    public TimeSpan End { get; init; }
    public string Text { get; init; } = "";
}

/// <summary>
/// 字级时间戳。移植自 WaterSprite 的 chars 模型：把每个「词」的时长按字数均分到单字，
/// 中英文混排时也能按真实演唱速度逐字点亮。
/// </summary>
internal sealed class LyricChar
{
    public char Char { get; init; }
    public double StartMs { get; init; }
    public double DurationMs { get; init; }

    /// <summary>
    /// 把一个「词」按字数均分时间、拆成单字追加到 <paramref name="into"/>。
    /// 对应 WaterSprite `lyric-timeline.mjs` 里 letters.forEach 的 step 均分逻辑。
    /// </summary>
    public static void SplitWord(List<LyricChar> into, string word, double startMs, double durationMs)
    {
        if (string.IsNullOrEmpty(word)) return;

        double step = durationMs / Math.Max(1, word.Length);
        for (int i = 0; i < word.Length; i++)
        {
            into.Add(new LyricChar
            {
                Char = word[i],
                StartMs = startMs + i * step,
                DurationMs = step
            });
        }
    }
}

internal sealed class LyricLine
{
    public TimeSpan Time { get; init; }
    /// <summary>这一句唱完的时刻；没有字级时间戳时用它做线性进度估算。</summary>
    public TimeSpan End { get; init; }
    public string Text { get; init; } = "";
    /// <summary>词级时间戳（QRC / YRC 的原始粒度），用于进度估算。</summary>
    public List<LyricWord> Words { get; init; } = new();

    /// <summary>
    /// 扁平到单字的字级时间戳，用于逐字高亮。为空即行级 LRC（唱到就整行点亮）。
    /// </summary>
    public List<LyricChar> Chars { get; init; } = new();

    /// <summary>只有整行时间、没有字级时间。</summary>
    public bool IsLineLevel => Chars.Count == 0;

    /// <summary>
    /// 这一句的演唱进度 0..1。有字级时间戳时按当前字推进（中英文混排也准），
    /// 否则退化成在整句时长上线性插值。
    /// </summary>
    public double GetProgress(TimeSpan position)
    {
        if (position <= Time) return 0;

        double total = (End - Time).TotalMilliseconds;
        if (total <= 0) return 1;

        if (Words.Count > 0)
        {
            for (int i = 0; i < Words.Count; i++)
            {
                var w = Words[i];
                if (position >= w.End) continue;

                double wordDur = (w.End - w.Start).TotalMilliseconds;
                double frac = wordDur > 0
                    ? Math.Clamp((position - w.Start).TotalMilliseconds / wordDur, 0, 1)
                    : 0;
                double done = (w.Start - Time).TotalMilliseconds + frac * wordDur;
                return Math.Clamp(done / total, 0, 1);
            }
            return 1;
        }

        return Math.Clamp((position - Time).TotalMilliseconds / total, 0, 1);
    }

    /// <summary>
    /// 已唱部分的像素宽度 —— 对应 WaterSprite 的 highlightedWidth。
    /// 有字级时间戳时按每字自己的时长累加宽度（中英混排都准）；
    /// 行级 LRC 没有字级时间，唱到就整行一次性点亮。
    /// </summary>
    /// <param name="charWidths">与 <see cref="Chars"/> 一一对应的逐字像素宽度。</param>
    public double GetPlayedWidth(IReadOnlyList<double> charWidths, TimeSpan position)
    {
        if (position <= Time) return 0;

        double ms = position.TotalMilliseconds;

        if (Chars.Count == 0)
        {
            double all = 0;
            for (int i = 0; i < charWidths.Count; i++) all += charWidths[i];
            return all;
        }

        double sum = 0;
        for (int i = 0; i < Chars.Count; i++)
        {
            double w = i < charWidths.Count ? charWidths[i] : 0;
            // QRC / YRC 给的字起点都是相对整首歌的绝对毫秒，所以这里直接和 position 相减。
            double elapsed = ms - Chars[i].StartMs;
            double frac = Chars[i].DurationMs > 0
                ? Math.Clamp(elapsed / Chars[i].DurationMs, 0, 1)
                : (elapsed >= 0 ? 1 : 0);
            sum += w * frac;
        }
        return sum;
    }
}

internal sealed class TrackLyrics
{
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public List<LyricLine> Lines { get; init; } = new();
    public string? CoverUrl { get; init; }
    public byte[]? CoverBytes { get; init; }

    public LyricLine? GetCurrentLine(TimeSpan position)
    {
        if (Lines.Count == 0) return null;

        LyricLine? current = null;
        for (int i = 0; i < Lines.Count; i++)
        {
            if (Lines[i].Time <= position) current = Lines[i];
            else break;
        }
        return current;
    }

    public string? GetCurrentLyric(TimeSpan position)
    {
        var line = GetCurrentLine(position);
        return string.IsNullOrWhiteSpace(line?.Text) ? null : line!.Text;
    }
}

internal static class LyricService
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    private static readonly ConcurrentDictionary<string, TrackLyrics> _cache = new(StringComparer.OrdinalIgnoreCase);

    // 注意：这里**故意不缓存失败**。
    // TryGetLocalQrc 的候选筛选带「文件名时长与上报时长 4 秒容差」判据，
    // 而切歌瞬间上报的 duration 还是上一首的，第一次匹配必然落空；
    // 解除这个暂时性失败，靠的正是 3 秒轮询带来的自动重试。
    // 曾加过失败缓存（本地 5min / 在线 45s），代价是把「下一轮本来就能成功」的歌
    // 冻住数分钟，用户看到的就是「切歌后一直没歌词」——已回退，不要再加回来。

    static LyricService()
    {
        _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    }

    /// <summary>
    /// 按音源分流取歌词。对应 WaterSprite `cache-worker.cjs:7` 的调度器：
    /// 是 QQ 音乐就只读 QQ 缓存、是网易云就只走网易云接口，不跨平台全局回退
    /// —— 跨平台会拿到别的版本的歌词，时间轴对不上。
    /// </summary>
    public static async Task<TrackLyrics?> GetLyricsAsync(
        string title, string artist, TimeSpan duration = default, MediaSourceKind kind = MediaSourceKind.Unknown)
    {
        if (string.IsNullOrWhiteSpace(title) || title == "未知曲目")
            return null;

        string cacheKey = $"{kind}|{title.Trim()}|{artist?.Trim()}";
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        // 诊断打点：这一行出现几次，就是回退链真正跑了几次。
        Log.Debug($"lyrics fetch: kind={kind} key='{cacheKey}'");

        TrackLyrics? result = kind switch
        {
            // QQ 音乐：只读本地 QRC（逐字），读不到就返回 null，不去网易云凑一个
            MediaSourceKind.QQMusic => TryGetLocalQrc(title, artist ?? "", duration),

            // 网易云音乐：只走网易云接口（yrc 逐字优先，lrc 行级兜底）
            MediaSourceKind.Netease => await TryFetchFromNeteaseAsync(title, artist ?? "", duration),

            // 酷狗 / 酷我：优先读本地逐字歌词（酷狗 KRC），没有就回落行级 LRC
            MediaSourceKind.Kugou => TryGetLocalKrc(title, artist ?? "", duration)
                                      ?? TryGetLocalLrc(title, artist ?? "", kind),
            MediaSourceKind.Kuwo => TryGetLocalLrc(title, artist ?? "", kind),

            // 汽水音乐暂无本地解析，走在线兜底
            MediaSourceKind.Soda => await TryFetchFromNeteaseAsync(title, artist ?? "", duration),

            // 浏览器/未知来源：在线匹配，逐层降级
            _ => await FallbackChainAsync(title, artist ?? "", duration)
        };

        if (result != null && result.Lines.Count > 0)
        {
            _cache[cacheKey] = result;
            return result;
        }

        // 这一轮没取到就如实返回 null，交给下一轮 3 秒轮询重试
        // （切歌瞬间 duration 还是旧值，第二次通常就能命中）。
        return null;
    }

    /// <summary>未知来源的默认链路：本地 QRC → 本地 KRC → 本地 LRC → 网易云 → QQ 接口。</summary>
    private static async Task<TrackLyrics?> FallbackChainAsync(string title, string artist, TimeSpan duration)
    {
        var qrc = TryGetLocalQrc(title, artist, duration);
        if (qrc != null && qrc.Lines.Count > 0) return qrc;

        // 未知来源也可能放的是酷狗的歌，KRC 是逐字歌词，比行级 LRC 更准，排在 LRC 之前
        var krc = TryGetLocalKrc(title, artist, duration);
        if (krc != null && krc.Lines.Count > 0) return krc;

        var lrc = TryGetLocalLrc(title, artist, MediaSourceKind.Unknown);
        if (lrc != null && lrc.Lines.Count > 0) return lrc;

        var netease = await TryFetchFromNeteaseAsync(title, artist, duration);
        if (netease != null && netease.Lines.Count > 0) return netease;

        return await TryFetchFromQQAsync(title, artist);
    }

    private static async Task<TrackLyrics?> TryFetchFromNeteaseAsync(string title, string artist, TimeSpan duration)
    {
        try
        {
            string keyword = $"{title} {artist}".Trim();
            string searchUrl = $"https://music.163.com/api/search/get?type=1&limit=30&s={Uri.EscapeDataString(keyword)}";

            using var searchReq = new HttpRequestMessage(HttpMethod.Get, searchUrl);
            searchReq.Headers.Referrer = new Uri("https://music.163.com/");
            using var searchResp = await _http.SendAsync(searchReq);
            if (!searchResp.IsSuccessStatusCode) return null;

            var jsonStr = await searchResp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonStr);
            if (!doc.RootElement.TryGetProperty("result", out var resEl) ||
                !resEl.TryGetProperty("songs", out var songsEl) ||
                songsEl.GetArrayLength() == 0)
            {
                return null;
            }

            // 严格唯一匹配，对应 WaterSprite `netease-media.cjs:14-18` 的 selectSong：
            // 标题归一化全等 + 歌手归一化全等 + 时长容差内，且**命中必须唯一**。
            // 之前取 songsEl[0] 会拿到同名翻唱/翻自版本，时间轴完全对不上。
            string targetTitle = NormalizeForMatch(title);
            string targetArtist = NormalizeForMatch(artist ?? "");
            int targetSeconds = duration > TimeSpan.Zero ? (int)Math.Round(duration.TotalSeconds) : 0;

            var matches = new List<(long Id, string? PicUrl)>();
            foreach (var song in songsEl.EnumerateArray())
            {
                if (!song.TryGetProperty("name", out var nameEl)) continue;
                if (NormalizeForMatch(nameEl.GetString() ?? "") != targetTitle) continue;

                if (targetArtist.Length > 0)
                {
                    string songArtist = "";
                    if (song.TryGetProperty("artists", out var artistsEl) &&
                        artistsEl.ValueKind == JsonValueKind.Array)
                    {
                        songArtist = string.Concat(artistsEl.EnumerateArray()
                            .Select(a => a.TryGetProperty("name", out var n) ? n.GetString() : null)
                            .Where(s => !string.IsNullOrEmpty(s)));
                    }
                    if (NormalizeForMatch(songArtist) != targetArtist) continue;
                }

                if (targetSeconds > 0 && song.TryGetProperty("duration", out var durEl) &&
                    durEl.TryGetInt64(out long durMs) && durMs > 0)
                {
                    if (Math.Abs(durMs / 1000.0 - targetSeconds) >= 4) continue;
                }

                string? pic = null;
                if (song.TryGetProperty("album", out var albumEl) &&
                    albumEl.TryGetProperty("picUrl", out var picEl))
                {
                    pic = picEl.GetString();
                }

                if (song.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out long sid))
                    matches.Add((sid, pic));
            }

            if (matches.Count != 1)
            {
                Log.Debug($"lyrics '{title}': netease candidates not unique ({matches.Count}), skipped");
                return null;
            }

            long songId = matches[0].Id;
            string? picUrl = matches[0].PicUrl;

            // 获取歌词
            // yv=-1 才会返回 yrc，那是带字级时间戳的逐字歌词（未加密）；
            // lv=-1 是普通行级 LRC，只做兜底。
            string lyricUrl = $"https://music.163.com/api/song/lyric?id={songId}&lv=-1&yv=-1&kv=-1&tv=-1";
            using var lyricReq = new HttpRequestMessage(HttpMethod.Get, lyricUrl);
            lyricReq.Headers.Referrer = new Uri("https://music.163.com/");
            using var lyricResp = await _http.SendAsync(lyricReq);

            List<LyricLine> lines = new();
            if (lyricResp.IsSuccessStatusCode)
            {
                var lyricJson = await lyricResp.Content.ReadAsStringAsync();
                using var lyricDoc = JsonDocument.Parse(lyricJson);

                // 优先逐字歌词，只有它有句内时间可以驱动滚动。
                if (lyricDoc.RootElement.TryGetProperty("yrc", out var yrcEl) &&
                    yrcEl.TryGetProperty("lyric", out var yrcTextEl))
                {
                    lines = ParseYrc(yrcTextEl.GetString() ?? "");
                    if (lines.Count > 0)
                    {
                        int wordCount = 0;
                        foreach (var l in lines) wordCount += l.Words.Count;
                        Log.Debug($"lyrics '{title}': yrc word-level, {lines.Count} lines / {wordCount} words");
                    }
                }

                // 拿不到逐字就退回行级 LRC。
                if (lines.Count == 0 &&
                    lyricDoc.RootElement.TryGetProperty("lrc", out var lrcEl) &&
                    lrcEl.TryGetProperty("lyric", out var textEl))
                {
                    lines = ParseLrc(textEl.GetString() ?? "");
                    Log.Debug($"lyrics '{title}': lrc fallback (line-level), {lines.Count} lines");
                }
            }

            // 下载封面图片字节（走 SafeArtwork：https 强制、私网拒绝、DNS 重绑定防护、魔数验类型）
            byte[]? coverBytes = null;
            if (!string.IsNullOrEmpty(picUrl))
            {
                string imgUrl = picUrl.StartsWith("http:") ? "https:" + picUrl.Substring(5) : picUrl;
                imgUrl += "?param=300y300";
                coverBytes = await SafeArtwork.DownloadAsync(imgUrl);
            }

            return new TrackLyrics
            {
                Title = title,
                Artist = artist ?? "",
                Lines = lines,
                CoverUrl = picUrl,
                CoverBytes = coverBytes
            };
        }
        catch (Exception ex)
        {
            Log.Debug($"Netease lyric/cover fetch error: {ex.Message}");
            return null;
        }
    }

    private static async Task<TrackLyrics?> TryFetchFromQQAsync(string title, string artist)
    {
        try
        {
            string keyword = $"{title} {artist}".Trim();
            string searchUrl = $"https://c.y.qq.com/soso/fcgi-bin/client_search_cp?w={Uri.EscapeDataString(keyword)}&n=1&format=json";
            var resp = await _http.GetStringAsync(searchUrl);
            using var doc = JsonDocument.Parse(resp);
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("song", out var song) &&
                song.TryGetProperty("list", out var list) &&
                list.GetArrayLength() > 0)
            {
                var item = list[0];
                byte[]? coverBytes = null;
                string? albummid = null;
                if (item.TryGetProperty("albummid", out var midEl))
                {
                    albummid = midEl.GetString();
                    if (!string.IsNullOrEmpty(albummid))
                    {
                        string imgUrl = $"https://y.gtimg.cn/music/photo_new/T002R300x300M000{albummid}.jpg";
                        coverBytes = await SafeArtwork.DownloadAsync(imgUrl);
                    }
                }

                return new TrackLyrics
                {
                    Title = title,
                    Artist = artist,
                    CoverBytes = coverBytes
                };
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 酷狗本地 KRC 逐字歌词。KRC 是加密 + zlib 压缩的，解开后与网易云 YRC 文法相同，
    /// 因此解析出来的 <see cref="LyricLine"/> 同样带 <see cref="LyricChar"/>，能走逐字高亮。
    /// 只给酷狗用：酷我/QQ 不会产出 .krc。
    /// </summary>
    private static TrackLyrics? TryGetLocalKrc(string title, string artist, TimeSpan duration)
    {
        try
        {
            string targetName = NormalizeForMatch(title);
            if (targetName.Length == 0) return null;

            string[] artists = SplitArtists(artist ?? "");
            int targetDurationSeconds = duration > TimeSpan.Zero ? (int)Math.Round(duration.TotalSeconds) : 0;

            var matches = new List<string>();
            var dirs = MediaCacheLocator.FindLyricDirs(MediaSourceKind.Kugou);

            foreach (var dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;

                string[] files;
                try { files = Directory.GetFiles(dir, "*.krc"); }
                catch { continue; }

                foreach (var file in files)
                {
                    string baseName = Path.GetFileNameWithoutExtension(file);

                    // 酷狗文件名常带哈希后缀，先剥掉再切 "歌手 - 歌名"，否则歌手段整段是乱码。
                    string stem = KrcHashSuffixRegex.Replace(baseName, string.Empty);
                    string name = NormalizeForMatch(stem);

                    // 纯哈希文件名（剥完只剩哈希）没有可读信息，跳过文件名匹配，
                    // 留到最后按文件内 [ti:]/[ar:] 标签兜底。
                    if (name.Length == 0 || name.Length < 2) continue;

                    if (!name.Contains(targetName)) continue;
                    if (artists.Length > 0 && !artists.All(a => name.Contains(a))) continue;

                    matches.Add(file);
                }

                if (matches.Count > 0) break;
            }

            // 文件名里找不到时，退回扫描文件内标签。只扫有限数量，避免整目录解码拖慢切歌。
            if (matches.Count == 0)
            {
                int scanned = 0;
                foreach (var dir in dirs)
                {
                    if (!Directory.Exists(dir)) continue;
                    string[] files;
                    try { files = Directory.GetFiles(dir, "*.krc"); }
                    catch { continue; }

                    foreach (var file in files)
                    {
                        if (scanned >= KrcTagScanLimit) break;
                        scanned++;

                        var (tagTitle, tagArtist) = KrcDecoder.ReadTags(file);
                        if (NormalizeForMatch(tagTitle) != targetName) continue;
                        if (artists.Length > 0 && !artists.All(a => NormalizeForMatch(tagArtist).Contains(a))) continue;
                        matches.Add(file);
                    }
                    if (matches.Count > 0) break;
                }
            }

            if (matches.Count == 0) return null;

            string? picked = PickUniqueCandidate(matches, title, targetDurationSeconds, "Kugou KRC");
            if (picked == null) return null;

            var lines = KrcDecoder.ParseKrcFile(picked);
            if (lines.Count == 0) return null;

            int words = lines.Sum(l => l.Words.Count);
            Log.Debug($"lyrics '{title}': Kugou KRC word-level, {lines.Count} lines / {words} words, file={Path.GetFileName(picked)}");

            return new TrackLyrics
            {
                Title = title,
                Artist = artist ?? "",
                Lines = lines
            };
        }
        catch (Exception ex)
        {
            Log.Debug($"lyrics '{title}': Kugou KRC lookup failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>酷狗 KRC 文件名末尾的哈希后缀，形如 "-a1b2c3d4e5f6a7b8" 或 "-hash-12-ab12cd"。</summary>
    private static readonly Regex KrcHashSuffixRegex =
        new(@"-[a-f\d]{16,32}(?:-\d+-[a-f\d]+)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>按文件内标签兜底扫描时最多解码多少个 .krc。</summary>
    private const int KrcTagScanLimit = 200;

    private static TrackLyrics? TryGetLocalLrc(string title, string artist, MediaSourceKind kind)
    {
        try
        {
            var searchDirs = new List<string>();

            // 已知音源：只查该平台自己的缓存目录，避免串到别家版本的歌词。
            if (kind is MediaSourceKind.Kugou or MediaSourceKind.Kuwo)
            {
                foreach (var dir in MediaCacheLocator.FindLyricDirs(kind)) searchDirs.Add(dir);
            }
            else
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string myMusic = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);

                if (!string.IsNullOrEmpty(appData))
                {
                    searchDirs.Add(Path.Combine(appData, "KuGou", "Lyric"));
                    searchDirs.Add(Path.Combine(appData, "KuGou8", "Lyric"));
                    searchDirs.Add(Path.Combine(appData, "KuWoMusic", "Lyric"));
                }
                if (!string.IsNullOrEmpty(localAppData))
                {
                    searchDirs.Add(Path.Combine(localAppData, "KuGou", "Lyric"));
                }
                if (!string.IsNullOrEmpty(myMusic))
                {
                    searchDirs.Add(Path.Combine(myMusic, "KuGou", "Lyric"));
                    searchDirs.Add(Path.Combine(myMusic, "Lyric"));
                }
            }

            string targetName = NormalizeForMatch(title);
            if (targetName.Length == 0) return null;

            // 与 QRC 一致：先收集全部候选，命中不唯一就放弃（不同版本互借歌词会错位）。
            var candidates = new List<string>();
            foreach (var dir in searchDirs)
            {
                if (!Directory.Exists(dir)) continue;
                string[] files;
                try { files = Directory.GetFiles(dir, "*.lrc"); }
                catch { continue; }

                foreach (var file in files)
                {
                    string fileName = Path.GetFileNameWithoutExtension(file);
                    if (NormalizeForMatch(fileName).Contains(targetName))
                        candidates.Add(file);
                }
                if (candidates.Count > 0) break;
            }

            if (candidates.Count != 1)
            {
                if (candidates.Count > 1)
                    Log.Debug($"lyrics '{title}': {MediaSourceDetector.Describe(kind)} LRC candidates not unique ({candidates.Count}), skipped");
                return null;
            }

            string text = File.ReadAllText(candidates[0]);
            var parsed = ParseLrc(text);
            if (parsed.Count == 0) return null;

            Log.Debug($"lyrics '{title}': local LRC line-level, {parsed.Count} lines, file={Path.GetFileName(candidates[0])}");
            return new TrackLyrics
            {
                Title = title,
                Artist = artist,
                Lines = parsed
            };
        }
        catch (Exception ex)
        {
            Log.Debug($"lyrics '{title}': local LRC lookup failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// QQ 音乐本地 QRC 逐字歌词缓存。命中时返回带字级时间戳的歌词。
    /// 匹配判据移植 WaterSprite `cache-media.cjs:17` 的 `matchSong`：
    /// 标题 + 歌手 + 时长（4 秒容差）全等，且**候选必须唯一**——歧义就返回 null，
    /// 否则会把翻唱/AI 版的歌词套到原唱上，时间轴全错。
    /// </summary>
    private static TrackLyrics? TryGetLocalQrc(string title, string artist, TimeSpan duration)
    {
        try
        {
            string targetName = NormalizeForMatch(title);
            if (targetName.Length == 0) return null;

            string[] artists = SplitArtists(artist ?? "");
            int targetDurationSeconds = duration > TimeSpan.Zero ? (int)Math.Round(duration.TotalSeconds) : 0;

            // 预告片时长兜底（对应 WaterSprite `cache-media.cjs:18-26`）：
            // 部分场景下播放器只上报 30 秒 / 60 秒的试听片段时长，真实曲目远比它长。
            // 此时用「文件名时长 > 上报时长 + 4」放宽判据，否则永远匹配不到。
            bool previewDuration = targetDurationSeconds is >= 29 and <= 31 or >= 59 and <= 61;

            var matches = new List<string>();

            foreach (var dir in MediaCacheLocator.FindLyricDirs(MediaSourceKind.QQMusic))
            {
                if (!Directory.Exists(dir)) continue;

                string[] files;
                try { files = Directory.GetFiles(dir, "*.qrc"); }
                catch { continue; }

                foreach (var file in files)
                {
                    string baseName = Path.GetFileNameWithoutExtension(file);
                    string name = NormalizeForMatch(baseName);

                    // 标题必须命中（文件名是 "歌手 - 标题 - 时长 - 专辑_qm"）
                    if (!name.Contains(targetName)) continue;

                    // 歌手：有上报就要求全部命中，一处都不中就排除（原唱 vs 翻唱的关键判据）
                    if (artists.Length > 0 && !artists.All(a => name.Contains(a)))
                        continue;

                    // 时长：两边都有值时要求 4 秒容差内；预告片时长只要求「比它长」
                    if (targetDurationSeconds > 0)
                    {
                        var dm = FileDurationRegex.Match(baseName);
                        if (dm.Success && int.TryParse(dm.Groups[1].Value, out int fileSeconds))
                        {
                            if (previewDuration)
                            {
                                if (fileSeconds <= targetDurationSeconds + 4) continue;
                            }
                            else if (Math.Abs(fileSeconds - targetDurationSeconds) > 4)
                            {
                                continue;
                            }
                        }
                    }

                    matches.Add(file);
                }

                if (matches.Count > 0) break; // 前面的目录优先级更高
            }

            if (matches.Count == 0) return null;

            // 唯一性判据（对应 WaterSprite `cache-media.cjs:17` 的 candidates.length === 1）：
            // 多个候选只在「歌手完全相同、只是专辑/文件不同」时才算同一首歌，取时长最接近的一个；
            // 一旦歌手段不同，说明是原唱/翻唱/Live 等不同版本，宁可没有歌词也不配错。
            // 预告片时长无法用来挑版本，此时只接受唯一候选。
            var picked = PickUniqueCandidate(matches, title, previewDuration ? 0 : targetDurationSeconds);
            if (picked == null) return null;

            string hit = picked;
            string? content = QrcDecoder.TryDecodeToLyricText(File.ReadAllBytes(hit));
            if (string.IsNullOrEmpty(content)) return null;

            var lines = QrcDecoder.ParseQrc(content);
            if (lines.Count == 0) return null;

            int words = lines.Sum(l => l.Words.Count);
            Log.Debug($"lyrics '{title}': QQ QRC word-level, {lines.Count} lines / {words} words, file={Path.GetFileName(hit)}");
            return new TrackLyrics
            {
                Title = title,
                Artist = artist ?? "",
                Lines = lines,
                // QQ 分支原本只读本地 QRC，CoverBytes 恒为 null，导致
                // MediaSessionService 的网络封面补全分支（依赖 CoverBytes）永远进不去。
                // 这里挂上播放器自己的封面缓存。
                // 注意：QRC 匹配拿不到专辑名，只能退回「歌手+标题」判据；
                // 主路径（MediaSessionService）已传入 album 走精确匹配，这个是次选兜底。
                CoverBytes = TryGetLocalCover(title, artist ?? "")
            };
        }
        catch (Exception ex)
        {
            Log.Debug($"lyrics '{title}': QQ QRC lookup failed: {ex.GetType().Name}: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// 读取播放器自己缓存的本地封面（目前覆盖 QQ 音乐）。
    ///
    /// 背景：QQ 音乐把封面按「歌手_专辑_编号.jpg」存进 QQMusicPicture（
    /// <see cref="MediaCacheLocator"/> 早已把该目录列入扫描目标），但此前只拿它找 .qrc，
    /// 图片从未被使用。当 SMTC 缩略图不可读时，这是最干净的兜底：读已有缓存，
    /// 不新建磁盘目录、不发网络请求，符合「封面禁止自建磁盘缓存」的约束。
    ///
    /// 判据沿用 WaterSprite `cache-media.cjs:14,40` 的做法：文件名是「歌手_专辑」，
    /// 拼 <c>归一化(歌手)+归一化(专辑)</c> 做全等匹配，且必须唯一。
    /// 之前用「标题 contains」是错的——只有专辑名恰好等于歌名时才碰巧命中
    /// （如《寂寞沙洲冷》），而《幻听》的专辑是「梦游计」，用歌名永远匹配不到。
    /// 专辑缺失时才退回标题匹配，宁可没封面也不配错版本。
    /// </summary>
    internal static byte[]? TryGetLocalCover(string title, string artist, string album = "")
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        string cacheKey = $"cover|{artist}|{album}|{title}";
        var hit = _coverCache.TryGetValue(cacheKey, out var existing) ? existing : null;
        if (hit != null)
        {
            // 空结果也缓存（对应 WaterSprite `kuwo-artwork.cjs:24` 的失败短缓存），
            // 否则「这首歌没有封面」会随每次刷新重扫目录。
            long now = Environment.TickCount64;
            if (now - hit.At < (hit.Bytes != null ? CoverHitTtlMs : CoverMissTtlMs))
                return hit.Bytes;
        }

        var bytes = TryReadLocalCoverFile(title, artist, album);
        _coverCache[cacheKey] = new CoverCacheEntry(Environment.TickCount64, bytes);

        // 容量上限，防止长会话线性增长（对应 `kuwo-artwork.cjs:38` 的 memo.size>128 淘汰）
        if (_coverCache.Count > CoverCacheLimit)
        {
            foreach (var stale in _coverCache.Keys.Take(_coverCache.Count - CoverCacheLimit))
                _coverCache.TryRemove(stale, out _);
        }

        return bytes;
    }

    /// <summary>封面缓存的失败结果只记一小段时间，避免长会话内存膨胀但又不必每轮重扫。</summary>
    private const long CoverHitTtlMs = 30 * 60 * 1000;
    private const long CoverMissTtlMs = 5 * 60 * 1000;
    private const int CoverCacheLimit = 64;

    private sealed record CoverCacheEntry(long At, byte[]? Bytes);

    private static readonly ConcurrentDictionary<string, CoverCacheEntry> _coverCache = new();

    /// <summary>QQ 音乐封面文件名的「歌手_专辑_编号.扩展名」结构，编号是分辨率档位。</summary>
    private static readonly Regex CoverSuffixRegex = new(@"_(\d+)\.(jpg|png|webp)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>播放器自带的占位图，不是真实封面（对应 `cache-media.cjs:14` 的 Albumdefault 排除）。</summary>
    private static readonly Regex CoverPlaceholderRegex = new(@"^(Albumdefault|HQ|SQ)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static byte[]? TryReadLocalCoverFile(string title, string artist, string album)
    {
        try
        {
            string[] artists = SplitArtists(artist ?? "");
            string targetTitle = NormalizeForMatch(title);
            string targetAlbum = NormalizeForMatch(album ?? "");

            // 主判据：歌手+专辑全等；专辑没上报时退回标题包含匹配
            bool useAlbum = targetAlbum.Length > 0;
            string targetKey = useAlbum ? NormalizeForMatch(artist ?? "") + targetAlbum : "";

            // 收集全部候选。目录只有几十项，重扫开销由外层 _coverCache 的
            // 成功/失败双档 TTL 兜住，命中缓存时根本不会走到这里。
            var matches = new List<(int Rank, string File)>();
            foreach (var dir in MediaCacheLocator.FindLyricDirs(MediaSourceKind.QQMusic))
            {
                if (!Directory.Exists(dir)) continue;

                string[] files;
                try { files = Directory.GetFiles(dir, "*.jpg"); }
                catch { continue; }

                foreach (var file in files)
                {
                    string fileName = Path.GetFileName(file);
                    string stem = Path.GetFileNameWithoutExtension(file);

                    var suffix = CoverSuffixRegex.Match(fileName);
                    string baseName = suffix.Success ? fileName.Substring(0, suffix.Index) : stem;
                    if (CoverPlaceholderRegex.IsMatch(baseName)) continue;

                    string normalizedBase = NormalizeForMatch(baseName);
                    if (normalizedBase.Length == 0) continue;

                    if (useAlbum)
                    {
                        // 「歌手_专辑」拼起来全等，多歌手用 _ 连接时也成立
                        if (normalizedBase != targetKey) continue;
                    }
                    else
                    {
                        if (!normalizedBase.Contains(targetTitle)) continue;
                        if (artists.Length > 0 && !artists.All(a => normalizedBase.Contains(a))) continue;
                    }

                    int rank = suffix.Success ? int.Parse(suffix.Groups[1].Value) : -1;
                    matches.Add((rank, file));
                }
            }

            if (matches.Count == 0) return null;

            // 同一专辑可能有多个分辨率档位（实测 _4=500x500、_0=150x150），
            // 取档位最大（最清晰）的那张，而不是像参考实现那样遇到多候选就放弃。
            matches.Sort((a, b) => b.Rank.CompareTo(a.Rank));
            string picked = matches[0].File;

            byte[] bytes = File.ReadAllBytes(picked);
            if (bytes.Length == 0 || bytes.Length > 1024 * 1024) return null;
            if (!SafeArtwork.TryDetectImageMime(bytes, out _)) return null;

            Log.Debug($"cover '{title}': local QQMusicPicture hit, file={Path.GetFileName(picked)}");
            return bytes;
        }
        catch (Exception ex)
        {
            Log.Debug($"cover '{title}': local picture lookup failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 从多个 QRC 候选里挑出唯一的那个文件。
    /// 文件名形如 "歌手 - 标题 - 257 - 专辑_qm"，按第一段（歌手）归一化分组：
    /// 只有一组 → 都是同一首歌的不同专辑副本，取时长最接近的；
    /// 多组 → 原唱/翻唱/Live 版本冲突，返回 null 并记日志。
    /// </summary>
    private static string? PickUniqueCandidate(List<string> matches, string title, int targetDurationSeconds, string label = "QQ QRC")
    {
        if (matches.Count == 1) return matches[0];

        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in matches)
        {
            string baseName = Path.GetFileNameWithoutExtension(file);
            int sep = baseName.IndexOf(" - ", StringComparison.Ordinal);
            string artistKey = NormalizeForMatch(sep > 0 ? baseName.Substring(0, sep) : baseName);
            if (!groups.TryGetValue(artistKey, out var list))
            {
                list = new List<string>();
                groups[artistKey] = list;
            }
            list.Add(file);
        }

        if (groups.Count > 1)
        {
            Log.Debug($"lyrics '{title}': {label} ambiguous ({matches.Count} files, {groups.Count} different artists), " +
                      "refused to guess an edition");
            return null;
        }

        // 同一首歌的多张专辑副本：优先时长最接近的
        if (targetDurationSeconds > 0)
        {
            string bestFile = matches[0];
            int bestDelta = int.MaxValue;
            foreach (var file in matches)
            {
                var dm = FileDurationRegex.Match(Path.GetFileNameWithoutExtension(file));
                if (!dm.Success || !int.TryParse(dm.Groups[1].Value, out int secs)) continue;
                int delta = Math.Abs(secs - targetDurationSeconds);
                if (delta < bestDelta) { bestDelta = delta; bestFile = file; }
            }
            return bestFile;
        }

        return matches[0];
    }

    /// <summary>QQ 音乐 QRC 文件名里的时长秒数，形如 "歌手 - 标题 - 257 - 专辑_qm"。</summary>
    private static readonly Regex FileDurationRegex = new(@"\s-\s(\d{1,4})\s-\s", RegexOptions.Compiled);

    /// <summary>
    /// 归一化用于精确匹配：NFKC 折叠全半角后只保留字母与数字，其余（空白、标点、
    /// 多歌手分隔符 / 、 &amp; _ 等）一律删掉。
    /// 对应 WaterSprite `cache-media.cjs:7` 的 `replace(/[^\p{L}\p{N}]/gu,'')`
    /// —— 不逐个枚举分隔符，才不会漏掉新的写法。
    /// </summary>
    private static string NormalizeForMatch(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        string folded = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        return NonAlphaNumericRegex.Replace(folded, string.Empty);
    }

    private static readonly Regex NonAlphaNumericRegex = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

    /// <summary>多歌手拆分：系统可能上报 "陈奕迅/王菲"，文件名里常常只写其中一位。</summary>
    private static readonly Regex ArtistSplitRegex = new(@"[/\\、&·;；|,]+", RegexOptions.Compiled);

    private static string[] SplitArtists(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return Array.Empty<string>();
        return ArtistSplitRegex
            .Split(artist)
            .Select(NormalizeForMatch)
            .Where(s => s.Length > 0)
            .Distinct()
            .ToArray();
    }

    public static List<LyricLine> ParseLrc(string rawLrc)
    {
        var result = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(rawLrc)) return result;

        var lines = rawLrc.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var regex = new Regex(@"\[(\d{1,2}):(\d{2})(?:\.(\d{1,3}))?\](.*)");

        foreach (var line in lines)
        {
            var match = regex.Match(line);
            if (!match.Success) continue;

            int min = int.Parse(match.Groups[1].Value);
            int sec = int.Parse(match.Groups[2].Value);
            int ms = 0;
            if (match.Groups[3].Success)
            {
                string msStr = match.Groups[3].Value;
                if (msStr.Length == 1) ms = int.Parse(msStr) * 100;
                else if (msStr.Length == 2) ms = int.Parse(msStr) * 10;
                else if (msStr.Length >= 3) ms = int.Parse(msStr.Substring(0, 3));
            }

            var time = new TimeSpan(0, 0, min, sec, ms);
            // 一行可能带多个时间戳（如 [00:12.00][00:40.00]歌词），必须把剩余的也剥掉，
            // 否则第二行会原样显示成 "[00:40.00]歌词"。
            string content = Regex.Replace(
                match.Groups[4].Value,
                @"^\s*(?:\[\d{1,2}:\d{2}(?:[.:]\d{1,3})?\])+\s*",
                "").Trim();

            // 过滤无实质内容的元数据标签与制作人员署名
            if (string.IsNullOrWhiteSpace(content)) continue;
            if (QrcDecoder.CreditRegex.IsMatch(content)) continue;

            result.Add(new LyricLine { Time = time, Text = content });
        }

        result.Sort((a, b) => a.Time.CompareTo(b.Time));
        FillLineEnds(result);
        return result;
    }

    /// <summary>行级 LRC 不带句尾时间，用下一行的开始时间补上（最后一句给 4 秒）。</summary>
    private static void FillLineEnds(List<LyricLine> lines)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            var end = i + 1 < lines.Count
                ? lines[i + 1].Time
                : lines[i].Time + TimeSpan.FromSeconds(4);
            if (end <= lines[i].Time) end = lines[i].Time + TimeSpan.FromSeconds(1);

            lines[i] = new LyricLine
            {
                Time = lines[i].Time,
                End = end,
                Text = lines[i].Text,
                Words = lines[i].Words,
                Chars = lines[i].Chars
            };
        }
    }

    /// <summary>
    /// 解析 QRC / 网易云 YRC 这类逐字歌词：
    /// [行起点,行时长](字起点,字时长,0)字(字起点,字时长,0)字...
    /// 时间单位是毫秒。解析思路参照 WaterSprite 的 vendor/qq-lyrics/qrc2lrc.js。
    /// </summary>
    public static List<LyricLine> ParseYrc(string rawYrc)
    {
        var result = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(rawYrc)) return result;

        var lineMatches = Regex.Matches(rawYrc, @"\[(\d+)\s*,\s*(\d+)\]");

        for (int i = 0; i < lineMatches.Count; i++)
        {
            var lineMatch = lineMatches[i];
            long lineStart = long.Parse(lineMatch.Groups[1].Value);
            long lineDur = long.Parse(lineMatch.Groups[2].Value);

            int bodyStart = lineMatch.Index + lineMatch.Length;
            int bodyEnd = i + 1 < lineMatches.Count ? lineMatches[i + 1].Index : rawYrc.Length;
            if (bodyEnd <= bodyStart) continue;

            string body = rawYrc.Substring(bodyStart, bodyEnd - bodyStart).Trim('\r', '\n', ' ', '\t');
            if (body.Length == 0) continue;

            var words = new List<LyricWord>();
            var chars = new List<LyricChar>();
            var text = new System.Text.StringBuilder();
            var wordMatches = Regex.Matches(body, @"\((\d+)\s*,\s*(\d+)\s*,\s*\d+\)");

            for (int j = 0; j < wordMatches.Count; j++)
            {
                var wordMatch = wordMatches[j];
                int textStart = wordMatch.Index + wordMatch.Length;
                int textEnd = j + 1 < wordMatches.Count ? wordMatches[j + 1].Index : body.Length;
                if (textEnd < textStart) textEnd = textStart;

                string wordText = body.Substring(textStart, textEnd - textStart);
                long wordStart = long.Parse(wordMatch.Groups[1].Value);
                long wordDur = long.Parse(wordMatch.Groups[2].Value);

                words.Add(new LyricWord
                {
                    Start = TimeSpan.FromMilliseconds(wordStart),
                    End = TimeSpan.FromMilliseconds(wordStart + wordDur),
                    Text = wordText
                });
                LyricChar.SplitWord(chars, wordText, wordStart, wordDur);
                text.Append(wordText);
            }

            string line = (words.Count > 0
                ? text.ToString()
                : Regex.Replace(body, @"\(\d+\s*,\s*\d+\s*,\s*\d+\)", "")).Trim();

            if (string.IsNullOrWhiteSpace(line)) continue;

            // 网易云 yrc 头部会带「作词 : 小柯 / 作曲 : 小柯」这类署名行
            if (QrcDecoder.CreditRegex.IsMatch(line)) continue;

            result.Add(new LyricLine
            {
                Time = TimeSpan.FromMilliseconds(lineStart),
                End = TimeSpan.FromMilliseconds(lineStart + (lineDur > 0 ? lineDur : 4000)),
                Text = line,
                Words = words,
                Chars = chars
            });
        }

        result.Sort((a, b) => a.Time.CompareTo(b.Time));
        return result;
    }
}

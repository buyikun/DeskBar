using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DeskBar.Services;

/// <summary>
/// 媒体源的过滤与打分。移植 WaterSprite `source-names.cjs` 的 excludedSource + `source-router.cjs` 的 rank 排序。
///
/// 原实现是 `foreach (var s in sessions) { if (IsAppAllowed(appId)) { target = s; break; } }`，
/// 也就是「按 GSMTC 返回顺序取第一个能用的」——等于随机。候选里有系统合成器、模拟器、
/// 甚至 GPU 驱动容器，于是胶囊会莫名其妙跳到无关进程上。
/// </summary>
internal static class MediaSourceRouter
{
    /// <summary>
    /// 明确排除的来源。这类进程会注册媒体会话但不是「用户想看的播放器」：
    /// 抖音（短视频自动播放，会把正在听的歌顶掉）、
    /// 雷电模拟器（Android 模拟器进程组，内部任何 App 的声音都会挂到这些宿主上）。
    /// 对应 WaterSprite `source-names.cjs:7` 的 excludedSource。
    /// </summary>
    private static readonly Regex ExcludedSourceRegex = new(
        @"douyin|抖音|" +
        @"dnplayer|ldplayer|ld9boxheadless|ldvboxheadless|ldvboxsvc|ldboxheadless|ldboxsvc|leidian",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 系统级进程黑名单。这些进程可能持有媒体会话（音频合成、缩略图、代理），
    /// 但它们从来不是内容来源。对应 WaterSprite `source-names.cjs` 的系统进程表。
    /// </summary>
    private static readonly HashSet<string> SystemBlocklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "nvcontainer", "svchost", "audiodg", "dllhost", "rundll32",
        "system", "taskhostw", "textinputhost", "sihost", "dwm"
    };

    /// <summary>专用音乐播放器，最高优先级。</summary>
    private static readonly Regex MusicPlayerRegex = new(
        @"qqmusic|qq音乐|cloudmusic|netease|网易云|kugou|酷狗|kuwo|kwmusic|酷我|" +
        @"sodamusic|汽水|luna|spotify|applemusic|amlibraryagent|foobar2000|" +
        @"potplayer|vlc|mpc-hc|wmplayer|mediaplayer",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>浏览器里的网页媒体，次之。</summary>
    private static readonly Regex BrowserRegex = new(
        @"msedge|microsoftedge|chrome|firefox|brave|opera|vivaldi|bilibili|哔哩",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>直播 / 视频类，再次之。</summary>
    private static readonly Regex LiveVideoRegex = new(
        @"live|stream|obs|vlc.*stream|twitch|youtube|douyu|斗鱼|huya|虎牙",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>该来源是否应被直接丢弃（既不进源列表，也不参与自动选择）。</summary>
    public static bool IsExcluded(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return true;

        if (ExcludedSourceRegex.IsMatch(appId)) return true;

        // 形如 "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" 的打包应用，
        // 取末段包名再做系统进程比对。
        string probe = appId;
        int bang = appId.LastIndexOf('!');
        if (bang >= 0 && bang + 1 < appId.Length) probe = appId.Substring(bang + 1);

        string bare = probe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? probe.Substring(0, probe.Length - 4)
            : probe;

        return SystemBlocklist.Contains(bare);
    }

    /// <summary>
    /// 来源类型的中文短标签，用于右键菜单里把「自动选择为什么会挑它」写明白。
    /// 与 <see cref="Rank"/> 共用同一套正则，避免两处判断漂移。
    /// </summary>
    public static string DescribeKind(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return "";

        if (MusicPlayerRegex.IsMatch(appId)) return "音乐";
        if (BrowserRegex.IsMatch(appId)) return "浏览器";
        if (LiveVideoRegex.IsMatch(appId)) return "视频";
        return "其他";
    }

    /// <summary>
    /// 优先级：音乐播放器 3 &gt; 浏览器 2 &gt; 直播/视频 1 &gt; 其他 0。
    /// 对应 WaterSprite `source-router.cjs` 的 rank。
    /// </summary>
    public static int Rank(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return 0;

        if (MusicPlayerRegex.IsMatch(appId)) return 3;
        if (BrowserRegex.IsMatch(appId)) return 2;
        if (LiveVideoRegex.IsMatch(appId)) return 1;
        return 0;
    }
}

/// <summary>
/// 自动选源的路由器。对应 WaterSprite `source-router.cjs` 的 SourceRouter.choose
/// 与 `media-state.cjs` 的「暂停不切源」。
///
/// 排序键（逐级比较）：
///   1. rank 高者优先（音乐播放器 &gt; 浏览器 &gt; 直播 &gt; 其他）
///   2. 后出现的优先（会话的 FirstSeen 更晚 = 用户刚启动的那个）
///   3. 前台进程优先
///   4. 当前源优先（避免无谓抖动）
///
/// 额外规则：当前源还在、且没有别的源正在播放时，**不切走**。
/// 否则「暂停一下」「开个新标签页但没播放」都会把胶囊跳走。
/// </summary>
internal static class MediaSourcePicker
{
    /// <summary>当前源从会话列表里消失后，还要坚持多久才认输。</summary>
    public static readonly TimeSpan DepartGrace = TimeSpan.FromMilliseconds(1500);

    public sealed class Candidate
    {
        public string AppId { get; init; } = "";
        public bool IsPlaying { get; init; }
        public int Rank { get; init; }
        public long FirstSeenTicks { get; init; }
        public bool IsForeground { get; init; }
        public bool IsCurrent { get; init; }
    }

    /// <summary>
    /// 在候选里挑一个。返回 null 表示「保持现状」。调用方负责落实选择结果。
    /// </summary>
    /// <param name="candidates">已过滤掉排除项的候选，按 GSMTC 原始顺序。</param>
    /// <param name="currentAppId">当前正在显示的来源；没有则 null。</param>
    /// <param name="currentPresent">当前来源是否仍在本轮会话列表里。</param>
    /// <param name="currentLastSeen">当前来源最后一次出现在列表里的时刻（UTC）。</param>
    /// <param name="nowUtc">当前时刻（UTC）。</param>
    public static Candidate? Choose(
        IReadOnlyList<Candidate> candidates,
        string? currentAppId,
        bool currentPresent,
        DateTime nowUtc,
        DateTime? currentLastSeen = null)
    {
        if (candidates.Count == 0) return null;

        // 找出哪个是当前源
        Candidate? current = null;
        if (!string.IsNullOrEmpty(currentAppId))
        {
            foreach (var c in candidates)
            {
                if (string.Equals(c.AppId, currentAppId, StringComparison.OrdinalIgnoreCase))
                {
                    current = c;
                    break;
                }
            }
        }

        // 有没有「别的源正在播放」？只有它才有资格顶掉当前源。
        bool othersPlaying = false;
        foreach (var c in candidates)
        {
            if (current != null && ReferenceEquals(c, current)) continue;
            if (c.IsPlaying) { othersPlaying = true; break; }
        }

        // 当前源仍在列表里：只要没有别的源真的在播，就保持不动。
        // 这就是「暂停不切源」——暂停只是 IsPlaying 变 false，不该换歌。
        if (currentPresent && !othersPlaying)
            return current;

        // 当前源已从列表消失：给它一个宽限期，期内仍然保持。
        // 播放器切歌、短暂重建会话都会造成一次「消失」，不该立刻跳源。
        if (!currentPresent && !string.IsNullOrEmpty(currentAppId)
            && currentLastSeen.HasValue
            && nowUtc - currentLastSeen.Value < DepartGrace)
        {
            return null;
        }

        // 真正开始挑选：排序后取第一个
        Candidate? best = null;
        foreach (var c in candidates)
        {
            if (best == null || IsBetter(c, best)) best = c;
        }
        return best;
    }

    /// <summary>a 是否严格优于 b。键顺序与 WaterSprite 一致。</summary>
    private static bool IsBetter(Candidate a, Candidate b)
    {
        if (a.Rank != b.Rank) return a.Rank > b.Rank;

        // 后出现的优先：用户刚打开的更可能是想要的那个
        if (a.FirstSeenTicks != b.FirstSeenTicks) return a.FirstSeenTicks > b.FirstSeenTicks;

        if (a.IsForeground != b.IsForeground) return a.IsForeground;

        if (a.IsCurrent != b.IsCurrent) return a.IsCurrent;

        // 都在播的话，优先正在播的，避免选中一个已暂停的
        return a.IsPlaying && !b.IsPlaying;
    }

    /// <summary>取当前前台窗口所属进程的 appId 形式（用于「前台优先」判据）。</summary>
    public static string? TryGetForegroundAppKey()
    {
        try
        {
            IntPtr hwnd = Interop.NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;

            Interop.NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return null;

            using var proc = Process.GetProcessById((int)pid);
            string name = proc.ProcessName;
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }
}

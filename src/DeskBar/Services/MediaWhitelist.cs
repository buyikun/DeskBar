using System;
using System.Collections.Generic;

namespace DeskBar.Services;

/// <summary>
/// 「播放器白名单」的解析与匹配。
///
/// 为什么单独抽出来：原来的实现只有一行
/// <c>_settings.MusicCapsuleWhitelist.Split(new[] { ',', ';' }, ...)</c>，配 <c>appId.Contains(item)</c>。
/// 两个问题都会让胶囊「静默消失」——用户看不到任何报错，只会觉得功能坏了：
///
/// ① 分隔符只认半角 <c>,</c> <c>;</c>。中文输入法默认打出的是全角 <c>，</c>，
///    于是整串 "QQMusic，cloudmusic" 被当成一个关键字，谁都不匹配，胶囊直接不显示。
/// ② 只比对 appId（英文串）。用户在右键菜单里看到的是「QQ音乐」，照着填中文，
///    结果一条都不匹配。菜单里显示什么名字，白名单里就该认什么名字。
///
/// 所以这里同时做两件事：把常见的中英文分隔符都当分隔符；
/// 匹配时既比对 appId，也比对 <see cref="MediaSessionService.GetFriendlySourceName"/> 的友好名。
/// </summary>
internal static class MediaWhitelist
{
    /// <summary>
    /// 全部当作分隔符的字符。中文输入法会输出全角逗号 / 顿号 / 中文分号，
    /// 用户复制粘贴时还常带空格和换行，这些都不该让整串变成一个巨型关键字。
    /// </summary>
    private static readonly char[] Separators =
    {
        ',', '，',           // 半角 / 全角逗号
        ';', '；',           // 半角 / 全角分号
        '、',                // 顿号
        '|', '｜',           // 竖线
        ' ', '\t', '\r', '\n' // 空白
    };

    /// <summary>把用户填的一串文本拆成关键字列表（已去空白、去空项、忽略大小写去重）。</summary>
    public static List<string> Parse(string? raw)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var item = part.Trim();
            if (item.Length == 0) continue;
            if (seen.Add(item)) result.Add(item);
        }
        return result;
    }

    /// <summary>白名单是否为空（空 = 接受所有播放器）。</summary>
    public static bool IsEmpty(string? raw) => Parse(raw).Count == 0;

    /// <summary>
    /// 单条关键字是否命中某个来源。比对 appId 与友好名两条路径，
    /// 同时抽出来给 <see cref="IsAllowed"/>、<see cref="MatchEntry"/> 与
    /// <see cref="BuildEntry"/> 共用，避免三处判断漂移。
    /// </summary>
    private static bool MatchesSingle(string appId, string entry)
    {
        if (appId.Contains(entry, StringComparison.OrdinalIgnoreCase)) return true;
        return MediaSessionService.GetFriendlySourceName(appId)
            .Contains(entry, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 在已解析的条目里找第一条命中该来源的下标，没有返回 -1。
    /// 设置页「正在播放」列表点击时用它做 toggle：用户可能手填了 <c>cloudmusic</c>，
    /// 精确比较字符串会找不到，只能按匹配语义反查。
    /// </summary>
    public static int IndexOfMatch(List<string> entries, string appId)
    {
        if (entries == null || entries.Count == 0) return -1;
        for (int i = 0; i < entries.Count; i++)
        {
            if (MatchesSingle(appId, entries[i])) return i;
        }
        return -1;
    }

    /// <summary>
    /// 由实时检测到的来源反推一条「只命中它自己」的白名单关键字，供设置页点选加入。
    ///
    /// 为什么要反推而不是直接把友好名写进去：友好名会被多个来源共享
    /// （「QQ音乐」「酷狗音乐」都含「音乐」），填进去就顺手把同族播放器全放行了 ——
    /// 用户说的「软件识别错误」正是这种宽匹配。所以候选按可读性降序
    /// （友好名 → 裸进程名 → 完整 AppId）逐个拿当前已知的全部来源验证，
    /// 只取第一个「命中目标且不命中任何别的来源」的；全都通不过时退回完整 AppId，
    /// 它本身就是一个精确到来源的串。
    /// </summary>
    /// <param name="appId">要加入白名单的来源。</param>
    /// <param name="knownAppIds">当前系统里能看到的全部来源（用于唯一性校验）。</param>
    public static string BuildEntry(string appId, IReadOnlyList<string> knownAppIds)
    {
        if (string.IsNullOrWhiteSpace(appId)) return "";

        var candidates = new List<string>();
        void Add(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;
            var trimmed = candidate.Trim();
            if (trimmed.Length == 0) return;
            if (candidates.Exists(x => string.Equals(x, trimmed, StringComparison.OrdinalIgnoreCase))) return;
            candidates.Add(trimmed);
        }

        Add(MediaSessionService.GetFriendlySourceName(appId));
        Add(BareProcessName(appId));
        Add(appId);

        foreach (var candidate in candidates)
        {
            if (MatchesSingle(appId, candidate) && !HitsOthers(appId, candidate, knownAppIds))
                return candidate;
        }
        return appId;
    }

    /// <summary>该关键字是否会顺带命中除目标以外的来源。</summary>
    private static bool HitsOthers(string target, string entry, IReadOnlyList<string> knownAppIds)
    {
        foreach (var other in knownAppIds)
        {
            if (string.Equals(other, target, StringComparison.OrdinalIgnoreCase)) continue;
            if (MatchesSingle(other, entry)) return true;
        }
        return false;
    }

    /// <summary>
    /// 取「进程名」形态的候选：打包应用（含 <c>!</c>）取末段包名，再去掉 <c>.exe</c>。
    /// 与 <see cref="MediaSourceRouter.IsExcluded"/> 的探针同一口径。
    /// </summary>
    private static string BareProcessName(string appId)
    {
        string probe = appId;
        int bang = appId.LastIndexOf('!');
        if (bang >= 0 && bang + 1 < appId.Length) probe = appId.Substring(bang + 1);

        return probe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? probe.Substring(0, probe.Length - 4)
            : probe;
    }

    /// <summary>
    /// 该来源是否被允许。空白名单一律放行；填了白名单但拆不出任何关键字时也放行
    /// （纯分隔符输入属于手误，不该把胶囊整个锁死）。
    /// </summary>
    public static bool IsAllowed(string? appId, string? raw)
    {
        var entries = Parse(raw);
        if (entries.Count == 0) return true;
        if (string.IsNullOrWhiteSpace(appId)) return false;

        // 友好名参与匹配：菜单里显示「QQ音乐」，用户就会填「QQ音乐」。
        foreach (var entry in entries)
        {
            if (MatchesSingle(appId, entry)) return true;
        }
        return false;
    }

    /// <summary>
    /// 检查某个 appId 是否被白名单里的哪一条命中，返回命中的关键字（未命中返回 null）。
    /// 设置页拿它做实时反馈：让用户看得见「我填的东西到底匹配上了谁」。
    /// </summary>
    public static string? MatchEntry(string? appId, string? raw)
    {
        var entries = Parse(raw);
        if (entries.Count == 0 || string.IsNullOrWhiteSpace(appId)) return null;

        foreach (var entry in entries)
        {
            if (MatchesSingle(appId, entry)) return entry;
        }
        return null;
    }
}

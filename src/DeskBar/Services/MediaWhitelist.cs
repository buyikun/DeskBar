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
    /// 该来源是否被允许。空白名单一律放行；填了白名单但拆不出任何关键字时也放行
    /// （纯分隔符输入属于手误，不该把胶囊整个锁死）。
    /// </summary>
    public static bool IsAllowed(string? appId, string? raw)
    {
        var entries = Parse(raw);
        if (entries.Count == 0) return true;
        if (string.IsNullOrWhiteSpace(appId)) return false;

        // 友好名参与匹配：菜单里显示「QQ音乐」，用户就会填「QQ音乐」。
        string friendly = MediaSessionService.GetFriendlySourceName(appId);

        foreach (var entry in entries)
        {
            if (appId.Contains(entry, StringComparison.OrdinalIgnoreCase)) return true;
            if (friendly.Contains(entry, StringComparison.OrdinalIgnoreCase)) return true;
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

        string friendly = MediaSessionService.GetFriendlySourceName(appId);
        foreach (var entry in entries)
        {
            if (appId.Contains(entry, StringComparison.OrdinalIgnoreCase)) return entry;
            if (friendly.Contains(entry, StringComparison.OrdinalIgnoreCase)) return entry;
        }
        return null;
    }
}

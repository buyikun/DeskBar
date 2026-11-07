namespace DeskBar.Services;

/// <summary>一个时段的完整外观方案：深浅色 + 任务栏色调。</summary>
internal sealed class ThemePlan : IEquatable<ThemePlan>
{
    /// <summary>true = 浅色，false = 深色。</summary>
    public bool IsLight { get; init; } = true;

    /// <summary>任务栏/浮岛使用的色调键（light|dark|natural|softmist|sheertights）。</summary>
    public string Tone { get; init; } = "softmist";

    public bool Equals(ThemePlan? other)
        => other is not null
           && other.IsLight == IsLight
           && string.Equals(other.Tone, Tone, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => Equals(obj as ThemePlan);

    public override int GetHashCode() => HashCode.Combine(IsLight, Tone.ToLowerInvariant());

    public override string ToString() => $"{(IsLight ? "Light" : "Dark")}/{Tone}";
}

/// <summary>
/// 时间段判定。支持跨夜（夜晚开始早于白天开始，例如 19:00 → 07:00）与全天同一时刻的退化输入。
/// </summary>
internal static class ThemeSchedule
{
    /// <summary>判断某时刻是否落在白天段。</summary>
    public static bool IsDay(TimeSpan now, TimeSpan dayStart, TimeSpan nightStart)
    {
        if (dayStart == nightStart)
        {
            // 两个时刻相同：约定白天为全天的一半，避免出现“永久白天/永久夜晚”这种无意义状态
            return true;
        }

        if (dayStart < nightStart)
        {
            // 常规排布：07:00 → 19:00 为白天
            return now >= dayStart && now < nightStart;
        }

        // 跨夜：19:00 → 07:00 之间是夜晚，其余为白天
        return !(now >= nightStart && now < dayStart);
    }

    public static ThemePlan Resolve(TimeSpan now, TimeSpan dayStart, TimeSpan nightStart, ThemePlan day, ThemePlan night)
        => IsDay(now, dayStart, nightStart) ? day : night;

    /// <summary>下一次「时段切换」发生的绝对时间与其将生效的方案。</summary>
    public static (DateTime At, ThemePlan Plan) NextSwitch(
        DateTime now, TimeSpan dayStart, TimeSpan nightStart, ThemePlan day, ThemePlan night)
    {
        var today = now.Date;
        var isDay = IsDay(now.TimeOfDay, dayStart, nightStart);

        // 候选边界：今天与明天的两个切换点
        var candidates = new List<(DateTime At, ThemePlan Plan)>();
        foreach (var dayOffset in new[] { 0, 1 })
        {
            var baseDate = today.AddDays(dayOffset);
            var toDay = baseDate + dayStart;    // 进入白天
            var toNight = baseDate + nightStart; // 进入夜晚
            if (toDay > now) candidates.Add((toDay, day));
            if (toNight > now) candidates.Add((toNight, night));
        }

        if (candidates.Count == 0)
        {
            return (now.AddMinutes(1), isDay ? day : night);
        }

        var next = candidates.OrderBy(c => c.At).First();
        return next;
    }

    /// <summary>把「距下次切换」格式化成“5 小时 12 分后”这样的中文短语。</summary>
    public static string DescribeRemaining(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        if (remaining.TotalMinutes < 1) return "不到 1 分钟后";
        if (remaining.TotalHours >= 1)
        {
            var h = (int)remaining.TotalHours;
            var m = remaining.Minutes;
            return m == 0 ? $"{h} 小时后" : $"{h} 小时 {m} 分后";
        }
        return $"{(int)remaining.TotalMinutes} 分钟后";
    }
}

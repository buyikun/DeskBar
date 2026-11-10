namespace DeskBar.Services;

/// <summary>
/// 任务栏描边的几何常量与圆角计算，是所有布局（居中岛屿 / 整条磨砂 / 整条全透明）的单一事实来源。
///
/// 2026-11-11 用户要求「删掉边缘形状设置、删掉胶囊圆、默认方圆角」，
/// 于是原来的 island / circle 双档被砍掉，只剩**唯一的方圆角档**（圆角取半高的一半，
/// 顶角圆润、底边平直）。不要再把 circle 档加回来。
/// </summary>
internal static class ShapeGeometry
{
    // 注意：这里**没有**「岛模式挖洞半径」的出口。挖洞的顶角形状由
    // TaskbarService 的 SmoothCornerOffsets（10 行）预计算表决定，不是半径数值能表达的。
    // 曾有一个 IslandTopRadiusPx 死函数零调用却数值巧合，
    // 极易被误读成「改这里就能改挖洞圆角」，已删除。要改挖洞去改那张表。

    /// <summary>
    /// 整条模式下描边窗口的圆角半径（DIP）：方圆角，取半高的一半（48 → 12、44 → 11）。
    /// </summary>
    public static double BorderRadius(double heightDip)
    {
        if (heightDip <= 0) return 0;
        return heightDip / 2.0 * 0.5;
    }

    /// <summary>
    /// 描边窗口相对任务栏矩形的垂直内缩（物理像素）。
    ///
    /// 任务栏底边 y 恒等于屏幕底边，四角圆角的下半部分会被屏幕直接裁掉，
    /// 看上去就只剩两个上角，用户读成「圈太小、卡到图标」。
    /// 上下各让出 2px 后四个角都完整可见，浮岛才有「悬浮」感。
    /// 内缩过的窗口高度同时也决定上面的圆角上界，两者必须一起用。
    ///
    /// 两个浮岛窗口（监控 / 音乐）也照此内缩，XAML 里写死 `Height=44`（= 48 − 2×inset）。
    /// **改这个常量必须同步那两个 XAML 的 Height**，否则框高会与描边层不齐。
    /// </summary>
    public const int BorderInsetPx = 2;

    /// <summary>
    /// 描边框左右两端相对内容区的额外留白（物理像素）。
    ///
    /// 任务栏按钮的高亮块是 40px 高的方块，在 44px 的描边窗里距窗顶仅 2px。
    /// 方圆角半径 11 在 dy=2 处内缩 11 − sqrt(11² − 9²) ≈ 4.7px；
    /// 留白设为 16px，使圆弧在接触图标前就已推开，横线完整越过图标、不卡任何边角。
    /// </summary>
    public const int BorderEdgePadPx = 16;
}

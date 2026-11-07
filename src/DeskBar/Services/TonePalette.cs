using System.Collections.Generic;
using System.Windows.Media;
// 项目开了隐式 using，System.Drawing 与 System.Windows.Media 都贡献了 Color/Brush，
// 这里显式取 WPF 版本，否则每个签名都要写全名。
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace DeskBar.Services;

/// <summary>
/// 「柔雾 / 黑丝」材质体系下的描边与交互色阶推导。
///
/// 设计原则（照 WaterSprites/Windhawk-Mod-SoftMist-BlackSheerTights 的做法）：
/// 浅色底用黑色系压边、深色底用白色系提边 —— 同一条边不可能在两种底色下都好看，
/// 所以必须按 tone 分别给值，而不是把 #26FFFFFF 这类白色高光写死在 XAML 里。
///
/// 本类同时缓存已 Freeze 的画刷：IslandBorderWindow 是 16ms tick，
/// 每帧 new SolidColorBrush 会白白制造 GC 压力。
/// </summary>
internal static class TonePalette
{
    /// <summary>该 tone 是否属于「深色底」（黑丝系）。natural / auto / 未知值跟随系统主题。</summary>
    public static bool IsDark(string? tone)
    {
        var t = (tone ?? "natural").ToLowerInvariant();
        return t switch
        {
            "sheertights" => true,
            "dark" => true,
            "softmist" => false,
            "light" => false,
            // natural 是透出壁纸，实际底色取决于系统模式；auto 本身就是跟随系统。
            _ => !NativeTaskbarAppearanceService.IsSystemInLightTheme(),
        };
    }

    // ---------------- 描边 ----------------

    /// <summary>浮岛 / 胶囊的 1px 描边常态色。</summary>
    public static Color BorderColor(string? tone) =>
        IsDark(tone) ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x33, 0x00, 0x00, 0x00);

    public static Brush BorderBrush(string? tone) => Cached($"border:{IsDark(tone)}", BorderColor(tone));

    // ---------------- 交互反馈 ----------------

    /// <summary>悬停填充（叠加在胶囊/按钮自己的透明底上）。</summary>
    public static Color HoverColor(string? tone) =>
        IsDark(tone) ? Color.FromArgb(0x5C, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x4D, 0x00, 0x00, 0x00);

    /// <summary>按下填充，比悬停再深一档。</summary>
    public static Color PressedColor(string? tone) =>
        IsDark(tone) ? Color.FromArgb(0x7A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x66, 0x00, 0x00, 0x00);

    public static Brush HoverBrush(string? tone) => Cached($"hover:{IsDark(tone)}", HoverColor(tone));

    public static Brush PressedBrush(string? tone) => Cached($"pressed:{IsDark(tone)}", PressedColor(tone));

    // ---------------- 详情面板（自带底色，需要整套前景/背景联动） ----------------

    /// <summary>
    /// 面板主底色：与任务栏同源的亚克力透感。
    /// 这里刻意用较低的 alpha —— 面板下面铺的就是 AcrylicWindowHelper 下发的
    /// 任务栏同款亚克力（模糊 + 0x25323232 / 0x25525252 着色），
    /// 底色只要再压一层薄色保证文字可读即可。写太高（原先 0xE6）会把材质彻底盖死，
    /// 视觉上就退化成一整块不透明塑料板。
    /// </summary>
    public static Color PanelColor(string? tone) =>
        IsDark(tone) ? Color.FromArgb(0x99, 0x32, 0x32, 0x32) : Color.FromArgb(0xA6, 0xF2, 0xF2, 0xF2);

    /// <summary>面板外框。</summary>
    public static Color PanelBorderColor(string? tone) =>
        IsDark(tone) ? Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x2E, 0x00, 0x00, 0x00);

    /// <summary>主要文字（标题、数值）。</summary>
    public static Color TextPrimaryColor(string? tone) =>
        IsDark(tone) ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A);

    /// <summary>次要文字（卡片标签、表头）。</summary>
    public static Color TextSecondaryColor(string? tone) =>
        IsDark(tone) ? Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0x00, 0x00, 0x00);

    /// <summary>弱化文字（副标题、单位说明）。</summary>
    public static Color TextMutedColor(string? tone) =>
        IsDark(tone) ? Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x88, 0x00, 0x00, 0x00);

    /// <summary>指标卡片底。</summary>
    public static Color CardColor(string? tone) =>
        IsDark(tone) ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x12, 0x00, 0x00, 0x00);

    /// <summary>指标卡片描边。</summary>
    public static Color CardBorderColor(string? tone) =>
        IsDark(tone) ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0x00, 0x00, 0x00);

    /// <summary>进度条 / 分隔线的轨道底色。</summary>
    public static Color TrackColor(string? tone) =>
        IsDark(tone) ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x22, 0x00, 0x00, 0x00);

    // ---------------- 详情面板：画刷入口 ----------------
    // XAML 侧统一用 {DynamicResource PanelXxxBrush}，代码在窗口构造/换肤时
    // 往 Window.Resources 覆盖同名键即可整套换肤，不必写两份 Style。

    public static Brush PanelBrush(string? tone) => Cached($"panel:{IsDark(tone)}", PanelColor(tone));
    public static Brush PanelBorderBrush(string? tone) => Cached($"panelborder:{IsDark(tone)}", PanelBorderColor(tone));
    public static Brush PanelTextPrimaryBrush(string? tone) => Cached($"tprim:{IsDark(tone)}", TextPrimaryColor(tone));
    public static Brush PanelTextSecondaryBrush(string? tone) => Cached($"tsec:{IsDark(tone)}", TextSecondaryColor(tone));
    public static Brush PanelTextMutedBrush(string? tone) => Cached($"tmuted:{IsDark(tone)}", TextMutedColor(tone));
    public static Brush PanelCardBrush(string? tone) => Cached($"card:{IsDark(tone)}", CardColor(tone));
    public static Brush PanelCardBorderBrush(string? tone) => Cached($"cardborder:{IsDark(tone)}", CardBorderColor(tone));
    public static Brush PanelTrackBrush(string? tone) => Cached($"track:{IsDark(tone)}", TrackColor(tone));

    /// <summary>表头 / 关闭按钮这类「比次要文字再亮一档」的前景。</summary>
    public static Brush PanelTextStrongBrush(string? tone) =>
        Cached($"tstrong:{IsDark(tone)}",
            IsDark(tone) ? Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xB3, 0x00, 0x00, 0x00));

    /// <summary>面板里的可交互按钮常态底（比卡片再重一点，才看得出可点）。</summary>
    public static Brush PanelActionBrush(string? tone) =>
        Cached($"action:{IsDark(tone)}",
            IsDark(tone) ? Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0x00, 0x00, 0x00));

    /// <summary>面板按钮悬停底。</summary>
    public static Brush PanelActionHoverBrush(string? tone) =>
        Cached($"actionhover:{IsDark(tone)}",
            IsDark(tone) ? Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0x00, 0x00, 0x00));

    /// <summary>面板按钮按下底。</summary>
    public static Brush PanelActionPressedBrush(string? tone) =>
        Cached($"actionpressed:{IsDark(tone)}",
            IsDark(tone) ? Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x3D, 0x00, 0x00, 0x00));

    /// <summary>面板按钮描边（常态 / 悬停）。</summary>
    public static Brush PanelActionBorderBrush(string? tone) =>
        Cached($"actionborder:{IsDark(tone)}",
            IsDark(tone) ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x24, 0x00, 0x00, 0x00));

    public static Brush PanelActionHoverBorderBrush(string? tone) =>
        Cached($"actionhoverborder:{IsDark(tone)}",
            IsDark(tone) ? Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x45, 0x00, 0x00, 0x00));

    /// <summary>CPU 数值这类强调前景。浅色底上必须压深，否则 #80D8FF 几乎看不见。</summary>
    public static Brush AccentPrimaryBrush(string? tone) =>
        Cached($"accent1:{IsDark(tone)}",
            IsDark(tone) ? Color.FromArgb(0xFF, 0x80, 0xD8, 0xFF) : Color.FromArgb(0xFF, 0x0B, 0x6B, 0x9E));

    public static Brush AccentSecondaryBrush(string? tone) =>
        Cached($"accent2:{IsDark(tone)}",
            IsDark(tone) ? Color.FromArgb(0xFF, 0xB0, 0xBE, 0xC5) : Color.FromArgb(0xFF, 0x45, 0x5A, 0x64));

    // ---------------- 指标分类色 ----------------
    // 详情窗用颜色区分 CPU / 内存 / 网络 / GPU 四类指标，一眼能认出是哪一项。
    // 但原来的色值是直接写死在 XAML 里的浅色版本（#0078D4 / #107C41 / #00BCF2），
    // 在深色面板上偏暗、在浅色面板上偏艳，且完全绕开了 tone 体系。
    // 这里按明暗分两套：深色底提亮以保证对比，浅色底压深以保证可读。
    // 色相保持不变 —— 用户要靠色相区分指标，换了色相信息就丢了。

    /// <summary>CPU 指标色（蓝）。</summary>
    public static Brush MetricCpuBrush(string? tone) =>
        Cached($"mcpu:{IsDark(tone)}",
            IsDark(tone) ? Color.FromArgb(0xFF, 0x4F, 0xA8, 0xE8) : Color.FromArgb(0xFF, 0x00, 0x78, 0xD4));

    /// <summary>内存指标色（绿）。</summary>
    public static Brush MetricMemoryBrush(string? tone) =>
        Cached($"mmem:{IsDark(tone)}",
            IsDark(tone) ? Color.FromArgb(0xFF, 0x3F, 0xC1, 0x6E) : Color.FromArgb(0xFF, 0x10, 0x7C, 0x41));

    /// <summary>网络指标色（青）。</summary>
    public static Brush MetricNetworkBrush(string? tone) =>
        Cached($"mnet:{IsDark(tone)}",
            IsDark(tone) ? Color.FromArgb(0xFF, 0x3F, 0xD6, 0xF5) : Color.FromArgb(0xFF, 0x00, 0x86, 0xAD));

    /// <summary>标题装饰色（原来的 #FFD700 金色，深色底上刺眼，提亮改成琥珀）。</summary>
    public static Brush MetricSparkBrush(string? tone) =>
        Cached($"mspark:{IsDark(tone)}",
            IsDark(tone) ? Color.FromArgb(0xFF, 0xF5, 0xC2, 0x42) : Color.FromArgb(0xFF, 0xC7, 0x8A, 0x00));

    // ---------------- 右键菜单：画刷入口 ----------------
    // 菜单是独立 PopupRoot 窗口，拿不到 DWM 亚克力，所以底色靠一层足够厚重的
    // 半透明填充撑住对比度：深色下必须比面板更实（避免文字糊在壁纸上），
    // 浅色下则要压白，否则系统默认的浅灰会被壁纸的暖色污染。

    /// <summary>菜单底：深色近黑玻璃 / 浅色近白雾面。</summary>
    public static Brush MenuSurfaceBrush(string? tone) =>
        Cached($"menusurface:{IsDark(tone)}",
            IsDark(tone) ? Color.FromArgb(0xF2, 0x2B, 0x2B, 0x2B) : Color.FromArgb(0xF5, 0xFA, 0xFA, 0xFA));

    /// <summary>菜单描边：深色提亮、浅色压深。</summary>
    public static Brush MenuBorderBrush(string? tone) =>
        Cached($"menuborder:{IsDark(tone)}",
            IsDark(tone) ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0x00, 0x00, 0x00));

    /// <summary>菜单正文。</summary>
    public static Brush MenuTextBrush(string? tone) =>
        Cached($"menutext:{IsDark(tone)}",
            IsDark(tone) ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A));

    /// <summary>禁用项与次要文字（子菜单箭头、快捷键）。</summary>
    public static Brush MenuTextMutedBrush(string? tone) =>
        Cached($"menumuted:{IsDark(tone)}",
            IsDark(tone) ? Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x8C, 0x00, 0x00, 0x00));

    /// <summary>勾选标记。沿用强调色，浅色底上同样要压深才看得见。</summary>
    public static Brush MenuCheckBrush(string? tone) => AccentPrimaryBrush(tone);

    /// <summary>菜单项悬停 / 按下底。</summary>
    public static Brush MenuItemHoverBrush(string? tone) =>
        Cached($"menuhover:{IsDark(tone)}",
            IsDark(tone) ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0x00, 0x00, 0x00));

    public static Brush MenuItemPressedBrush(string? tone) =>
        Cached($"menupressed:{IsDark(tone)}",
            IsDark(tone) ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0x00, 0x00, 0x00));

    /// <summary>菜单分隔线。</summary>
    public static Brush MenuSeparatorBrush(string? tone) =>
        Cached($"menusep:{IsDark(tone)}",
            IsDark(tone) ? Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1A, 0x00, 0x00, 0x00));

    // ---------------- 画刷缓存 ----------------

    private static readonly Dictionary<string, Brush> Cache = new();

    private static Brush Cached(string key, Color color)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var b)) return b;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            Cache[key] = brush;
            return brush;
        }
    }
}

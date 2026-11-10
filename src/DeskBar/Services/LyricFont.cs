using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace DeskBar.Services;

/// <summary>
/// 用户自备歌词字体的加载器。
///
/// 不内置字体文件的原因：好看的歌词字体（MiSans 等）动辄 10MB+，
/// 且许可常常不允许随第三方程序分发（MiSans 明确不适用 MIT）。
/// 由用户自行导入 .ttf/.otf/.ttc，既避开体积也避开授权。
///
/// WPF 加载字体文件有两条路径，这里都验证过（见 .scratch/fonttest/Probe*.cs）：
///   A. new FontFamily(uri, "./#FamilyName")   —— 指向单个文件
///   B. new FontFamily(uri.AbsoluteUri + "#FamilyName")
/// 两条都能让 Typeface.TryGetGlyphTypeface 返回 true 并正确排版。
/// 族名用 GlyphTypeface.FamilyNames 从文件里读，不信任文件名：
/// 用户可能把 simsun.ttc 改名成「我的字体.ttf」。
///
/// 已知边界（都是实测结论，不是推测）：
///   - 字体文件加载后 WPF 会占用句柄，FileShare.None 打开会 IOException，
///     但改名/替换仍然成功 —— 所以换字体不必退出程序。
///   - 目标字重在文件里不存在时，WPF 自动挑最接近的（SimSun 只有 Normal/Bold，
///     请求 SemiBold 会解析成 Bold），不会抛异常也不会变丑。
///   - 族名写错不抛异常，只是 TryGetGlyphTypeface 返回 false、退回默认字体。
///   - 选到非字体文件抛 FileFormatException —— 用来给用户报错。
///   - 加载成功后 WPF 有字体缓存，同一个文件内容变了要重启才生效，
///     所以设置页提示语写的是「重新打开 DeskBar 生效」。
/// </summary>
internal static class LyricFont
{
    /// <summary>最近一次尝试加载的路径与结果，避免每次 ApplyToneBorder 都去读文件。</summary>
    private static string? _cachedPath;
    private static FontFamily? _cachedFamily;
    private static string? _cachedError;
    private static bool _cacheValid;

    /// <summary>
    /// 解析字体文件，成功返回 FontFamily，失败返回 null 并给出原因。
    /// 同一个路径只真正解析一次（结果含失败都缓存，直到路径变化）。
    /// </summary>
    public static FontFamily? FromFile(string? path, out string? error)
    {
        var p = (path ?? string.Empty).Trim();
        if (p.Length == 0)
        {
            error = null;
            return null;   // 用户没配字体，不是错误
        }

        lock (typeof(LyricFont))
        {
            if (_cacheValid && string.Equals(_cachedPath, p, StringComparison.OrdinalIgnoreCase))
            {
                error = _cachedError;
                return _cachedFamily;
            }

            var family = TryLoad(p, out var err);
            _cachedPath = p;
            _cachedFamily = family;
            _cachedError = err;
            _cacheValid = true;

            if (family is null)
                Log.Warn($"lyric font: '{p}' 无法使用（{err}），回退系统默认字体");
            else
                Log.Info($"lyric font: '{p}' -> {family.Source}");

            error = err;
            return family;
        }
    }

    /// <summary>路径变化后强制下次重新解析（用户在设置里换了字体文件时调用）。</summary>
    public static void Invalidate()
    {
        lock (typeof(LyricFont))
        {
            _cacheValid = false;
            _cachedPath = null;
            _cachedFamily = null;
            _cachedError = null;
        }
    }

    private static FontFamily? TryLoad(string path, out string? error)
    {
        error = null;

        if (!File.Exists(path))
        {
            error = "文件不存在";
            return null;
        }

        var ext = Path.GetExtension(path);
        if (!ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".otf", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".ttc", StringComparison.OrdinalIgnoreCase))
        {
            error = "只支持 .ttf / .otf / .ttc";
            return null;
        }

        Uri uri;
        try
        {
            uri = new Uri(Path.GetFullPath(path));
        }
        catch (Exception ex)
        {
            error = "路径无效：" + ex.Message;
            return null;
        }

        // 从文件里读真实族名，不信文件名
        string familyName;
        try
        {
            var glyph = new GlyphTypeface(uri);
            familyName = PickFamilyName(glyph);
            if (string.IsNullOrWhiteSpace(familyName))
            {
                error = "字体里没有可用的族名";
                return null;
            }
        }
        catch (FileFormatException)
        {
            error = "不是有效的字体文件";
            return null;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + "：" + ex.Message;
            return null;
        }

        // 用族名构造并回验一次：族名写错时这里才会暴露
        try
        {
            var family = new FontFamily(uri, "./#" + familyName);
            var typeface = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            if (!typeface.TryGetGlyphTypeface(out _))
            {
                error = $"字体族「{familyName}」无法用于排版";
                return null;
            }
            return family;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + "：" + ex.Message;
            return null;
        }
    }

    /// <summary>优先英文族名（跨系统稳定），退回任意可用族名。</summary>
    private static string PickFamilyName(GlyphTypeface glyph)
    {
        try
        {
            if (glyph.FamilyNames.TryGetValue(CultureInfo.GetCultureInfo("en-US"), out var en) &&
                !string.IsNullOrWhiteSpace(en))
            {
                return en;
            }
            foreach (var pair in glyph.FamilyNames)
            {
                if (!string.IsNullOrWhiteSpace(pair.Value)) return pair.Value;
            }
        }
        catch { }
        return string.Empty;
    }
}

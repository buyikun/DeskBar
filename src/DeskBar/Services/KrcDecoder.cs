using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace DeskBar.Services;

/// <summary>
/// 酷狗 KRC 逐字歌词解密器。算法移植自 WaterSprite-TaskbarMediaBar
/// (soda-player/kugou-lyrics.cjs)，逐字节等价。
///
/// 流程：校验 "krc1" 魔数 → 逐字节 XOR 16 字节**格式常量** → zlib inflate → UTF-8 文本。
/// 注意这 16 字节是 KRC 的格式常量（写成数组只因为它本身就是二进制），
/// 与任何账号凭据无关；WaterSprite 源码里也明确标注为
/// "KRC's fixed format constant, not an account credential. Independent decoder."。
///
/// 解开之后的正文形如：
///   [ti:歌名]
///   [ar:歌手]
///   [offset:0]
///   [行起点ms,行时长ms]&lt;字起点,字时长,0&gt;字&lt;字起点,字时长,0&gt;字…
/// 也就是「时间在尖括号里、文本跟在后面」，与网易云 YRC 的顺序一致，
/// 与 QQ QRC（文本在前、圆括号在后）相反。
/// </summary>
internal static class KrcDecoder
{
    /// <summary>KRC 固定的 16 字节 XOR 常量（格式常量，非凭据）。</summary>
    private static readonly byte[] Key =
    {
        64, 71, 97, 119, 94, 50, 116, 71, 81, 54, 49, 45, 206, 210, 110, 105
    };

    private const int MaxFileBytes = 1024 * 1024;       // 酷狗歌词文件不会超过 1MB
    private const int MaxInflatedBytes = 4 * 1024 * 1024; // 解压结果上限，防压缩炸弹

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("krc1");

    /// <summary>
    /// 解密 .krc 文件，返回其中的歌词正文（KRC 纯文本）；不是合法 KRC 或解密失败返回 null。
    /// </summary>
    public static string? TryDecodeToLyricText(byte[] raw)
    {
        try
        {
            if (raw.Length < 5 || raw.Length > MaxFileBytes) return null;
            for (int i = 0; i < Magic.Length; i++)
            {
                if (raw[i] != Magic[i]) return null;
            }

            var data = new byte[raw.Length - 4];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)(raw[i + 4] ^ Key[i % Key.Length]);
            }

            return Inflate(data);
        }
        catch
        {
            return null;
        }
    }

    private static string Inflate(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var zs = new ZLibStream(ms, CompressionMode.Decompress);
        using var output = new MemoryStream();

        var buffer = new byte[8192];
        int total = 0;
        while (true)
        {
            int read = zs.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;

            total += read;
            if (total > MaxInflatedBytes) throw new InvalidDataException("KRC 解压结果超出上限");
            output.Write(buffer, 0, read);
        }

        return DecodeText(output.ToArray());
    }

    /// <summary>
    /// 把解压出来的字节转成文本。KRC 正文多数是 UTF-8，但少数老歌是 GB18030，
    /// 还有带 BOM 的 UTF-16；照 WaterSprite 的 text() 做同样的三级回退，
    /// 否则非 UTF-8 的歌词会整段乱码。
    /// </summary>
    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            // throwOnInvalidBytes: true —— 非法 UTF-8 序列直接抛，交给下面的 GB18030 兜底。
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            try
            {
                // GB18030 属于代码页编码，.NET 8 里必须先注册 CodePagesEncodingProvider。
                // 这里用反射激活，避免为此加包引用而改 deps.json（部署只替换业务三件套）。
                EnsureCodePagesRegistered();
                return Encoding.GetEncoding(54936).GetString(bytes); // 54936 = GB18030
            }
            catch
            {
                return Encoding.UTF8.GetString(bytes);
            }
        }
    }

    private static bool _codePagesTried;

    private static void EnsureCodePagesRegistered()
    {
        if (_codePagesTried) return;
        _codePagesTried = true;
        try
        {
            // System.Text.Encoding.CodePages 随 .NET 运行时一起安装，但需要显式注册。
            var providerType = Type.GetType(
                "System.Text.CodePagesEncodingProvider, System.Text.Encoding.CodePages",
                throwOnError: false);
            var instance = providerType?.GetProperty("Instance")?.GetValue(null);
            if (instance is EncodingProvider provider)
            {
                Encoding.RegisterProvider(provider);
            }
        }
        catch
        {
            // 注册失败就维持 UTF-8，不影响主流程。
        }
    }

    private static readonly Encoding StrictUtf8 =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly Regex TagRegex = new(@"^\[(\w+):([^\]]*)]", RegexOptions.Compiled);
    private static readonly Regex LineRegex = new(@"^\[(\d+)\s*,\s*(\d+)\](.*)$", RegexOptions.Compiled);

    /// <summary>
    /// 词级标签 &lt;起点,时长&gt; 或 &lt;起点,时长,0&gt;，后面紧跟文本。
    /// 第三个字段是可选的（部分 KRC 只给两个），所以用 (?:\s*,[^>]*)? 兜住。
    /// </summary>
    private static readonly Regex WordRegex = new(@"<(\d+)\s*,\s*(\d+)(?:\s*,[^>]*)?>([^<]*)", RegexOptions.Compiled);

    /// <summary>
    /// 行正文里可能残留的 LRC 时间戳前缀（一个或多个），例如 "[00:05.00]"。
    /// KRC 已经把行时间放在 [起点,时长] 里了，这里的重复时间戳只应被剥掉。
    /// </summary>
    private static readonly Regex LrcTimestampPrefixRegex =
        new(@"^\s*(?:\[\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?\])+\s*", RegexOptions.Compiled);

    /// <summary>
    /// 解析 KRC 正文。行级 LRC 行（[mm:ss.xx]text）会被跳过，
    /// 交由调用方在逐字行数量为 0 时回落 <see cref="LyricService.ParseLrc"/>。
    /// </summary>
    public static List<LyricLine> ParseKrc(string content)
    {
        var result = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(content)) return result;

        int offset = ReadOffset(content);

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            var lineMatch = LineRegex.Match(line);
            if (!lineMatch.Success) continue; // 标签行、空行、纯 LRC 行都在这里被跳过

            long rawStart = long.Parse(lineMatch.Groups[1].Value);
            long lineDur = long.Parse(lineMatch.Groups[2].Value);
            string body = lineMatch.Groups[3].Value;
            if (body.Length == 0) continue;

            var wordMatches = WordRegex.Matches(body);

            // KRC 的字时间戳有两种流派：相对行起点、或相对整首歌的绝对毫秒。
            // 手头没有足够样本覆盖全部版本，这里用「首字起点是否小于行起点」自适应判断，
            // 两种都能算对；判不出来时按绝对毫秒（与 QRC / YRC 一致）。
            bool relative = false;
            if (wordMatches.Count > 0 &&
                long.TryParse(wordMatches[0].Groups[1].Value, out long firstWordStart) &&
                firstWordStart < rawStart)
            {
                relative = true;
            }

            long lineStart = Math.Max(0, rawStart + offset);

            var words = new List<LyricWord>();
            var chars = new List<LyricChar>();
            var text = new StringBuilder();

            foreach (Match wordMatch in wordMatches)
            {
                string wordText = wordMatch.Groups[3].Value;
                if (wordText.Length == 0) continue;

                long wordStart = long.Parse(wordMatch.Groups[1].Value);
                long wordDur = long.Parse(wordMatch.Groups[2].Value);
                long absoluteStart = relative ? lineStart + wordStart : wordStart;
                if (absoluteStart < 0) absoluteStart = 0;

                words.Add(new LyricWord
                {
                    Start = TimeSpan.FromMilliseconds(absoluteStart),
                    End = TimeSpan.FromMilliseconds(absoluteStart + wordDur),
                    Text = wordText
                });
                LyricChar.SplitWord(chars, wordText, absoluteStart, wordDur);
                text.Append(wordText);
            }

            // 没有词级标签时，把标签整体去掉后当普通行处理。
            // 部分 KRC 的行正文还带 LRC 时间戳前缀（[00:05.00]），必须一并剥掉，
            // 否则会原样显示成歌词（实测会看到 "[00:05.00]纯行级歌词兜底"）。
            string lyricText = words.Count > 0
                ? text.ToString()
                : LrcTimestampPrefixRegex.Replace(WordRegex.Replace(body, string.Empty), string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(lyricText)) continue;
            if (QrcDecoder.CreditRegex.IsMatch(lyricText)) continue;

            result.Add(new LyricLine
            {
                Time = TimeSpan.FromMilliseconds(lineStart),
                End = TimeSpan.FromMilliseconds(lineStart + (lineDur > 0 ? lineDur : 4000)),
                Text = lyricText,
                Words = words,
                Chars = chars
            });
        }

        result.Sort((a, b) => a.Time.CompareTo(b.Time));
        return result;
    }

    /// <summary>KRC 头部的 [offset:N] 是整首歌词的时间平移，单位毫秒。</summary>
    private static int ReadOffset(string content)
    {
        int offset = 0;
        foreach (var rawLine in content.Split('\n'))
        {
            var m = TagRegex.Match(rawLine.Trim());
            if (!m.Success) continue;
            if (!string.Equals(m.Groups[1].Value, "offset", StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(m.Groups[2].Value.Trim(), out int value)) offset = value;
            break;
        }
        return offset;
    }

    /// <summary>
    /// 只读 KRC 文件头部的 [ti:]/[ar:] 标签，用于文件名不可靠时按标签匹配。
    /// 解不开或没有标签时返回两个空串。
    /// </summary>
    public static (string Title, string Artist) ReadTags(string file)
    {
        try
        {
            string? text = TryDecodeToLyricText(File.ReadAllBytes(file));
            if (string.IsNullOrEmpty(text)) return (string.Empty, string.Empty);

            string title = string.Empty;
            string artist = string.Empty;
            int checkedLines = 0;

            foreach (var rawLine in text.Split('\n'))
            {
                // 标签只出现在头部，看完前几十行还没命中就别再往下找了。
                if (++checkedLines > 64) break;

                var m = TagRegex.Match(rawLine.Trim());
                if (!m.Success) continue;

                string name = m.Groups[1].Value;
                string value = m.Groups[2].Value.Trim();

                if (string.Equals(name, "ti", StringComparison.OrdinalIgnoreCase) && title.Length == 0)
                    title = value;
                else if (string.Equals(name, "ar", StringComparison.OrdinalIgnoreCase) && artist.Length == 0)
                    artist = value;
            }

            return (title, artist);
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
    }

    /// <summary>解密并解析一个 .krc 文件，失败返回空列表。</summary>
    public static List<LyricLine> ParseKrcFile(string file)
    {
        try
        {
            string? text = TryDecodeToLyricText(File.ReadAllBytes(file));
            if (string.IsNullOrEmpty(text)) return new List<LyricLine>();

            var lines = ParseKrc(text);

            // 少数 KRC 只有行级 LRC 正文（没有 <> 词级标签），此时交给 LRC 解析器，
            // 否则会一行都解析不出来。
            if (lines.Count == 0)
            {
                var lrc = LyricService.ParseLrc(text);
                if (lrc.Count > 0) return lrc;
            }

            return lines;
        }
        catch
        {
            return new List<LyricLine>();
        }
    }
}

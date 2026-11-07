using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace DeskBar.Services;

/// <summary>
/// QQ 音乐本地 QRC 逐字歌词解密器。算法移植自 WaterSprite-TaskbarMediaBar
/// (soda-player/vendor/qq-lyrics)，逐字节等价。
/// 流程：XMC mask 逐字节 XOR → 校验首行 "[offset:N]" → 逐块 3DES-ECB（无 padding）→ zlib inflate → XML。
/// 注意：这里必须用 QQ 自己的 DES 位序实现（见下方 BitNum），标准 .NET TripleDES / OpenSSL
/// 解出来的结果与它不同，无法互换。
/// </summary>
internal static class QrcDecoder
{
    private static readonly byte[] Key = Encoding.ASCII.GetBytes("!@#)(*$%123ZXC!@!@#)(NHL");

    private static readonly byte[][] SBox =
    {
        new byte[]{14,4,13,1,2,15,11,8,3,10,6,12,5,9,0,7,0,15,7,4,14,2,13,1,10,6,12,11,9,5,3,8,4,1,14,8,13,6,2,11,15,12,9,7,3,10,5,0,15,12,8,2,4,9,1,7,5,11,3,14,10,0,6,13},
        new byte[]{15,1,8,14,6,11,3,4,9,7,2,13,12,0,5,10,3,13,4,7,15,2,8,15,12,0,1,10,6,9,11,5,0,14,7,11,10,4,13,1,5,8,12,6,9,3,2,15,13,8,10,1,3,15,4,2,11,6,7,12,0,5,14,9},
        new byte[]{10,0,9,14,6,3,15,5,1,13,12,7,11,4,2,8,13,7,0,9,3,4,6,10,2,8,5,14,12,11,15,1,13,6,4,9,8,15,3,0,11,1,2,12,5,10,14,7,1,10,13,0,6,9,8,7,4,15,14,3,11,5,2,12},
        new byte[]{7,13,14,3,0,6,9,10,1,2,8,5,11,12,4,15,13,8,11,5,6,15,0,3,4,7,2,12,1,10,14,9,10,6,9,0,12,11,7,13,15,1,3,14,5,2,8,4,3,15,0,6,10,10,13,8,9,4,5,11,12,7,2,14},
        new byte[]{2,12,4,1,7,10,11,6,8,5,3,15,13,0,14,9,14,11,2,12,4,7,13,1,5,0,15,10,3,9,8,6,4,2,1,11,10,13,7,8,15,9,12,5,6,3,0,14,11,8,12,7,1,14,2,13,6,15,0,9,10,4,5,3},
        new byte[]{12,1,10,15,9,2,6,8,0,13,3,4,14,7,5,11,10,15,4,2,7,12,9,5,6,1,13,14,0,11,3,8,9,14,15,5,2,8,12,3,7,0,4,10,1,13,11,6,4,3,2,12,9,5,15,10,11,14,1,7,6,0,8,13},
        new byte[]{4,11,2,14,15,0,8,13,3,12,9,7,5,10,6,1,13,0,11,7,4,9,1,10,14,3,5,12,2,15,8,6,1,4,11,13,12,3,7,14,10,15,6,8,0,5,9,2,6,11,13,8,1,4,10,7,9,5,0,15,14,2,3,12},
        new byte[]{13,2,8,4,6,15,11,1,10,9,3,14,5,0,12,7,1,15,13,8,10,3,7,4,12,5,6,11,0,14,9,2,7,11,4,1,9,12,14,2,0,6,10,13,15,3,5,8,2,1,14,7,4,10,8,13,15,12,9,0,3,5,6,11}
    };

    private static readonly int[] KeyRndShift = { 1, 1, 2, 2, 2, 2, 2, 2, 1, 2, 2, 2, 2, 2, 2, 1 };
    private static readonly int[] KeyPermC = { 56,48,40,32,24,16,8,0,57,49,41,33,25,17,9,1,58,50,42,34,26,18,10,2,59,51,43,35 };
    private static readonly int[] KeyPermD = { 62,54,46,38,30,22,14,6,61,53,45,37,29,21,13,5,60,52,44,36,28,20,12,4,27,19,11,3 };
    private static readonly int[] KeyCompression = { 13,16,10,23,0,4,2,27,14,5,20,9,22,18,11,3,25,7,15,6,26,19,12,1,40,51,30,36,46,54,29,39,50,44,32,47,43,48,38,55,33,52,45,41,49,35,28,31 };

    private static readonly byte[][] SeedMap =
    {
        new byte[]{0x4a,0xd6,0xca,0x90,0x67,0xf7,0x52},
        new byte[]{0x5e,0x95,0x23,0x9f,0x13,0x11,0x7e},
        new byte[]{0x47,0x74,0x3d,0x90,0xaa,0x3f,0x51},
        new byte[]{0xc6,0x09,0xd5,0x9f,0xfa,0x66,0xf9},
        new byte[]{0xf3,0xd6,0xa1,0x90,0xa0,0xf7,0xf0},
        new byte[]{0x1d,0x95,0xde,0x9f,0x84,0x11,0xf4},
        new byte[]{0x0e,0x74,0xbb,0x90,0xbc,0x3f,0x92},
        new byte[]{0x00,0x09,0x5b,0x9f,0x62,0x66,0xa1}
    };

    // ---- DES 位运算原语（QQ 定制位序，勿改成标准 DES） ----

    private static uint BitNum(byte[] bytes, int b, int c)
    {
        uint bit = (uint)((bytes[(b / 32) * 4 + 3 - ((b % 32) / 8)] >> (7 - (b % 8))) & 1);
        return bit << c;
    }

    private static uint BitNumIntR(uint a, int b, int c) => (((a >> (31 - b)) & 1u) << c) & 0xffu;

    private static uint BitNumIntL(uint a, int b, int c) => ((a << b) & 0x80000000u) >> c;

    private static uint SboxBit(uint a) => (a & 0x20u) | ((a & 0x1fu) >> 1) | ((a & 1u) << 4);

    private static byte[][][] CreateSchedule()
    {
        var s = new byte[3][][];
        for (int i = 0; i < 3; i++)
        {
            s[i] = new byte[16][];
            for (int j = 0; j < 16; j++) s[i][j] = new byte[6];
        }
        return s;
    }

    private static void KeySchedule(byte[] key, byte[][] schedule, bool decrypt)
    {
        uint c = 0, d = 0;
        for (int i = 0, j = 31; i < 28; i++, j--) c |= BitNum(key, KeyPermC[i], j);
        for (int i = 0, j = 31; i < 28; i++, j--) d |= BitNum(key, KeyPermD[i], j);
        for (int i = 0; i < 16; i++)
        {
            c = ((c << KeyRndShift[i]) | (c >> (28 - KeyRndShift[i]))) & 0xfffffff0u;
            d = ((d << KeyRndShift[i]) | (d >> (28 - KeyRndShift[i]))) & 0xfffffff0u;
            int toGen = decrypt ? 15 - i : i;
            Array.Clear(schedule[toGen], 0, 6);
            for (int j = 0; j < 24; j++) schedule[toGen][j / 8] |= (byte)BitNumIntR(c, KeyCompression[j], 7 - (j % 8));
            for (int j = 24; j < 48; j++) schedule[toGen][j / 8] |= (byte)BitNumIntR(d, KeyCompression[j] - 27, 7 - (j % 8));
        }
    }

    private static void TripleDesKeySetup(byte[] key, byte[][][] schedule, bool decrypt)
    {
        if (!decrypt)
        {
            KeySchedule(Sub(key, 0), schedule[0], false);
            KeySchedule(Sub(key, 8), schedule[1], true);
            KeySchedule(Sub(key, 16), schedule[2], false);
        }
        else
        {
            KeySchedule(Sub(key, 0), schedule[2], true);
            KeySchedule(Sub(key, 8), schedule[1], false);
            KeySchedule(Sub(key, 16), schedule[0], true);
        }
    }

    private static byte[] Sub(byte[] src, int offset)
    {
        var dst = new byte[8];
        Array.Copy(src, offset, dst, 0, 8);
        return dst;
    }

    private static void Ip(uint[] state, byte[] input)
    {
        state[0] = BitNum(input,57,31)|BitNum(input,49,30)|BitNum(input,41,29)|BitNum(input,33,28)|BitNum(input,25,27)|BitNum(input,17,26)|BitNum(input,9,25)|BitNum(input,1,24)|BitNum(input,59,23)|BitNum(input,51,22)|BitNum(input,43,21)|BitNum(input,35,20)|BitNum(input,27,19)|BitNum(input,19,18)|BitNum(input,11,17)|BitNum(input,3,16)|BitNum(input,61,15)|BitNum(input,53,14)|BitNum(input,45,13)|BitNum(input,37,12)|BitNum(input,29,11)|BitNum(input,21,10)|BitNum(input,13,9)|BitNum(input,5,8)|BitNum(input,63,7)|BitNum(input,55,6)|BitNum(input,47,5)|BitNum(input,39,4)|BitNum(input,31,3)|BitNum(input,23,2)|BitNum(input,15,1)|BitNum(input,7,0);
        state[1] = BitNum(input,56,31)|BitNum(input,48,30)|BitNum(input,40,29)|BitNum(input,32,28)|BitNum(input,24,27)|BitNum(input,16,26)|BitNum(input,8,25)|BitNum(input,0,24)|BitNum(input,58,23)|BitNum(input,50,22)|BitNum(input,42,21)|BitNum(input,34,20)|BitNum(input,26,19)|BitNum(input,18,18)|BitNum(input,10,17)|BitNum(input,2,16)|BitNum(input,60,15)|BitNum(input,52,14)|BitNum(input,44,13)|BitNum(input,36,12)|BitNum(input,28,11)|BitNum(input,20,10)|BitNum(input,12,9)|BitNum(input,4,8)|BitNum(input,62,7)|BitNum(input,54,6)|BitNum(input,46,5)|BitNum(input,38,4)|BitNum(input,30,3)|BitNum(input,22,2)|BitNum(input,14,1)|BitNum(input,6,0);
    }

    private static void InvIp(uint[] state, byte[] output)
    {
        output[3] = (byte)(BitNumIntR(state[1],7,7)|BitNumIntR(state[0],7,6)|BitNumIntR(state[1],15,5)|BitNumIntR(state[0],15,4)|BitNumIntR(state[1],23,3)|BitNumIntR(state[0],23,2)|BitNumIntR(state[1],31,1)|BitNumIntR(state[0],31,0));
        output[2] = (byte)(BitNumIntR(state[1],6,7)|BitNumIntR(state[0],6,6)|BitNumIntR(state[1],14,5)|BitNumIntR(state[0],14,4)|BitNumIntR(state[1],22,3)|BitNumIntR(state[0],22,2)|BitNumIntR(state[1],30,1)|BitNumIntR(state[0],30,0));
        output[1] = (byte)(BitNumIntR(state[1],5,7)|BitNumIntR(state[0],5,6)|BitNumIntR(state[1],13,5)|BitNumIntR(state[0],13,4)|BitNumIntR(state[1],21,3)|BitNumIntR(state[0],21,2)|BitNumIntR(state[1],29,1)|BitNumIntR(state[0],29,0));
        output[0] = (byte)(BitNumIntR(state[1],4,7)|BitNumIntR(state[0],4,6)|BitNumIntR(state[1],12,5)|BitNumIntR(state[0],12,4)|BitNumIntR(state[1],20,3)|BitNumIntR(state[0],20,2)|BitNumIntR(state[1],28,1)|BitNumIntR(state[0],28,0));
        output[7] = (byte)(BitNumIntR(state[1],3,7)|BitNumIntR(state[0],3,6)|BitNumIntR(state[1],11,5)|BitNumIntR(state[0],11,4)|BitNumIntR(state[1],19,3)|BitNumIntR(state[0],19,2)|BitNumIntR(state[1],27,1)|BitNumIntR(state[0],27,0));
        output[6] = (byte)(BitNumIntR(state[1],2,7)|BitNumIntR(state[0],2,6)|BitNumIntR(state[1],10,5)|BitNumIntR(state[0],10,4)|BitNumIntR(state[1],18,3)|BitNumIntR(state[0],18,2)|BitNumIntR(state[1],26,1)|BitNumIntR(state[0],26,0));
        output[5] = (byte)(BitNumIntR(state[1],1,7)|BitNumIntR(state[0],1,6)|BitNumIntR(state[1],9,5)|BitNumIntR(state[0],9,4)|BitNumIntR(state[1],17,3)|BitNumIntR(state[0],17,2)|BitNumIntR(state[1],25,1)|BitNumIntR(state[0],25,0));
        output[4] = (byte)(BitNumIntR(state[1],0,7)|BitNumIntR(state[0],0,6)|BitNumIntR(state[1],8,5)|BitNumIntR(state[0],8,4)|BitNumIntR(state[1],16,3)|BitNumIntR(state[0],16,2)|BitNumIntR(state[1],24,1)|BitNumIntR(state[0],24,0));
    }

    private static uint F(uint stateIn, byte[] key)
    {
        uint t1 = BitNumIntL(stateIn,31,0)|((stateIn & 0xf0000000u)>>1)|BitNumIntL(stateIn,4,5)|BitNumIntL(stateIn,3,6)|((stateIn & 0x0f000000u)>>3)|BitNumIntL(stateIn,8,11)|BitNumIntL(stateIn,7,12)|((stateIn & 0x00f00000u)>>5)|BitNumIntL(stateIn,12,17)|BitNumIntL(stateIn,11,18)|((stateIn & 0x000f0000u)>>7)|BitNumIntL(stateIn,16,23);
        uint t2 = BitNumIntL(stateIn,15,0)|((stateIn & 0x0000f000u)<<15)|BitNumIntL(stateIn,20,5)|BitNumIntL(stateIn,19,6)|((stateIn & 0x00000f00u)<<13)|BitNumIntL(stateIn,24,11)|BitNumIntL(stateIn,23,12)|((stateIn & 0x000000f0u)<<11)|BitNumIntL(stateIn,28,17)|BitNumIntL(stateIn,27,18)|((stateIn & 0x0000000fu)<<9)|BitNumIntL(stateIn,0,23);
        uint x0 = ((t1 >> 24) & 0xffu) ^ key[0];
        uint x1 = ((t1 >> 16) & 0xffu) ^ key[1];
        uint x2 = ((t1 >> 8) & 0xffu) ^ key[2];
        uint x3 = ((t2 >> 24) & 0xffu) ^ key[3];
        uint x4 = ((t2 >> 16) & 0xffu) ^ key[4];
        uint x5 = ((t2 >> 8) & 0xffu) ^ key[5];
        uint state = (((uint)SBox[0][SboxBit(x0 >> 2)] << 28)
            | ((uint)SBox[1][SboxBit(((x0 & 3u) << 4) | (x1 >> 4))] << 24)
            | ((uint)SBox[2][SboxBit(((x1 & 15u) << 2) | (x2 >> 6))] << 20)
            | ((uint)SBox[3][SboxBit(x2 & 63u)] << 16)
            | ((uint)SBox[4][SboxBit(x3 >> 2)] << 12)
            | ((uint)SBox[5][SboxBit(((x3 & 3u) << 4) | (x4 >> 4))] << 8)
            | ((uint)SBox[6][SboxBit(((x4 & 15u) << 2) | (x5 >> 6))] << 4)
            | (uint)SBox[7][SboxBit(x5 & 63u)]);
        state = BitNumIntL(state,15,0)|BitNumIntL(state,6,1)|BitNumIntL(state,19,2)|BitNumIntL(state,20,3)|BitNumIntL(state,28,4)|BitNumIntL(state,11,5)|BitNumIntL(state,27,6)|BitNumIntL(state,16,7)|BitNumIntL(state,0,8)|BitNumIntL(state,14,9)|BitNumIntL(state,22,10)|BitNumIntL(state,25,11)|BitNumIntL(state,4,12)|BitNumIntL(state,17,13)|BitNumIntL(state,30,14)|BitNumIntL(state,9,15)|BitNumIntL(state,1,16)|BitNumIntL(state,7,17)|BitNumIntL(state,23,18)|BitNumIntL(state,13,19)|BitNumIntL(state,31,20)|BitNumIntL(state,26,21)|BitNumIntL(state,2,22)|BitNumIntL(state,8,23)|BitNumIntL(state,18,24)|BitNumIntL(state,12,25)|BitNumIntL(state,29,26)|BitNumIntL(state,5,27)|BitNumIntL(state,21,28)|BitNumIntL(state,10,29)|BitNumIntL(state,3,30)|BitNumIntL(state,24,31);
        return state;
    }

    private static void Crypt(byte[] input, byte[] output, byte[][] key)
    {
        uint[] state = new uint[2];
        Ip(state, input);
        for (int idx = 0; idx < 15; idx++)
        {
            uint t = state[1];
            state[1] = F(state[1], key[idx]) ^ state[0];
            state[0] = t;
        }
        state[0] = F(state[1], key[15]) ^ state[0];
        InvIp(state, output);
    }

    private static void TripleDesCrypt(byte[] input, byte[] output, byte[][][] schedule)
    {
        Crypt(input, output, schedule[0]);
        Crypt(output, output, schedule[1]);
        Crypt(output, output, schedule[2]);
    }

    private static uint NextMask(ref int x, ref int y, ref int dx, ref int index)
    {
        uint mask;
        index++;
        if (x < 0)
        {
            dx = 1;
            y = (8 - y) % 8;
            mask = 0xc3;
        }
        else if (x > 6)
        {
            dx = -1;
            y = 7 - y;
            mask = 0xd8;
        }
        else
        {
            mask = SeedMap[y][x];
        }
        x += dx;
        if (index == 0x8000 || (index > 0x8000 && (index + 1) % 0x8000 == 0))
            return NextMask(ref x, ref y, ref dx, ref index);
        return mask;
    }

    private static byte[] DecodeQmcMask(byte[] bytes)
    {
        int x = -1, y = 8, dx = 1, index = -1;
        var outp = new byte[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
            outp[i] = (byte)(bytes[i] ^ NextMask(ref x, ref y, ref dx, ref index));
        return outp;
    }

    private static string Inflate(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var zs = new ZLibStream(ms, CompressionMode.Decompress);
        using var sr = new StreamReader(zs, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    private static readonly Regex HeaderRegex = new(@"^\[offset:-?\d+]$", RegexOptions.Compiled);
    private static readonly Regex LyricContentRegex = new("LyricContent=\"([^\"]*)\"", RegexOptions.Compiled);

    /// <summary>解密 .qrc 文件，返回其中的歌词正文（QRC 格式的纯文本）；失败返回 null。</summary>
    public static string? TryDecodeToLyricText(byte[] raw)
    {
        try
        {
            if (raw.Length < 16) return null;

            byte[] decoded = DecodeQmcMask(raw);
            int lineEnd = Array.IndexOf(decoded, (byte)0x0a);
            if (lineEnd <= 0) return null;

            string header = Encoding.UTF8.GetString(decoded, 0, lineEnd).Trim();
            if (!HeaderRegex.IsMatch(header)) return null;

            int len = decoded.Length - lineEnd - 1;
            int aligned = (len / 8) * 8;
            if (aligned < 8) return null;

            var decrypted = new byte[aligned];
            var schedule = CreateSchedule();
            TripleDesKeySetup(Key, schedule, decrypt: true);
            var inBlock = new byte[8];
            var outBlock = new byte[8];
            for (int off = 0; off < aligned; off += 8)
            {
                Array.Copy(decoded, lineEnd + 1 + off, inBlock, 0, 8);
                TripleDesCrypt(inBlock, outBlock, schedule);
                Array.Copy(outBlock, 0, decrypted, off, 8);
            }

            string xml = Inflate(decrypted);
            var m = LyricContentRegex.Match(xml);
            return m.Success ? m.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    }

    private static readonly Regex QrcLineRegex = new(@"^\[(\d+)\s*,\s*(\d+)]", RegexOptions.Compiled);
    private static readonly Regex QrcWordRegex = new(@"\((\d+)\s*,\s*(\d+)\)", RegexOptions.Compiled);

    /// <summary>
    /// 解析 QRC 正文。每行形如 [行起点ms,行时长ms]字(字起点ms,字时长ms)字(...)…
    /// 注意 QRC 的文本在括号前、时间在括号内，与网易云 YRC 的顺序相反。
    /// </summary>
    /// <summary>制作人员署名行。QRC 会把「作词/混音/录音：xxx」这类署名也带上时间戳，
    /// 不剔除的话歌曲开头几秒会连续闪出这些署名而不是歌词。
    /// LyricService 解析 YRC / LRC 时复用同一条规则。</summary>
    internal static readonly Regex CreditRegex = new(
        @"^.{0,6}(作词|作曲|编曲|制作人|监制|出品|发行|录音|混音|母带|吉他|贝斯|鼓手|键盘|钢琴|弦乐|和声|编程|统筹|企划|策划|主唱|乐器|OP|SP|Programming|Mixing|Recording|Producer|Composer|Lyrics|Arrangement|Vocal|Guitar|Piano|Strings|Composed|Arranged|Produced|Written|曲|词|唱)[^：:]{0,24}[:：]",
        RegexOptions.Compiled);

    public static List<LyricLine> ParseQrc(string content)
    {
        var result = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(content)) return result;

        foreach (var rawLine in content.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0) continue;

            var head = QrcLineRegex.Match(line);
            if (!head.Success) continue; // [ti:] / [ar:] / [offset:] 等元数据

            int lineStart = int.Parse(head.Groups[1].Value);
            int lineDur = int.Parse(head.Groups[2].Value);
            string body = line.Substring(head.Length);

            var words = new List<LyricWord>();
            var chars = new List<LyricChar>();
            var sb = new StringBuilder();
            int prev = 0;
            int lastEnd = lineStart + lineDur;

            foreach (Match w in QrcWordRegex.Matches(body))
            {
                string text = body.Substring(prev, w.Index - prev);
                prev = w.Index + w.Length;
                if (text.Length == 0) continue;

                int ws = int.Parse(w.Groups[1].Value);
                int wd = int.Parse(w.Groups[2].Value);
                words.Add(new LyricWord
                {
                    Start = TimeSpan.FromMilliseconds(ws),
                    End = TimeSpan.FromMilliseconds(ws + wd),
                    Text = text
                });
                LyricChar.SplitWord(chars, text, ws, wd);
                sb.Append(text);
                if (ws + wd > lastEnd) lastEnd = ws + wd;
            }

            // 行尾若还有裸文本（没有时间戳），直接追加
            if (prev < body.Length) sb.Append(body.Substring(prev));

            string plain = sb.ToString();
            if (plain.Length == 0) continue;

            // 署名行（曲：/词：/混音：...）不是歌词
            if (CreditRegex.IsMatch(plain)) continue;

            result.Add(new LyricLine
            {
                Time = TimeSpan.FromMilliseconds(lineStart),
                End = TimeSpan.FromMilliseconds(Math.Max(lastEnd, lineStart + 1)),
                Text = plain,
                Words = words,
                Chars = chars
            });
        }

        result.Sort((a, b) => a.Time.CompareTo(b.Time));
        return result;
    }
}

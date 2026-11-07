using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace DeskBar.Services;

/// <summary>
/// 封面下载的安全闸门。移植 WaterSprite `web-artwork.cjs` 的整套校验：
/// 私网地址拒绝、DNS 重绑定防护、重定向次数上限、体积上限、魔数验真实类型。
///
/// 背景：封面 URL 来自第三方接口（网易云/QQ 的搜索结果），属于不可信输入。
/// 不加校验的话，一个被污染的响应就能让 DeskBar 去请求内网地址（SSRF），
/// 或者用伪造 Content-Type 的 HTML/SVG 骗过解码器。
/// </summary>
internal static class SafeArtwork
{
    /// <summary>单张封面最大字节数。超过就放弃 —— 解码大图会把 UI 线程拖住。</summary>
    private const int MaxBytes = 1024 * 1024;

    private const int MaxRedirects = 3;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    /// <summary>只放行这些图片扩展名对应的魔数。</summary>
    public static bool TryDetectImageMime(ReadOnlySpan<byte> data, out string mime)
    {
        mime = "";

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            mime = "image/png";
            return true;
        }

        // JPEG: FF D8 FF
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            mime = "image/jpeg";
            return true;
        }

        // GIF: "GIF87a" / "GIF89a"
        if (data.Length >= 6 && data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46)
        {
            mime = "image/gif";
            return true;
        }

        // WebP: "RIFF"...."WEBP"
        if (data.Length >= 12
            && data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
            && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
        {
            mime = "image/webp";
            return true;
        }

        // ICO: 00 00 01 00
        if (data.Length >= 4 && data[0] == 0x00 && data[1] == 0x00 && data[2] == 0x01 && data[3] == 0x00)
        {
            mime = "image/x-icon";
            return true;
        }

        // BMP: "BM"
        if (data.Length >= 2 && data[0] == 0x42 && data[1] == 0x4D)
        {
            mime = "image/bmp";
            return true;
        }

        return false;
    }

    /// <summary>
    /// 下载一张封面。任何一步校验失败都返回 null，绝不抛异常。
    /// </summary>
    public static Task<byte[]?> DownloadAsync(string? url) => DownloadAsync(url, Http.Client);

    private static async Task<byte[]?> DownloadAsync(string? url, HttpClient client)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        string current = url.Trim();
        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!TryValidateUrl(current, out Uri? uri)) return null;
            if (!await HostIsPublicAsync(uri!).ConfigureAwait(false)) return null;

            using var cts = new CancellationTokenSource(RequestTimeout);
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
                return null;
            }

            using (response)
            {
                // 重定向：自己跟，每次都重新校验，防止跳到内网
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                    or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    Uri? location = response.Headers.Location;
                    if (location == null) return null;
                    current = location.IsAbsoluteUri ? location.ToString() : new Uri(uri!, location).ToString();
                    continue;
                }

                if (!response.IsSuccessStatusCode) return null;

                // 上游声明的长度过大就直接放弃，避免把大文件读进内存
                long? declared = response.Content.Headers.ContentLength;
                if (declared.HasValue && declared.Value > MaxBytes) return null;

                byte[] buffer;
                try
                {
                    buffer = await ReadCappedAsync(response.Content, MaxBytes, cts.Token).ConfigureAwait(false);
                }
                catch
                {
                    return null;
                }

                if (buffer.Length == 0 || buffer.Length > MaxBytes) return null;

                // 魔数校验：不信 Content-Type，只认真实字节
                return TryDetectImageMime(buffer, out _) ? buffer : null;
            }
        }

        return null; // 重定向太多次
    }

    // 这里不用 ReadAsByteArrayAsync + 事后检查长度：那会先完整读进内存才发现超限。
    private static async Task<byte[]> ReadCappedAsync(HttpContent content, int cap, CancellationToken token)
    {
        using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var chunk = new byte[16 * 1024];

        while (true)
        {
            int read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), token).ConfigureAwait(false);
            if (read <= 0) break;
            if (output.Length + read > cap) throw new InvalidOperationException("artwork too large");
            output.Write(chunk, 0, read);
        }

        return output.ToArray();
    }

    /// <summary>只允许 https、无凭据、默认端口、非本机域名。对应 `web-artwork.cjs` 的 allowedUrl。</summary>
    private static bool TryValidateUrl(string raw, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed)) return false;

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrEmpty(parsed.UserInfo)) return false;
        if (!parsed.IsDefaultPort) return false;

        string host = parsed.Host;
        if (string.IsNullOrEmpty(host)) return false;

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return false;
        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return false;
        if (host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)) return false;
        if (host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return false;

        // 主机直接写成 IP 的情况，先挡掉私网
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal) && !IsPublicAddress(literal)) return false;

        uri = parsed;
        return true;
    }

    /// <summary>
    /// 解析域名后再校验一次解析结果。这一步是 DNS 重绑定防护：
    /// 攻击者可以让域名先解析到公网、校验通过后改成内网地址。
    /// </summary>
    private static async Task<bool> HostIsPublicAsync(Uri uri)
    {
        try
        {
            if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal))
                return IsPublicAddress(literal);

            var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost).ConfigureAwait(false);
            if (addresses.Length == 0) return false;

            foreach (var address in addresses)
            {
                if (!IsPublicAddress(address)) return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>是否为公网单播地址。对应 `web-artwork.cjs` 的 publicAddress。</summary>
    public static bool IsPublicAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            if (b.Length != 4) return false;

            // 0.0.0.0/8
            if (b[0] == 0) return false;
            // 10.0.0.0/8
            if (b[0] == 10) return false;
            // 127.0.0.0/8
            if (b[0] == 127) return false;
            // 169.254.0.0/16（链路本地）
            if (b[0] == 169 && b[1] == 254) return false;
            // 172.16.0.0/12
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
            // 192.168.0.0/16
            if (b[0] == 192 && b[1] == 168) return false;
            // 192.0.0.0/24
            if (b[0] == 192 && b[1] == 0 && b[2] == 0) return false;
            // 100.64.0.0/10（运营商级 NAT）
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;
            // 198.18.0.0/15（基准测试保留段）
            if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return false;
            // 224.0.0.0/4 组播 + 240.0.0.0/4 保留
            if (b[0] >= 224) return false;

            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return false;

            byte[] b = address.GetAddressBytes();
            if (b.Length != 16) return false;

            // ::1 已在 IsLoopback 挡住；再挡 ::（未指定）与唯一本地地址 fc00::/7
            for (int i = 0; i < 15; i++)
            {
                if (b[i] != 0) break;
                if (i == 14 && b[15] == 0) return false;
            }
            if ((b[0] & 0xFE) == 0xFC) return false;

            return true;
        }

        return false;
    }

    /// <summary>共享的 HttpClient。单独一份，避免和歌词接口的超时/头设置互相干扰。</summary>
    internal static class Http
    {
        public static readonly HttpClient Client = Create();

        private static HttpClient Create()
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,                 // 重定向由 SafeArtwork 自己跟，便于逐跳校验
                AutomaticDecompression = DecompressionMethods.None,
                ConnectTimeout = RequestTimeout,
                MaxConnectionsPerServer = 4
            };
            var client = new HttpClient(handler) { Timeout = RequestTimeout };
            client.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            return client;
        }
    }
}

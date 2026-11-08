using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeskBar.Services;

/// <summary>
/// 版本比较与更新通道。
///
/// 设计要点：
/// ① 只读 GitHub Releases 的 latest 接口，匿名访问够用（60 次/小时），不需要内置 token。
/// ② 下载链接默认走加速前缀。GitHub 直链在国内经常连不上，加速站只是把
///    `https://github.com/...` 换成 `https://ghp.xptvhelper.link/https://github.com/...`，
///    前缀本身可被用户在设置页改成任意中转，或留空走直连。
/// ③ 检查更新是「用户点了才发请求」，不做启动轮询。本项目的内存与零磁盘写入预算是硬约束，
///    为极小概率的「有新版本」而常驻一个后台请求没有性价比。
/// </summary>
internal static class UpdateService
{
    /// <summary>仓库坐标。改仓库名只需改这一处。</summary>
    private const string RepoOwner = "buyikun";
    private const string RepoName = "DeskBar";

    /// <summary>默认加速前缀。空字符串表示直连 GitHub。</summary>
    public const string DefaultAccelPrefix = "https://ghp.xptvhelper.link/";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            // 加速站与 api.github.com 都要能连，连接超时不能拖过整体超时
            ConnectTimeout = RequestTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        var client = new HttpClient(handler) { Timeout = RequestTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DeskBar-UpdateCheck");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>一次检查的结果。</summary>
    public sealed class CheckResult
    {
        /// <summary>最新版本号，去掉前缀 v，例如 "2.1.0"。失败时为空。</summary>
        public string LatestVersion { get; init; } = "";

        /// <summary>当前运行版本号，例如 "2.0.0"。</summary>
        public string CurrentVersion { get; init; } = "";

        /// <summary>是否有更新。</summary>
        public bool HasUpdate { get; init; }

        /// <summary>Release 页面地址（给人看的）。</summary>
        public string ReleasePageUrl { get; init; } = "";

        /// <summary>安装包直链（已拼接加速前缀）。无资产时为空。</summary>
        public string AssetUrl { get; init; } = "";

        /// <summary>安装包文件名。</summary>
        public string AssetName { get; init; } = "";

        /// <summary>发行说明正文。</summary>
        public string Notes { get; init; } = "";

        /// <summary>失败原因，成功时为空。</summary>
        public string Error { get; init; } = "";
    }

    /// <summary>当前程序版本（三段式，取 Assembly 版本）。</summary>
    public static string CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// 检查最新版本。网络或解析失败时返回 Error，不抛异常——调用方只需看字段。
    /// </summary>
    /// <param name="accelPrefix">加速前缀，可为空表示直连。</param>
    public static async Task<CheckResult> CheckAsync(string accelPrefix)
    {
        var current = CurrentVersion;
        string json;
        try
        {
            using var cts = new CancellationTokenSource(RequestTimeout);
            // latest 接口自动跳过 draft 与 prerelease，正是发布时要的语义
            var apiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            json = await Http.GetStringAsync(apiUrl, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Fail(current, "请求超时，请检查网络或稍后重试。");
        }
        catch (HttpRequestException ex)
        {
            Log.Debug($"update check http failed: {ex.Message}");
            return Fail(current, "网络请求失败，请检查网络连接。");
        }
        catch (Exception ex)
        {
            Log.Warn($"update check failed: {ex.Message}");
            return Fail(current, "检查更新失败：" + ex.Message);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var latest = NormalizeVersion(tag);
            var pageUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
            var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

            // 优先挑名字里带 win-x64 的 zip；没有就用第一个 zip
            string assetName = "", assetUrl = "";
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                string firstZipUrl = "", firstZipName = "";
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                    if (url.Length == 0 || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;

                    if (firstZipName.Length == 0)
                    {
                        firstZipName = name;
                        firstZipUrl = url;
                    }
                    if (name.IndexOf("win-x64", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        assetName = name;
                        assetUrl = url;
                        break;
                    }
                }
                if (assetUrl.Length == 0)
                {
                    assetName = firstZipName;
                    assetUrl = firstZipUrl;
                }
            }

            if (latest.Length == 0)
                return Fail(current, "无法识别发布版本号。");

            return new CheckResult
            {
                LatestVersion = latest,
                CurrentVersion = current,
                HasUpdate = CompareVersions(latest, current) > 0,
                ReleasePageUrl = pageUrl,
                AssetName = assetName,
                AssetUrl = ApplyAccel(assetUrl, accelPrefix),
                Notes = notes
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"update check parse failed: {ex.Message}");
            return Fail(current, "解析发布信息失败。");
        }
    }

    private static CheckResult Fail(string current, string error) => new()
    {
        CurrentVersion = current,
        Error = error
    };

    /// <summary>
    /// 把 GitHub 直链套上加速前缀。
    /// 前缀为空、已是绝对非 GitHub 地址、或目标不是 github.com 时原样返回。
    /// </summary>
    public static string ApplyAccel(string url, string accelPrefix)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        var prefix = (accelPrefix ?? "").Trim();
        if (prefix.Length == 0) return url;

        var lower = url.ToLowerInvariant();
        // 只对 github.com 域名做替换；api.github.com 之类不经过加速站
        if (!lower.StartsWith("https://github.com/", StringComparison.Ordinal))
            return url;

        // 前缀末尾补一个 /
        if (!prefix.EndsWith("/", StringComparison.Ordinal)) prefix += "/";
        return prefix + url;
    }

    /// <summary>去掉前导 v/V 与空白，只留数字与点。</summary>
    public static string NormalizeVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
        // 只保留 数字 / 点，丢掉 -beta 之类的后缀，避免比较时误判
        var m = Regex.Match(s, @"^\d+(\.\d+)*");
        return m.Success ? m.Value : "";
    }

    /// <summary>
    /// 按段比较版本号，缺失段按 0 补齐。
    /// 返回 &gt;0 表示 a 比 b 新。
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        var pa = SplitParts(a);
        var pb = SplitParts(b);
        int len = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < len; i++)
        {
            int va = i < pa.Length ? pa[i] : 0;
            int vb = i < pb.Length ? pb[i] : 0;
            if (va != vb) return va > vb ? 1 : -1;
        }
        return 0;
    }

    private static int[] SplitParts(string v)
    {
        var text = NormalizeVersion(v);
        if (text.Length == 0) return Array.Empty<int>();
        var parts = text.Split('.');
        var result = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            int n = 0;
            try { n = int.Parse(parts[i]); } catch { n = 0; }
            result[i] = n;
        }
        return result;
    }
}

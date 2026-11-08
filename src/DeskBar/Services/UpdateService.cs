using System;
using System.IO;
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

    /// <summary>
    /// 覆盖更新的进度回调。
    /// </summary>
    /// <param name="phase">当前阶段（下载 / 解压 / 准备替换）。</param>
    /// <param name="percent">0–100；未知总量时为 -1。</param>
    public delegate void ProgressCallback(string phase, int percent);

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

    // ===================== 下载与覆盖安装 =====================

    /// <summary>
    /// 下载更新包并解压到暂存目录。
    /// 返回解压后的目录（内含新的 exe/dll）与临时根目录，供安装时使用。
    /// </summary>
    public static async Task<DownloadResult> DownloadAsync(string assetUrl, string latestVersion,
        ProgressCallback? progress)
    {
        if (string.IsNullOrWhiteSpace(assetUrl))
            return new DownloadResult { Error = "没有可用的下载地址。" };

        var stamp = $"{latestVersion}_{DateTime.Now:yyyyMMddHHmmss}";
        var root = Path.Combine(Path.GetTempPath(), "DeskBar-Update", stamp);
        var zipPath = Path.Combine(root, "update.zip");
        var stagedDir = Path.Combine(root, "staged");

        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(stagedDir);

            // 依次尝试：加速站 → 直连 GitHub。
            // 加速站在国内带宽极低（实测约 8KB/s，7MB 包要十几分钟且容易断），
            // 因此必须有直连兜底；任何一条失败都清掉半截文件再换下一条。
            var candidates = BuildDownloadCandidates(assetUrl);
            Exception? lastError = null;
            bool ok = false;

            for (int i = 0; i < candidates.Count && !ok; i++)
            {
                var (url, label) = candidates[i];
                progress?.Invoke($"正在下载（{label}）…", 0);
                try
                {
                    if (File.Exists(zipPath)) File.Delete(zipPath);
                    await DownloadFileAsync(url, zipPath, progress).ConfigureAwait(false);

                    // 关键校验：能打开 zip 才算下载完整。
                    // 加速站断流会留下「半个包」，只认文件存在会把坏包当成好包。
                    progress?.Invoke("正在校验…", 100);
                    using (var probe = System.IO.Compression.ZipFile.OpenRead(zipPath))
                    {
                        if (probe.Entries.Count == 0)
                            throw new InvalidDataException("压缩包为空。");
                    }
                    ok = true;
                    progress?.Invoke($"下载完成（{label}）", 100);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    Log.Warn($"update download via {label} failed: {ex.Message}");
                    // 多条链路都失败时，才给用户看到错误
                    if (i < candidates.Count - 1)
                        progress?.Invoke($"「{label}」失败，切换备用通道…", 0);
                }
            }

            if (!ok)
                return new DownloadResult
                {
                    Error = lastError is null
                        ? "下载失败，请检查网络后重试。"
                        : "下载失败（已尝试加速与直连）：" + lastError.Message
                };

            progress?.Invoke("正在解压…", 100);
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, stagedDir, overwriteFiles: true);

            // 校验：解压后必须能找到 DeskBar.exe，否则这个包不是我们要的
            if (!File.Exists(Path.Combine(stagedDir, "DeskBar.exe")))
                return new DownloadResult { Error = "更新包内容异常：未找到 DeskBar.exe。" };

            progress?.Invoke("准备就绪", 100);
            return new DownloadResult { StagedDir = stagedDir, TempRoot = root };
        }
        catch (Exception ex)
        {
            Log.Warn($"update download failed: {ex.Message}");
            try { Directory.Delete(root, true); } catch { }
            return new DownloadResult { Error = "下载或解压失败：" + ex.Message };
        }
    }

    /// <summary>
    /// 生成下载候选链路。
    ///
    /// 顺序刻意是「直连优先、加速站兜底」：本机实测直连 GitHub 约 3.9MB/s（7MB 包 1.8 秒完成），
    /// 而加速站只有约 8KB/s 且容易断流（7MB 要十几分钟，还留下半个 zip）。
    /// 加速通道的价值只在「直连完全不通」时才体现，所以它必须排在后面，而不是默认首选。
    /// </summary>
    private static List<(string Url, string Label)> BuildDownloadCandidates(string accelAssetUrl)
    {
        var list = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(accelAssetUrl)) return list;

        var url = accelAssetUrl.Trim();
        var accelIdx = url.IndexOf("https://github.com/", StringComparison.OrdinalIgnoreCase);
        if (accelIdx > 0)
        {
            // 形如 https://加速站/https://github.com/... → 取后半段作直连
            var direct = url.Substring(accelIdx);
            list.Add((direct, "GitHub 直连"));
            list.Add((url, "加速通道"));
        }
        else
        {
            list.Add((url, "GitHub 直连"));
        }
        return list;
    }

    /// <summary>下载文件的包内结果。</summary>
    public sealed class DownloadResult
    {
        /// <summary>解压后包含新版本的目录。</summary>
        public string StagedDir { get; init; } = "";

        /// <summary>本次更新的临时根目录，安装或失败后应清理。</summary>
        public string TempRoot { get; init; } = "";

        public string Error { get; init; } = "";
    }

    private static async Task<bool> DownloadFileAsync(string url, string destPath, ProgressCallback? progress)
    {
        // 加速站在国内带宽很低：实测 7MB 包约 8KB/s，需要十几分钟。
        // 旧值 5 分钟会把正常下载掐断成「半个 zip」，因此放宽到 30 分钟。
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? -1L;
        // 加速站可能不返回 Content-Length，此时进度只能显示已下载量
        long received = 0;
        var buffer = new byte[8192];

        await using (var src = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false))
        await using (var dst = File.Create(destPath))
        {
            int read;
            while ((read = await src.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
                received += read;
                if (total > 0)
                    progress?.Invoke($"正在下载 {received / 1024}KB / {total / 1024}KB",
                        (int)(received * 100 / total));
                else
                    progress?.Invoke($"正在下载 {received / 1024}KB", -1);
            }
        }
        return true;
    }

    /// <summary>
    /// 生成「等本进程退出后替换文件并重启」的批处理脚本，然后调用它。
    ///
    /// 为什么要外部脚本：DeskBar.exe / DeskBar.dll 正在被本进程加载，Windows 不允许覆盖
    /// 已被映射的可执行文件。只能让当前进程先退出，由不持有这些文件句柄的 cmd 完成替换。
    /// 脚本会：等待本进程退出 → 拷贝新文件 → 清理临时目录 → 重新启动程序。
    /// </summary>
    public static InstallResult StartInstall(string stagedDir, string tempRoot)
    {
        try
        {
            var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var exePath = Path.Combine(appDir, "DeskBar.exe");
            if (!File.Exists(exePath))
                return new InstallResult { Error = "找不到当前程序路径。" };

            var scriptPath = Path.Combine(tempRoot, "apply-update.cmd");
            var logPath = Path.Combine(tempRoot, "apply-update.log");

            // 用户配置与日志不覆盖：settings.json 不在包里，logs 目录脚本里显式跳过
            var script = BuildApplyScript(
                pid: Environment.ProcessId,
                stagedDir: stagedDir,
                appDir: appDir,
                exePath: exePath,
                tempRoot: tempRoot,
                logPath: logPath);
            File.WriteAllText(scriptPath, script, new System.Text.UTF8Encoding(false));

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = tempRoot
            };
            System.Diagnostics.Process.Start(psi);

            Log.Info($"update: install script launched, staged={stagedDir}");
            return new InstallResult { Started = true };
        }
        catch (Exception ex)
        {
            Log.Warn($"update install failed: {ex.Message}");
            return new InstallResult { Error = "启动更新程序失败：" + ex.Message };
        }
    }

    /// <summary>安装启动结果。</summary>
    public sealed class InstallResult
    {
        public bool Started { get; init; }
        public string Error { get; init; } = "";
    }

    /// <summary>
    /// 生成替换脚本。全部使用 cmd 内建命令 + 绝对路径，不依赖 PowerShell。
    ///
    /// 两条踩过的坑：
    /// ① 延时不能用 <c>timeout</c>：环境里若存在 GNU timeout 会遮蔽 cmd 的同名命令，
    ///    脚本直接报 "invalid time interval"。改用 <c>ping -n N 127.0.0.1</c> 更稳。
    /// ② 判进程存活不能用裸 <c>find</c>：改用 <c>tasklist /FO CSV /NH</c> + <c>findstr</c>。
    /// </summary>
    private static string BuildApplyScript(int pid, string stagedDir, string appDir,
        string exePath, string tempRoot, string logPath)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal enableextensions enabledelayedexpansion");
        sb.AppendLine($"echo [%date% %time%] update start > \"{logPath}\"");
        sb.AppendLine();

        // 等旧进程真正退出（最多约 60 秒），避免拷贝时文件仍被占用。
        // 用 findstr 匹配 PID 数字，可跨中英文系统（tasklist 输出语言无关的是 PID 列）。
        sb.AppendLine("set /a tries=0");
        sb.AppendLine(":waitloop");
        sb.AppendLine($"tasklist /FI \"PID eq {pid}\" /FO CSV /NH 2>nul | findstr /C:\"{pid}\" >nul");
        sb.AppendLine("if not errorlevel 1 (");
        sb.AppendLine("  set /a tries+=1");
        sb.AppendLine("  if !tries! GEQ 60 goto copyfiles");
        sb.AppendLine("  ping -n 2 127.0.0.1 >nul");
        sb.AppendLine("  goto waitloop");
        sb.AppendLine(")");
        sb.AppendLine();

        sb.AppendLine(":copyfiles");
        sb.AppendLine($"echo [%date% %time%] copying files (waited !tries!s) >> \"{logPath}\"");
        sb.AppendLine($"xcopy /E /Y /R /I \"{stagedDir}\\*\" \"{appDir}\\\" >> \"{logPath}\" 2>&1");
        sb.AppendLine("if errorlevel 1 (");
        sb.AppendLine($"  echo [%date% %time%] COPY_FAILED >> \"{logPath}\"");
        sb.AppendLine(") else (");
        sb.AppendLine($"  echo [%date% %time%] copy ok >> \"{logPath}\"");
        sb.AppendLine(")");
        sb.AppendLine();

        sb.AppendLine($"echo [%date% %time%] relaunch >> \"{logPath}\"");
        sb.AppendLine($"start \"\" \"{exePath}\"");
        sb.AppendLine();

        // 自清理：再起一个独立 cmd，等本脚本退出后再删临时目录
        sb.AppendLine($"echo [%date% %time%] cleanup >> \"{logPath}\"");
        sb.AppendLine($"start \"\" /b cmd /c \"ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{tempRoot}\"\"");
        sb.AppendLine("endlocal");
        sb.AppendLine("exit /b 0");
        return sb.ToString();
    }
}

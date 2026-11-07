using System.Collections.Generic;
using System.IO;

namespace DeskBar.Services;

/// <summary>
/// 极简轻量日志：支持 UI 动态开关与防膨胀自动清理机制。
/// 关闭时完全静默（0 磁盘刷盘、0 SSD 损耗）；
/// 开启时记录详细运行排查信息，并通过自动保留天数与容量上限巡检防止无限膨胀。
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static string? _logDir;
    private static string? _crashLogPath;
    private static bool _isDebugEnabled;
    private static DateTime _lastCleanupUtc = DateTime.MinValue;
    private static int _writeCount;

    private const int MaxLogRetentionDays = 3;                 // 历史日志最多保留 3 天
    private const long MaxSingleFileBytes = 3 * 1024 * 1024;   // 单个日志文件上限 3MB
    private const long MaxTotalLogBytes = 10 * 1024 * 1024;    // 日志目录总容量上限 10MB
    private const long MaxCrashLogBytes = 512 * 1024;          // crash.log 上限 512KB

    /// <summary>UI 控制的动态调试日志开关（默认 false）</summary>
    public static bool IsDebugEnabled
    {
        get => _isDebugEnabled;
        set
        {
            _isDebugEnabled = value;
            if (value)
            {
                CleanOldLogs();
            }
        }
    }

    /// <summary>日志存储目录路径</summary>
    public static string LogDirectory => _logDir ?? Path.Combine(AppContext.BaseDirectory, "logs");

    public static void Init(string logDir)
    {
        _logDir = logDir;
        _crashLogPath = Path.Combine(logDir, "crash.log");
        CleanOldLogs();
    }

    public static void Info(string msg) => Write("INFO ", msg, isError: false);
    public static void Warn(string msg) => Write("WARN ", msg, isError: false);
    public static void Debug(string msg) => Write("DEBUG", msg, isError: false);
    public static void Error(string msg) => Write("ERROR", msg, isError: true);

    private static void Write(string level, string msg, bool isError)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}";
        System.Diagnostics.Debug.WriteLine(line);

        // 如果既没开启调试日志，也不是 Error，则直接返回，0 磁盘 I/O
        if (!IsDebugEnabled && !isError) return;

        lock (Gate)
        {
            try
            {
                if (string.IsNullOrEmpty(_logDir)) return;
                if (!Directory.Exists(_logDir)) Directory.CreateDirectory(_logDir);

                // 周期性巡检清理（每 6 小时自动触发一次）
                if ((DateTime.UtcNow - _lastCleanupUtc).TotalHours >= 6)
                {
                    _lastCleanupUtc = DateTime.UtcNow;
                    CleanOldLogsInternal();
                }

                if (isError && !IsDebugEnabled)
                {
                    // 仅未开启调试时的错误记录到 crash.log
                    if (_crashLogPath is not null)
                    {
                        var fi = new FileInfo(_crashLogPath);
                        if (fi.Exists && fi.Length > MaxCrashLogBytes) File.Delete(_crashLogPath);
                        File.AppendAllText(_crashLogPath, line + Environment.NewLine);
                    }
                }
                else if (IsDebugEnabled)
                {
                    // 开启了调试日志：写入当天的 deskbar.log
                    string debugFile = Path.Combine(_logDir, $"deskbar-{DateTime.Now:yyyyMMdd}.log");
                    
                    // 每 64 次写入检测一次单文件大小，避免每次系统调用
                    if ((++_writeCount & 0x3F) == 0)
                    {
                        var fi = new FileInfo(debugFile);
                        if (fi.Exists && fi.Length > MaxSingleFileBytes)
                        {
                            File.Delete(debugFile);
                        }
                    }

                    File.AppendAllText(debugFile, line + Environment.NewLine);
                }
            }
            catch { /* 日志失败绝不影响主业务流程 */ }
        }
    }

    /// <summary>对外触发的日志清理方法（带锁防护）</summary>
    public static void CleanOldLogs()
    {
        lock (Gate)
        {
            CleanOldLogsInternal();
        }
    }

    /// <summary>内部日志防膨胀清理机制（调用方已持有 Gate 锁）</summary>
    private static void CleanOldLogsInternal()
    {
        try
        {
            if (string.IsNullOrEmpty(_logDir) || !Directory.Exists(_logDir)) return;

            var dir = new DirectoryInfo(_logDir);
            var files = dir.GetFiles("*.log");
            if (files.Length == 0) return;

            var cutoff = DateTime.Now.AddDays(-MaxLogRetentionDays);
            long totalBytes = 0;
            var surviving = new List<FileInfo>();

            foreach (var f in files)
            {
                // 1. 清理超过保留天数的历史文件（排除 crash.log，crash.log 依靠大小控制）
                if (!f.Name.Equals("crash.log", StringComparison.OrdinalIgnoreCase) && f.LastWriteTime < cutoff)
                {
                    try { f.Delete(); } catch { }
                }
                else
                {
                    surviving.Add(f);
                    totalBytes += f.Length;
                }
            }

            // 2. 如果总大小依然超过上限（MaxTotalLogBytes），按最后修改时间由旧到新清理超额文件
            if (totalBytes > MaxTotalLogBytes)
            {
                surviving.Sort((a, b) => a.LastWriteTime.CompareTo(b.LastWriteTime));
                foreach (var f in surviving)
                {
                    if (totalBytes <= MaxTotalLogBytes) break;
                    try
                    {
                        long len = f.Length;
                        f.Delete();
                        totalBytes -= len;
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    /// <summary>清空当前日志目录下的全部日志文件</summary>
    public static void ClearLogs()
    {
        lock (Gate)
        {
            try
            {
                if (!string.IsNullOrEmpty(_logDir) && Directory.Exists(_logDir))
                {
                    foreach (var f in Directory.GetFiles(_logDir, "*.log"))
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
            }
            catch { }
        }
    }
}

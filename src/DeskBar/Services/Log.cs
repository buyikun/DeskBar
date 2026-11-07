using System.IO;

namespace DeskBar.Services;

/// <summary>
/// 极简轻量日志：支持 UI 动态开关。
/// 关闭时完全静默（0 磁盘刷盘、0 SSD 损耗）；
/// 开启时记录详细运行排查信息，并限制单文件最大 5MB 自动截断滚动。
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static string? _logDir;
    private static string? _crashLogPath;

    /// <summary>UI 控制的动态调试日志开关（默认 false）</summary>
    public static bool IsDebugEnabled { get; set; } = false;

    /// <summary>日志存储目录路径</summary>
    public static string LogDirectory => _logDir ?? Path.Combine(AppContext.BaseDirectory, "logs");

    public static void Init(string logDir)
    {
        _logDir = logDir;
        _crashLogPath = Path.Combine(logDir, "crash.log");
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

                if (isError && !IsDebugEnabled)
                {
                    // 仅未开启调试时的错误记录到 crash.log
                    if (_crashLogPath is not null)
                    {
                        var fi = new FileInfo(_crashLogPath);
                        if (fi.Exists && fi.Length > 512 * 1024) File.Delete(_crashLogPath);
                        File.AppendAllText(_crashLogPath, line + Environment.NewLine);
                    }
                }
                else if (IsDebugEnabled)
                {
                    // 开启了调试日志：写入当天的 deskbar.log
                    string debugFile = Path.Combine(_logDir, $"deskbar-{DateTime.Now:yyyyMMdd}.log");
                    var fi = new FileInfo(debugFile);
                    if (fi.Exists && fi.Length > 5 * 1024 * 1024) // 超过 5MB 截断重置
                    {
                        File.Delete(debugFile);
                    }
                    File.AppendAllText(debugFile, line + Environment.NewLine);
                }
            }
            catch { /* 日志失败绝不影响主业务流程 */ }
        }
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

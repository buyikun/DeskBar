using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace DeskBar.Services;

/// <summary>
/// 从应用标识（AUMID 或 exe 名）提取程序图标。
/// 音乐胶囊在播放器不提供封面缩略图时用它兜底，避免封面区长期空白。
/// </summary>
internal static class AppIconHelper
{
    private static readonly Dictionary<string, BitmapSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static BitmapSource? GetIcon(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return null;

        lock (Cache)
        {
            if (Cache.TryGetValue(appId, out var cached)) return cached;
        }

        BitmapSource? icon = null;
        try
        {
            var path = ResolveExePath(appId);
            if (!string.IsNullOrEmpty(path)) icon = ExtractIcon(path!);
        }
        catch (Exception ex)
        {
            Log.Debug($"AppIconHelper.GetIcon failed: {ex.Message}");
        }

        lock (Cache) { Cache[appId] = icon; }
        Log.Debug($"app icon for '{appId}': {(icon is null ? "none" : "ok")}");
        return icon;
    }

    /// <summary>把 AUMID 或 "路径\app.exe" 解析为磁盘上真实存在的可执行文件路径。</summary>
    private static string? ResolveExePath(string appId)
    {
        if (File.Exists(appId)) return appId;

        var name = appId;
        var slash = name.LastIndexOf('\\');
        if (slash >= 0) name = name[(slash + 1)..];
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;   // UWP 包名暂不处理

        var bare = name[..^4];
        foreach (var p in Process.GetProcessesByName(bare))
        {
            try
            {
                var file = p.MainModule?.FileName;
                if (!string.IsNullOrEmpty(file) && File.Exists(file)) return file;
            }
            catch { }
            finally { p.Dispose(); }
        }
        return null;
    }

    private static BitmapSource? ExtractIcon(string path)
    {
        var shfi = new SHFILEINFO();
        var res = SHGetFileInfo(path, 0, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(),
            SHGFI_ICON | SHGFI_LARGEICON);
        if (res == IntPtr.Zero || shfi.hIcon == IntPtr.Zero) return null;

        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(
                shfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            DestroyIcon(shfi.hIcon);
        }
    }

    private const uint SHGFI_ICON = 0x00000100;
    private const uint SHGFI_LARGEICON = 0x00000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}

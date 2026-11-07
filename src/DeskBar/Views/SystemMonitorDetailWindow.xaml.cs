using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using DeskBar.Interop;
using DeskBar.Services;

namespace DeskBar.Views;

/// <summary>
/// 借鉴 TaskMonitor 的下钻交互设计：
/// 展现全系统核心硬件指标看板与 Top-5 资源高消耗进程排行（已聚合多实例），
/// 并提供一键打开任务管理器与资源监视器功能。
/// </summary>
public partial class SystemMonitorDetailWindow : Window
{
    private readonly AppSettings _settings;
    private bool _isClosing;

    internal SystemMonitorDetailWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        BtnClose.Click += (_, _) => CloseWithAnimation();
        BtnTaskmgr.Click += (_, _) => LaunchUtility("taskmgr.exe");
        BtnResmon.Click += (_, _) => LaunchUtility("resmon.exe");

        SourceInitialized += OnSourceInitialized;
        Deactivated += (_, _) => CloseWithAnimation();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        AcrylicWindowHelper.EnableRoundedCorners(this);
        AcrylicWindowHelper.RemoveWindowBorder(this);
        // 使用亚克力磨砂背景
        AcrylicWindowHelper.Apply(this, _settings.AcrylicTone, "monitor-detail", flat: false);
    }

    public void UpdateMetrics(SystemMetrics m)
    {
        Dispatcher.Invoke(() =>
        {
            // CPU
            DetailCpuVal.Text = $"{m.CpuPercent:0.0}%";
            DetailCpuBar.Value = m.CpuPercent;

            // 内存
            DetailMemVal.Text = $"{m.MemPercent:0}%";
            DetailMemBar.Value = m.MemPercent;
            DetailMemSub.Text = $"{m.MemUsedGb:0.0} / {m.MemTotalGb:0.0} GB";

            // 网络
            DetailNetUp.Text = FormatRate(m.NetUpKBps);
            DetailNetDown.Text = FormatRate(m.NetDownKBps);

            // GPU
            if (m.GpuPercent >= 0)
            {
                DetailGpuVal.Text = $"{m.GpuPercent:0}%";
            }
            else
            {
                DetailGpuVal.Text = "N/A";
            }

            // 磁盘
            if (m.DiskAvailable)
            {
                DetailDiskVal.Text = $"R: {m.DiskReadMBps:0.0} | W: {m.DiskWriteMBps:0.0} MB/s";
            }
            else
            {
                DetailDiskVal.Text = "采样中...";
            }

            // Top-5 进程列表
            ListProcesses.ItemsSource = m.TopProcesses;
        });
    }

    /// <summary>定位到胶囊窗口上方并展示。</summary>
    public void ShowAtCapsule(Rect capsuleRectDip)
    {
        _isClosing = false;
        RootBorder.Opacity = 0;

        // 获取主屏幕工作区
        var wa = SystemParameters.WorkArea;

        // 水平对齐：左对齐胶囊，若右侧超出屏幕则向左平移
        double left = capsuleRectDip.Left;
        if (left + Width > wa.Right)
        {
            left = wa.Right - Width - 8;
        }
        if (left < wa.Left) left = wa.Left + 8;

        // 垂直定位：默认位于胶囊上方（任务栏在底部），留 8 DIP 间距
        double top = capsuleRectDip.Top - Height - 8;
        if (top < wa.Top)
        {
            // 若上方空间不足（如顶置任务栏），放置在胶囊下方
            top = capsuleRectDip.Bottom + 8;
        }

        Left = left;
        Top = top;

        Show();
        Activate();

        var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        RootBorder.BeginAnimation(OpacityProperty, anim);
    }

    public void CloseWithAnimation()
    {
        if (_isClosing) return;
        _isClosing = true;

        var anim = new DoubleAnimation(RootBorder.Opacity, 0, TimeSpan.FromMilliseconds(120));
        anim.Completed += (_, _) =>
        {
            Hide();
            _isClosing = false;
        };
        RootBorder.BeginAnimation(OpacityProperty, anim);
    }

    private static void LaunchUtility(string fileName)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"LaunchUtility '{fileName}' failed: {ex.Message}");
        }
    }

    private static string FormatRate(double kbps)
    {
        if (kbps < 0) kbps = 0;
        if (kbps < 1000) return $"{kbps:0.00} KB/s";
        if (kbps < 1048576) return $"{kbps / 1024:0.00} MB/s";
        return $"{kbps / 1048576:0.00} GB/s";
    }
}

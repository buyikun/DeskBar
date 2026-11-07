namespace DeskBar.Models;

/// <summary>单进程或聚合进程的资源消耗度量。</summary>
public sealed class ProcessMetricItem
{
    public int Pid { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int Count { get; set; } = 1;
    public double CpuPercent { get; set; }
    public double MemoryMb { get; set; }
    public string MemoryFormatted => MemoryMb >= 1024 
        ? $"{MemoryMb / 1024:0.0} GB" 
        : $"{MemoryMb:0} MB";
}

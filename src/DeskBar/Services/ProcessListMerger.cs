using System;
using System.Collections.Generic;
using System.Linq;
using DeskBar.Models;

namespace DeskBar.Services;

/// <summary>
/// 借鉴 TaskMonitor 的 ProcessListMerger：
/// 负责对单次 NtQuerySystemInformation 抓取出的数百个原始进程条目进行同名聚合并排序，
/// 避免 Chrome / Edge 等多进程应用在排查面板上刷屏，并准确反映应用的真实总资源消耗。
/// </summary>
public static class ProcessListMerger
{
    public static List<ProcessMetricItem> MergeAndSort(
        IEnumerable<ProcessMetricItem> rawItems, 
        int topCount = 5, 
        bool sortByCpu = true)
    {
        var groups = new Dictionary<string, ProcessMetricItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in rawItems)
        {
            if (string.IsNullOrWhiteSpace(item.Name)) continue;
            // 忽略空闲进程与系统自身微小占用，以便用户聚焦排查
            if (item.Name.Equals("Idle", StringComparison.OrdinalIgnoreCase) ||
                item.Name.Equals("System Idle Process", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (groups.TryGetValue(item.Name, out var existing))
            {
                existing.Count++;
                existing.CpuPercent += item.CpuPercent;
                existing.MemoryMb += item.MemoryMb;
            }
            else
            {
                groups[item.Name] = new ProcessMetricItem
                {
                    Pid = item.Pid,
                    Name = item.Name,
                    DisplayName = item.Name,
                    Count = 1,
                    CpuPercent = item.CpuPercent,
                    MemoryMb = item.MemoryMb
                };
            }
        }

        foreach (var entry in groups.Values)
        {
            if (entry.Count > 1)
            {
                entry.DisplayName = $"{entry.Name} ×{entry.Count}";
            }
            else
            {
                entry.DisplayName = entry.Name;
            }
        }

        IEnumerable<ProcessMetricItem> sorted = sortByCpu
            ? groups.Values.OrderByDescending(p => p.CpuPercent).ThenByDescending(p => p.MemoryMb)
            : groups.Values.OrderByDescending(p => p.MemoryMb).ThenByDescending(p => p.CpuPercent);

        return sorted.Take(topCount).ToList();
    }
}

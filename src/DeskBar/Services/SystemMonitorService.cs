using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using DeskBar.Models;

namespace DeskBar.Services;

/// <summary>一次采样得到的系统指标快照。</summary>
public sealed class SystemMetrics
{
    public double CpuPercent { get; set; }          // 0-100
    public double MemPercent { get; set; }          // 0-100
    public double MemUsedGb { get; set; }
    public double MemTotalGb { get; set; }
    public double NetDownKBps { get; set; }
    public double NetUpKBps { get; set; }
    public double GpuPercent { get; set; } = -1;    // <0 表示不可用
    public bool DiskAvailable { get; set; }
    public double DiskReadMBps { get; set; }
    public double DiskWriteMBps { get; set; }

    /// <summary>当前系统 CPU 消耗 Top 列表（已做同名归并，如 Chrome ×12）。</summary>
    public List<ProcessMetricItem> TopProcesses { get; set; } = new();
}

/// <summary>
/// 系统硬件指标与进程级资源采集服务：
/// 借鉴 TaskMonitor 的单次内核遍历 (NtQuerySystemInformation) 与网卡事件缓存机制，
/// 消除每秒 GC 垃圾与多次分散查询，一次性采集系统 CPU、各进程 CPU/内存以及硬件指标。
/// </summary>
public sealed class SystemMonitorService : IDisposable
{
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    // --- 单次内核遍历缓冲区 ---
    private IntPtr _ntBuffer = IntPtr.Zero;
    private int _ntBufferSize = 1024 * 1024; // 初始 1MB (典型 24H2 系统需 600-800KB)
    private readonly Dictionary<int, ProcessTimes> _prevProcessTimes = new();
    private bool _kernelSamplePrimed;

    // --- CPU 兜底机制 ---
    private ulong _prevIdle, _prevKernel, _prevUser;
    private bool _cpuPrimedFallback;

    // --- 网络（带网卡缓存） ---
    private NetworkInterface[]? _cachedInterfaces;
    private ulong _prevNetIn, _prevNetOut;
    private long _prevNetTicks;

    // --- PDH（GPU + 磁盘共用一条查询） ---
    private IntPtr _pdhQuery = IntPtr.Zero;
    private IntPtr _pdhGpuCounter = IntPtr.Zero;
    private IntPtr _pdhDiskReadCounter = IntPtr.Zero;
    private IntPtr _pdhDiskWriteCounter = IntPtr.Zero;
    private bool _pdhTried;

    public event Action<SystemMetrics>? MetricsChanged;

    private struct ProcessTimes
    {
        public long KernelTime;
        public long UserTime;
    }

    public SystemMonitorService(int intervalMs = 1000)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(intervalMs)
        };
        _timer.Tick += (_, _) => Sample();

        InitNetworkCache();
    }

    public void Start()
    {
        if (_disposed) return;
        _timer.Start();
        Sample();   // 建立第一次基线
    }

    public void Stop() => _timer.Stop();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();

        try
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkTopologyChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkTopologyChanged;
        }
        catch { }

        if (_ntBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_ntBuffer);
            _ntBuffer = IntPtr.Zero;
        }

        ClosePdh();
    }

    private void Sample()
    {
        if (_disposed) return;
        try
        {
            var m = new SystemMetrics();

            // 1. 单次内核遍历（采集全系统 CPU 与 Top-N 进程消耗）
            bool kernelSuccess = SampleKernelProcesses(m);
            if (!kernelSuccess)
            {
                // 若未公开接口在特殊环境受限，平滑回退到 GetSystemTimes
                SampleCpuFallback(m);
            }

            // 2. 内存全局状态
            SampleMemory(m);

            // 3. 网络收发速率（基于已缓存的网卡）
            SampleNetwork(m);

            // 4. GPU 与磁盘（PDH 采样）
            if (!SamplePdh(m))
            {
                m.GpuPercent = -1;
                m.DiskAvailable = false;
            }

            MetricsChanged?.Invoke(m);
        }
        catch (Exception ex)
        {
            Log.Debug($"SystemMonitorService.Sample failed: {ex.Message}");
        }
    }

    // =========================================================================
    // 1. NtQuerySystemInformation: 单次内核遍历
    // =========================================================================

    private const int SystemProcessInformation = 5;
    private const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;

    private bool SampleKernelProcesses(SystemMetrics m)
    {
        try
        {
            if (_ntBuffer == IntPtr.Zero)
            {
                _ntBuffer = Marshal.AllocHGlobal(_ntBufferSize);
            }

            uint returnLength = 0;
            int status = NtQuerySystemInformation(SystemProcessInformation, _ntBuffer, (uint)_ntBufferSize, out returnLength);

            while ((uint)status == STATUS_INFO_LENGTH_MISMATCH && _ntBufferSize < 32 * 1024 * 1024)
            {
                int newSize = Math.Max((int)returnLength + 65536, _ntBufferSize * 2);
                Marshal.FreeHGlobal(_ntBuffer);
                _ntBufferSize = newSize;
                _ntBuffer = Marshal.AllocHGlobal(_ntBufferSize);
                status = NtQuerySystemInformation(SystemProcessInformation, _ntBuffer, (uint)_ntBufferSize, out returnLength);
            }

            if (status != 0) return false;

        var rawList = new List<ProcessMetricItem>(256);
        var currentTimes = new Dictionary<int, ProcessTimes>(256);

        long totalSystemDelta = 0;
        double idleDelta = 0;

        IntPtr curr = _ntBuffer;
        while (curr != IntPtr.Zero)
        {
            // 读取 64 位 SYSTEM_PROCESS_INFORMATION 确切字段布局:
            // 0: NextEntryOffset (ULONG)
            // 8: WorkingSetPrivateSize (LARGE_INTEGER)
            // 40: UserTime (LARGE_INTEGER)
            // 48: KernelTime (LARGE_INTEGER)
            // 56: ImageName.Length (USHORT)
            // 64: ImageName.Buffer (IntPtr)
            // 80: UniqueProcessId (IntPtr)
            // 144: WorkingSetSize (IntPtr)

            uint nextOffset = (uint)Marshal.ReadInt32(curr, 0);
            long workingSetPrivate = Marshal.ReadInt64(curr, 8);
            long userTime = Marshal.ReadInt64(curr, 40);
            long kernelTime = Marshal.ReadInt64(curr, 48);

            ushort nameLen = (ushort)Marshal.ReadInt16(curr, 56);
            IntPtr nameBuf = Marshal.ReadIntPtr(curr, 64);
            IntPtr pidPtr = Marshal.ReadIntPtr(curr, 80);
            int pid = pidPtr.ToInt32();

            IntPtr workingSetSize = Marshal.ReadIntPtr(curr, 144);

            currentTimes[pid] = new ProcessTimes { KernelTime = kernelTime, UserTime = userTime };

            string procName = $"PID {pid}";
            if (pid == 0)
            {
                procName = "System Idle Process";
            }
            else if (pid == 4)
            {
                procName = "System";
            }
            else if (nameLen > 0 && nameLen < 1024 && nameBuf != IntPtr.Zero)
            {
                try
                {
                    procName = Marshal.PtrToStringUni(nameBuf, nameLen / 2) ?? $"PID {pid}";
                }
                catch
                {
                    procName = $"PID {pid}";
                }
            }

            long delta = 0;
            if (_kernelSamplePrimed && _prevProcessTimes.TryGetValue(pid, out var prev))
            {
                long dk = kernelTime - prev.KernelTime;
                long du = userTime - prev.UserTime;
                if (dk < 0) dk = 0;
                if (du < 0) du = 0;
                delta = dk + du;
                totalSystemDelta += delta;

                if (pid == 0)
                {
                    idleDelta = delta;
                }
            }

            double memMb = (workingSetPrivate > 0 ? workingSetPrivate : workingSetSize.ToInt64()) / 1048576.0;

            rawList.Add(new ProcessMetricItem
            {
                Pid = pid,
                Name = procName,
                MemoryMb = Math.Max(0, memMb),
                CpuPercent = delta // 暂存时钟周期增量，后续归一化
            });

            if (nextOffset == 0) break;
            curr = IntPtr.Add(curr, (int)nextOffset);
        }

        // 更新历史表
        _prevProcessTimes.Clear();
        foreach (var kv in currentTimes)
        {
            _prevProcessTimes[kv.Key] = kv.Value;
        }

        if (!_kernelSamplePrimed || totalSystemDelta <= 0)
        {
            _kernelSamplePrimed = true;
            return true;
        }

        // 计算全机总 CPU 占用率 (与任务管理器保持一致: 100 - 空闲百分比)
        double busyRatio = Math.Clamp(1.0 - (idleDelta / totalSystemDelta), 0.0, 1.0);
        m.CpuPercent = busyRatio * 100.0;

        // 计算每个进程的 CPU 百分比
        foreach (var p in rawList)
        {
            p.CpuPercent = Math.Clamp(p.CpuPercent * 100.0 / totalSystemDelta, 0.0, 100.0);
        }

        // 聚合同名进程并选出 Top 5 资源消耗最高的进程
        m.TopProcesses = ProcessListMerger.MergeAndSort(rawList, topCount: 5, sortByCpu: true);

        return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"SampleKernelProcesses error: {ex.Message}");
            return false;
        }
    }

    // =========================================================================
    // 2. CPU 兜底
    // =========================================================================

    private void SampleCpuFallback(SystemMetrics m)
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return;

        ulong i = ToU64(idle), k = ToU64(kernel), u = ToU64(user);
        if (_cpuPrimedFallback)
        {
            ulong di = i - _prevIdle, dk = k - _prevKernel, du = u - _prevUser;
            ulong total = dk + du;
            if (total > 0)
            {
                double busy = total > di ? total - di : 0;
                m.CpuPercent = Math.Clamp(busy * 100.0 / total, 0, 100);
            }
        }
        _prevIdle = i; _prevKernel = k; _prevUser = u; _cpuPrimedFallback = true;
    }

    // =========================================================================
    // 3. 内存
    // =========================================================================

    private void SampleMemory(SystemMetrics m)
    {
        var st = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref st)) return;

        m.MemPercent = st.dwMemoryLoad;
        m.MemTotalGb = st.ullTotalPhys / 1073741824.0;
        m.MemUsedGb = (st.ullTotalPhys - st.ullAvailPhys) / 1073741824.0;
    }

    // =========================================================================
    // 4. 网络（网卡缓存优化）
    // =========================================================================

    private void InitNetworkCache()
    {
        RefreshNetworkInterfaces();
        try
        {
            NetworkChange.NetworkAddressChanged += OnNetworkTopologyChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkTopologyChanged;
        }
        catch (Exception ex)
        {
            Log.Debug($"NetworkChange listener failed: {ex.Message}");
        }
    }

    private void OnNetworkTopologyChanged(object? sender, EventArgs e)
    {
        RefreshNetworkInterfaces();
    }

    private void RefreshNetworkInterfaces()
    {
        try
        {
            var list = new List<NetworkInterface>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                list.Add(ni);
            }
            _cachedInterfaces = list.ToArray();
        }
        catch (Exception ex)
        {
            Log.Debug($"RefreshNetworkInterfaces failed: {ex.Message}");
        }
    }

    private void SampleNetwork(SystemMetrics m)
    {
        var interfaces = _cachedInterfaces;
        if (interfaces == null || interfaces.Length == 0)
        {
            RefreshNetworkInterfaces();
            interfaces = _cachedInterfaces;
            if (interfaces == null) return;
        }

        ulong inBytes = 0, outBytes = 0;
        try
        {
            foreach (var ni in interfaces)
            {
                var s = ni.GetIPv4Statistics();
                inBytes += (ulong)Math.Max(0, s.BytesReceived);
                outBytes += (ulong)Math.Max(0, s.BytesSent);
            }
        }
        catch
        {
            RefreshNetworkInterfaces();
            return;
        }

        long now = Environment.TickCount64;
        if (_prevNetTicks != 0)
        {
            double dt = (now - _prevNetTicks) / 1000.0;
            if (dt > 0.1)
            {
                m.NetDownKBps = Math.Max(0, (inBytes - _prevNetIn) / 1024.0 / dt);
                m.NetUpKBps = Math.Max(0, (outBytes - _prevNetOut) / 1024.0 / dt);
            }
        }
        _prevNetIn = inBytes;
        _prevNetOut = outBytes;
        _prevNetTicks = now;
    }

    // =========================================================================
    // 5. GPU + 磁盘（PDH 采样）
    // =========================================================================

    private bool SamplePdh(SystemMetrics m)
    {
        if (!_pdhTried) OpenPdh();
        if (_pdhQuery == IntPtr.Zero) return false;

        if (PdhCollectQueryData(_pdhQuery) != 0) return false;

        m.GpuPercent = ReadGpuPercent();
        m.DiskAvailable = ReadDiskRates(m);
        return true;
    }

    private double ReadGpuPercent()
    {
        if (_pdhGpuCounter == IntPtr.Zero) return -1;

        uint size = 0, count = 0;
        uint rc = PdhGetFormattedCounterArrayW(_pdhGpuCounter, PDH_FMT_DOUBLE, out size, out count, IntPtr.Zero);
        if (rc != PDH_MORE_DATA || size == 0 || count == 0) return -1;

        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            rc = PdhGetFormattedCounterArrayW(_pdhGpuCounter, PDH_FMT_DOUBLE, out size, out count, buf);
            if (rc != 0) return -1;

            var sums = new Dictionary<string, double>(StringComparer.Ordinal);
            int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();

            for (uint n = 0; n < count; n++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buf + (int)(n * (uint)itemSize));
                if (item.FmtValue.CStatus != 0) continue;

                var name = item.szName == IntPtr.Zero ? "" : Marshal.PtrToStringUni(item.szName) ?? "";
                var key = EngineKey(name);
                var v = item.FmtValue.doubleValue;
                if (v < 0) v = 0;
                sums[key] = sums.TryGetValue(key, out var acc) ? acc + v : v;
            }

            double best = 0;
            foreach (var kv in sums) if (kv.Value > best) best = kv.Value;
            return Math.Clamp(best, 0, 100);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static string EngineKey(string instance)
    {
        int luid = instance.IndexOf("luid_", StringComparison.Ordinal);
        int eng = instance.IndexOf("engtype_", StringComparison.Ordinal);
        if (luid < 0 || eng < 0) return instance;
        return instance.Substring(luid, eng - luid + 8);
    }

    private bool ReadDiskRates(SystemMetrics m)
    {
        if (_pdhDiskReadCounter == IntPtr.Zero || _pdhDiskWriteCounter == IntPtr.Zero) return false;

        if (PdhGetFormattedCounterValue(_pdhDiskReadCounter, PDH_FMT_DOUBLE, out _, out var rd) != 0) return false;
        if (PdhGetFormattedCounterValue(_pdhDiskWriteCounter, PDH_FMT_DOUBLE, out _, out var wr) != 0) return false;

        m.DiskReadMBps = Math.Max(0, rd.doubleValue) / 1048576.0;
        m.DiskWriteMBps = Math.Max(0, wr.doubleValue) / 1048576.0;
        return true;
    }

    private void OpenPdh()
    {
        _pdhTried = true;
        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _pdhQuery) != 0)
            {
                _pdhQuery = IntPtr.Zero;
                Log.Debug("PDH open failed; GPU/disk metrics disabled");
                return;
            }

            uint rc = PdhAddEnglishCounterW(_pdhQuery, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _pdhGpuCounter);
            if (rc != 0)
            {
                _pdhGpuCounter = IntPtr.Zero;
                Log.Debug($"PDH GPU counter unavailable (0x{rc:X8})");
            }

            rc = PdhAddEnglishCounterW(_pdhQuery, @"\PhysicalDisk(_Total)\Disk Read Bytes/sec", IntPtr.Zero, out _pdhDiskReadCounter);
            if (rc != 0) _pdhDiskReadCounter = IntPtr.Zero;

            rc = PdhAddEnglishCounterW(_pdhQuery, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec", IntPtr.Zero, out _pdhDiskWriteCounter);
            if (rc != 0) _pdhDiskWriteCounter = IntPtr.Zero;

            PdhCollectQueryData(_pdhQuery);
            Log.Info($"PDH ready: gpu={_pdhGpuCounter != IntPtr.Zero} diskRead={_pdhDiskReadCounter != IntPtr.Zero}");
        }
        catch (Exception ex)
        {
            Log.Debug($"OpenPdh failed: {ex.Message}");
            _pdhQuery = IntPtr.Zero;
        }
    }

    private void ClosePdh()
    {
        try { if (_pdhQuery != IntPtr.Zero) PdhCloseQuery(_pdhQuery); } catch { }
        _pdhQuery = IntPtr.Zero;
        _pdhGpuCounter = IntPtr.Zero;
        _pdhDiskReadCounter = IntPtr.Zero;
        _pdhDiskWriteCounter = IntPtr.Zero;
    }

    private static ulong ToU64(FILETIME ft) => ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

    // =========================================================================
    // Win32 / Native P/Invoke
    // =========================================================================

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, uint size, out uint needed);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_MORE_DATA = 0x800007D2;

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE
    {
        public uint CStatus;
        public double doubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public IntPtr szName;
        public PDH_FMT_COUNTERVALUE FmtValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? szDataSource, IntPtr dwUserData, out IntPtr phQuery);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr hQuery);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr hCounter, uint dwFormat, out uint lpdwType, out PDH_FMT_COUNTERVALUE pValue);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr hCounter, uint dwFormat, out uint lpdwSize, out uint lpdwItemCount, IntPtr ItemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr hQuery);
}

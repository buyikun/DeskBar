using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace DeskBar.Services;

internal sealed class SystemMetrics
{
    public double CpuPercent { get; set; }
    public double MemPercent { get; set; }
    public double MemUsedGb { get; set; }
    public double MemTotalGb { get; set; }
    public double NetDownKBps { get; set; }
    public double NetUpKBps { get; set; }
    public double GpuPercent { get; set; } = -1.0;
    public bool DiskAvailable { get; set; }
    public double DiskReadMBps { get; set; }
    public double DiskWriteMBps { get; set; }
}

internal sealed class SystemMonitorService : IDisposable
{
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

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

    private struct PDH_FMT_COUNTERVALUE
    {
        public uint CStatus;
        public double doubleValue;
    }

    private struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public nint szName;
        public PDH_FMT_COUNTERVALUE FmtValue;
    }

    private readonly DispatcherTimer _timer;
    private bool _disposed;
    private ulong _prevIdle;
    private ulong _prevKernel;
    private ulong _prevUser;
    private bool _cpuPrimed;
    private ulong _prevNetIn;
    private ulong _prevNetOut;
    private long _prevNetTicks;
    private nint _pdhQuery = IntPtr.Zero;
    private nint _pdhGpuCounter = IntPtr.Zero;
    private nint _pdhDiskReadCounter = IntPtr.Zero;
    private nint _pdhDiskWriteCounter = IntPtr.Zero;
    private bool _pdhTried;

    private const uint PDH_FMT_DOUBLE = 512u;
    private const uint PDH_MORE_DATA = 2147485650u;

    public event Action<SystemMetrics>? MetricsChanged;

    public SystemMonitorService(int intervalMs = 1000)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(intervalMs)
        };
        _timer.Tick += delegate
        {
            Sample();
        };
    }

    public void Start()
    {
        if (!_disposed)
        {
            _timer.Start();
            Sample();
        }
    }

    public void Stop()
    {
        _timer.Stop();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _timer.Stop();
            ClosePdh();
        }
    }

    private void Sample()
    {
        if (_disposed)
        {
            return;
        }
        try
        {
            SystemMetrics systemMetrics = new SystemMetrics();
            SampleCpu(systemMetrics);
            SampleMemory(systemMetrics);
            SampleNetwork(systemMetrics);
            if (!SamplePdh(systemMetrics))
            {
                systemMetrics.GpuPercent = -1.0;
                systemMetrics.DiskAvailable = false;
            }
            this.MetricsChanged?.Invoke(systemMetrics);
        }
        catch (Exception ex)
        {
            Log.Debug("SystemMonitorService.Sample failed: " + ex.Message);
        }
    }

    private void SampleCpu(SystemMetrics m)
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            return;
        }
        ulong num = ToU64(idleTime);
        ulong num2 = ToU64(kernelTime);
        ulong num3 = ToU64(userTime);
        if (_cpuPrimed)
        {
            ulong num4 = num - _prevIdle;
            ulong num5 = num2 - _prevKernel;
            ulong num6 = num3 - _prevUser;
            ulong num7 = num5 + num6;
            if (num7 != 0)
            {
                double num8 = ((num7 > num4) ? (num7 - num4) : 0);
                m.CpuPercent = Math.Clamp(num8 * 100.0 / (double)num7, 0.0, 100.0);
            }
        }
        _prevIdle = num;
        _prevKernel = num2;
        _prevUser = num3;
        _cpuPrimed = true;
    }

    private void SampleMemory(SystemMetrics m)
    {
        MEMORYSTATUSEX mEMORYSTATUSEX = default(MEMORYSTATUSEX);
        mEMORYSTATUSEX.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        MEMORYSTATUSEX lpBuffer = mEMORYSTATUSEX;
        if (GlobalMemoryStatusEx(ref lpBuffer))
        {
            m.MemPercent = lpBuffer.dwMemoryLoad;
            m.MemTotalGb = (double)lpBuffer.ullTotalPhys / 1073741824.0;
            m.MemUsedGb = (double)(lpBuffer.ullTotalPhys - lpBuffer.ullAvailPhys) / 1073741824.0;
        }
    }

    private void SampleNetwork(SystemMetrics m)
    {
        ulong num = 0uL;
        ulong num2 = 0uL;
        try
        {
            NetworkInterface[] allNetworkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (NetworkInterface networkInterface in allNetworkInterfaces)
            {
                if (networkInterface.OperationalStatus == OperationalStatus.Up && networkInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                {
                    IPv4InterfaceStatistics iPv4Statistics = networkInterface.GetIPv4Statistics();
                    num += (ulong)Math.Max(0L, iPv4Statistics.BytesReceived);
                    num2 += (ulong)Math.Max(0L, iPv4Statistics.BytesSent);
                }
            }
        }
        catch
        {
            return;
        }
        long tickCount = Environment.TickCount64;
        if (_prevNetTicks != 0L)
        {
            double num3 = (double)(tickCount - _prevNetTicks) / 1000.0;
            if (num3 > 0.1)
            {
                m.NetDownKBps = Math.Max(0.0, (double)(num - _prevNetIn) / 1024.0 / num3);
                m.NetUpKBps = Math.Max(0.0, (double)(num2 - _prevNetOut) / 1024.0 / num3);
            }
        }
        _prevNetIn = num;
        _prevNetOut = num2;
        _prevNetTicks = tickCount;
    }

    private bool SamplePdh(SystemMetrics m)
    {
        if (!_pdhTried)
        {
            OpenPdh();
        }
        if (_pdhQuery == IntPtr.Zero)
        {
            return false;
        }
        if (PdhCollectQueryData(_pdhQuery) != 0)
        {
            return false;
        }
        m.GpuPercent = ReadGpuPercent();
        m.DiskAvailable = ReadDiskRates(m);
        return true;
    }

    private double ReadGpuPercent()
    {
        if (_pdhGpuCounter == IntPtr.Zero)
        {
            return -1.0;
        }
        uint lpdwSize = 0u;
        uint lpdwItemCount = 0u;
        if (PdhGetFormattedCounterArrayW(_pdhGpuCounter, 512u, out lpdwSize, out lpdwItemCount, IntPtr.Zero) != 2147485650u || lpdwSize == 0 || lpdwItemCount == 0)
        {
            return -1.0;
        }
        nint num = Marshal.AllocHGlobal((int)lpdwSize);
        try
        {
            if (PdhGetFormattedCounterArrayW(_pdhGpuCounter, 512u, out lpdwSize, out lpdwItemCount, num) != 0)
            {
                return -1.0;
            }
            Dictionary<string, double> dictionary = new Dictionary<string, double>(StringComparer.Ordinal);
            int num2 = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
            for (uint num3 = 0u; num3 < lpdwItemCount; num3++)
            {
                PDH_FMT_COUNTERVALUE_ITEM pDH_FMT_COUNTERVALUE_ITEM = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(num + (int)num3 * num2);
                if (pDH_FMT_COUNTERVALUE_ITEM.FmtValue.CStatus == 0)
                {
                    string key = EngineKey((pDH_FMT_COUNTERVALUE_ITEM.szName == IntPtr.Zero) ? "" : (Marshal.PtrToStringUni(pDH_FMT_COUNTERVALUE_ITEM.szName) ?? ""));
                    double num4 = pDH_FMT_COUNTERVALUE_ITEM.FmtValue.doubleValue;
                    if (num4 < 0.0)
                    {
                        num4 = 0.0;
                    }
                    dictionary[key] = (dictionary.TryGetValue(key, out var value) ? (value + num4) : num4);
                }
            }
            double num5 = 0.0;
            foreach (KeyValuePair<string, double> item in dictionary)
            {
                if (item.Value > num5)
                {
                    num5 = item.Value;
                }
            }
            return Math.Clamp(num5, 0.0, 100.0);
        }
        finally
        {
            Marshal.FreeHGlobal(num);
        }
    }

    private static string EngineKey(string instance)
    {
        int num = instance.IndexOf("luid_", StringComparison.Ordinal);
        int num2 = instance.IndexOf("engtype_", StringComparison.Ordinal);
        if (num < 0 || num2 < 0)
        {
            return instance;
        }
        return instance.Substring(num, num2 - num + 8);
    }

    private bool ReadDiskRates(SystemMetrics m)
    {
        if (_pdhDiskReadCounter == IntPtr.Zero || _pdhDiskWriteCounter == IntPtr.Zero)
        {
            return false;
        }
        if (PdhGetFormattedCounterValue(_pdhDiskReadCounter, 512u, out var lpdwType, out var pValue) != 0)
        {
            return false;
        }
        if (PdhGetFormattedCounterValue(_pdhDiskWriteCounter, 512u, out lpdwType, out var pValue2) != 0)
        {
            return false;
        }
        m.DiskReadMBps = Math.Max(0.0, pValue.doubleValue) / 1048576.0;
        m.DiskWriteMBps = Math.Max(0.0, pValue2.doubleValue) / 1048576.0;
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
            uint num = PdhAddEnglishCounterW(_pdhQuery, "\\GPU Engine(*)\\Utilization Percentage", IntPtr.Zero, out _pdhGpuCounter);
            if (num != 0)
            {
                _pdhGpuCounter = IntPtr.Zero;
                Log.Debug($"PDH GPU counter unavailable (0x{num:X8})");
            }
            if (PdhAddEnglishCounterW(_pdhQuery, "\\PhysicalDisk(_Total)\\Disk Read Bytes/sec", IntPtr.Zero, out _pdhDiskReadCounter) != 0)
            {
                _pdhDiskReadCounter = IntPtr.Zero;
            }
            if (PdhAddEnglishCounterW(_pdhQuery, "\\PhysicalDisk(_Total)\\Disk Write Bytes/sec", IntPtr.Zero, out _pdhDiskWriteCounter) != 0)
            {
                _pdhDiskWriteCounter = IntPtr.Zero;
            }
            PdhCollectQueryData(_pdhQuery);
            Log.Info($"PDH ready: gpu={_pdhGpuCounter != IntPtr.Zero} diskRead={_pdhDiskReadCounter != IntPtr.Zero}");
        }
        catch (Exception ex)
        {
            Log.Debug("OpenPdh failed: " + ex.Message);
            _pdhQuery = IntPtr.Zero;
        }
    }

    private void ClosePdh()
    {
        try
        {
            if (_pdhQuery != IntPtr.Zero)
            {
                PdhCloseQuery(_pdhQuery);
            }
        }
        catch
        {
        }
        _pdhQuery = IntPtr.Zero;
        _pdhGpuCounter = IntPtr.Zero;
        _pdhDiskReadCounter = IntPtr.Zero;
        _pdhDiskWriteCounter = IntPtr.Zero;
    }

    private static ulong ToU64(FILETIME ft)
    {
        return ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? szDataSource, nint dwUserData, out nint phQuery);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(nint hQuery, string szFullCounterPath, nint dwUserData, out nint phCounter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(nint hQuery);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(nint hCounter, uint dwFormat, out uint lpdwType, out PDH_FMT_COUNTERVALUE pValue);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterArrayW(nint hCounter, uint dwFormat, out uint lpdwSize, out uint lpdwItemCount, nint ItemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(nint hQuery);
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace DeskBar.Services;

/// <summary>Cloud Alpha Wireless 接收器的状态查询；仅允许连接、电量和充电状态三个只读命令。</summary>
internal static class HyperXHidBatteryReader
{
    private const ushort VendorId = 0x03F0;
    private const ushort ProductId = 0x098D;
    private const string DeviceName = "HyperX Cloud Alpha Wireless";
    private const int MaxReportLength = 4096;
    private const int QueryTimeoutMs = 1000;

    internal static async Task<IReadOnlyList<WirelessBatteryDevice>> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // SetupAPI 枚举会同步访问驱动，调用方即便在 UI 线程进入也不会在这里执行枚举。
        var collections = await Task.Run(() => EnumerateCollections(cancellationToken), cancellationToken).ConfigureAwait(false);
        var results = new List<WirelessBatteryDevice>();
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var collection in collections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (found.Contains(collection.Identity)) continue;
            try
            {
                using var handle = CreateFileW(collection.Path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                if (handle.IsInvalid) continue;
                using var stream = new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: true);
                var connection = await QueryAsync(stream, collection, 0x03, cancellationToken).ConfigureAwait(false);
                if (connection is null || connection.Value == 1) continue;

                // 协议还提供 0C 充电标志，当前 Device 模型没有充电字段，不能伪装成连接方式。
                // 只查询实际要显示的百分比，避免增加一个无用的 I/O 往返。
                var percent = await QueryAsync(stream, collection, 0x0B, cancellationToken).ConfigureAwait(false);
                if (percent is null || percent.Value > 100) continue;
                results.Add(new WirelessBatteryDevice(collection.Identity, DeviceName, percent.Value, "2.4 GHz 接收器", false, "headset"));
                found.Add(collection.Identity);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.ComponentModel.Win32Exception) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        return results.AsReadOnly();
    }

    private static async Task<byte?> QueryAsync(FileStream stream, HidCollection collection, byte command, CancellationToken cancellationToken)
    {
        // 固定白名单：以后不能把本读取器变为任意 HID 命令通道。
        if (command is not (0x03 or 0x0C or 0x0B)) throw new ArgumentOutOfRangeException(nameof(command));
        var request = new byte[collection.OutputLength];
        request[0] = 0x21;
        request[1] = 0xBB;
        request[2] = command;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(QueryTimeoutMs);
        try
        {
            await stream.WriteAsync(request.AsMemory(), deadline.Token).ConfigureAwait(false);
            var response = new byte[collection.InputLength];
            while (true)
            {
                int received = await stream.ReadAsync(response.AsMemory(), deadline.Token).ConfigureAwait(false);
                if (received == 0) return null;
                // Windows 会为无编号报告保留前导零；与参考 HID 层的 hidapi 语义一致。
                int start = response[0] == 0 ? 1 : 0;
                if (received - start >= 4 && response[start] == 0x21 && response[start + 2] == command)
                    return response[start + 3];
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
    }

    private static List<HidCollection> EnumerateCollections(CancellationToken cancellationToken)
    {
        var result = new List<HidCollection>();
        HidD_GetHidGuid(out var hidClass);
        IntPtr devices = SetupDiGetClassDevsW(ref hidClass, null, IntPtr.Zero, 0x12);
        if (devices == new IntPtr(-1)) return result;
        try
        {
            for (uint index = 0; ; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = new DeviceInterfaceData { Size = Marshal.SizeOf<DeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(devices, IntPtr.Zero, ref hidClass, index, ref item))
                {
                    if (Marshal.GetLastWin32Error() == 259) break;
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
                SetupDiGetDeviceInterfaceDetailW(devices, ref item, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
                if (required < 6 || required > 32768) continue;
                IntPtr detail = Marshal.AllocHGlobal(checked((int)required));
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W: cbSize 为 x64=8/x86=6，路径始终从偏移4开始。
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(devices, ref item, detail, required, out _, IntPtr.Zero)) continue;
                    string? path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    using var handle = CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (handle.IsInvalid) continue;
                    var attributes = new HidAttributes { Size = Marshal.SizeOf<HidAttributes>() };
                    if (!HidD_GetAttributes(handle, ref attributes) || attributes.Vendor != VendorId || attributes.Product != ProductId) continue;
                    if (!HidD_GetPreparsedData(handle, out var preparsed)) continue;
                    try
                    {
                        if (HidP_GetCaps(preparsed, out var caps) != 0x00110000 || caps.UsagePage < 0xFF00 ||
                            caps.OutputReportLength < 3 || caps.InputReportLength < 4 ||
                            caps.OutputReportLength > MaxReportLength || caps.InputReportLength > MaxReportLength) continue;
                        // Reference accepts all vendor collections, including this host's MI_03 / FF43:0202.
                        // Use the full collection path: VID/PID alone aliases multiple receivers/collections.
                        result.Add(new HidCollection(path, $"hyperx:{VendorId:X4}:{ProductId:X4}:{path.ToUpperInvariant()}", caps.InputReportLength, caps.OutputReportLength));
                    }
                    finally { HidD_FreePreparsedData(preparsed); }
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devices); }
        return result;
    }

    private sealed record HidCollection(string Path, string Identity, int InputLength, int OutputLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        internal int Size;
        internal Guid InterfaceClass;
        internal uint Flags;
        internal UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidAttributes
    {
        internal int Size;
        internal ushort Vendor;
        internal ushort Product;
        internal ushort Version;
    }

    // HIDP_CAPS 为34个USHORT（68字节），保留区17项，后面是10项能力计数。
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct HidCaps
    {
        internal ushort Usage;
        internal ushort UsagePage;
        internal ushort InputReportLength;
        internal ushort OutputReportLength;
        internal ushort FeatureReportLength;
        internal fixed ushort Reserved[17];
        internal ushort LinkCollectionNodes;
        internal ushort InputButtons;
        internal ushort InputValues;
        internal ushort InputDataIndices;
        internal ushort OutputButtons;
        internal ushort OutputValues;
        internal ushort OutputDataIndices;
        internal ushort FeatureButtons;
        internal ushort FeatureValues;
        internal ushort FeatureDataIndices;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetAttributes(SafeFileHandle device, ref HidAttributes attributes);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsed);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsed);
    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsed, out HidCaps caps);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid deviceClass, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr devices, IntPtr device, ref Guid deviceClass, uint index, ref DeviceInterfaceData item);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr devices, ref DeviceInterfaceData item, IntPtr detail, uint detailSize, out uint requiredSize, IntPtr deviceInfo);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
}

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace DeskBar.Services;

/// <summary>Reads battery status from the known AULA BY receiver only.</summary>
internal static class AulaReceiverBatteryReader
{
    private const ushort VendorId = 0x3554;
    private const ushort ProductId = 0xFA09;
    private const ushort UsagePageFf02 = 0xFF02;
    private const ushort UsagePageFf04 = 0xFF04;
    private const ushort ReceiverUsage = 0x0002;
    private const int ReportLength = 20;
    private const int ModelDeadlineMs = 1300;
    private const int BatteryDeadlineMs = 4000;
    private const int ResendIntervalMs = 1300;

    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint ErrorIoPending = 997;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 0x00000102;
    private const uint Infinite = 0xFFFFFFFF;
    private const int HidpStatusSuccess = 0x00110000;

    private static readonly Guid DeviceContainerIdKeyFmtid = new("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C");
    private static Guid HidClassGuid = GetHidClassGuid();

    private static readonly (byte[] Code, string Name)[] KnownModels =
    {
        (new byte[] { 0x03, 0x00, 0x00, 0x00, 0x02, 0x1C }, "MCHOSE G87"),
        (new byte[] { 0x03, 0x00, 0x00, 0x00, 0x00, 0x77 }, "MCHOSE K99"),
        (new byte[] { 0x03, 0x00, 0x00, 0x00, 0x01, 0xDF }, "MCHOSE K99 V2"),
        (new byte[] { 0x03, 0x00, 0x00, 0x00, 0x01, 0x32 }, "MCHOSE G98"),
        (new byte[] { 0x03, 0x00, 0x00, 0x00, 0x00, 0xD6 }, "MCHOSE X75"),
        (new byte[] { 0x0A, 0x00, 0x00, 0x00, 0x00, 0x03 }, "MCHOSE G98 Pro"),
        (new byte[] { 0x03, 0x00, 0x00, 0x00, 0x01, 0xC6 }, "MCHOSE X75 V2"),
        (new byte[] { 0x03, 0x00, 0x00, 0x00, 0x01, 0x15 }, "MCHOSE G75 Pro"),
        (new byte[] { 0x0A, 0x00, 0x00, 0x00, 0x00, 0x18 }, "MCHOSE G98 V2"),
        (new byte[] { 0x0A, 0x00, 0x00, 0x00, 0x00, 0x2B }, "MCHOSE UT98"),
        (new byte[] { 0x0A, 0x00, 0x00, 0x00, 0x00, 0x01 }, "MCHOSE Z75"),
        (new byte[] { 0x0A, 0x00, 0x00, 0x00, 0x00, 0x09 }, "MCHOSE Z75S"),
        (new byte[] { 0x03, 0x00, 0x00, 0x00, 0x01, 0xC7 }, "MCHOSE KX75"),
        (new byte[] { 0x03, 0x00, 0x00, 0x00, 0x01, 0x74 }, "MCHOSE GX87"),
        (new byte[] { 0x03, 0x00, 0x00, 0x00, 0x01, 0xED }, "MCHOSE GX87 V2"),
        (new byte[] { 0x03, 0x00, 0x00, 0x00, 0x01, 0x35 }, "MCHOSE K87"),
        (new byte[] { 0x03, 0x00, 0x00, 0x00, 0x03, 0x35 }, "MCHOSE G75 V2"),
        (new byte[] { 0x0A, 0x00, 0x00, 0x00, 0x00, 0x34 }, "MCHOSE G87 V2")
    };

    internal static IReadOnlyList<WirelessBatteryDevice> Read(CancellationToken token)
    {
        var found = new List<WirelessBatteryDevice>();
        IntPtr set = SetupDiGetClassDevsW(ref HidClassGuid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == new IntPtr(-1)) return found;

        try
        {
            for (uint index = 0; ; index++)
            {
                token.ThrowIfCancellationRequested();
                var iface = new SpDeviceInterfaceData { CbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref HidClassGuid, index, ref iface))
                {
                    if (Marshal.GetLastWin32Error() == ErrorNoMoreItems) break;
                    continue;
                }

                SafeFileHandle? handle = null;
                try
                {
                    if (!TryGetInterface(set, ref iface, out var path, out var deviceInfo)) continue;
                    handle = CreateFileW(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
                        IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
                    if (handle.IsInvalid) continue;

                    var attributes = new HidAttributes { Size = (uint)Marshal.SizeOf<HidAttributes>() };
                    if (!HidD_GetAttributes(handle, ref attributes) || attributes.VendorId != VendorId || attributes.ProductId != ProductId)
                        continue;
                    if (!HidD_GetPreparsedData(handle, out var preparsed)) continue;
                    HidCaps caps;
                    int capsStatus;
                    try { capsStatus = HidP_GetCaps(preparsed, out caps); }
                    finally { HidD_FreePreparsedData(preparsed); }
                    if (capsStatus != HidpStatusSuccess ||
                        (caps.UsagePage != UsagePageFf02 && caps.UsagePage != UsagePageFf04) ||
                        caps.Usage != ReceiverUsage || caps.InputReportByteLength < ReportLength ||
                        caps.OutputReportByteLength < ReportLength)
                        continue;

                    token.ThrowIfCancellationRequested();
                    var containerId = TryGetContainerId(set, ref deviceInfo);
                    string stableId = containerId is Guid id && id != Guid.Empty
                        ? $"aula:{id:B}"
                        : $"aula:{path}";
                    string? model = TryReadModel(handle, caps, token);
                    if (!TryReadBattery(handle, caps, token, out int percent, out bool charging)) continue;

                    string name = model ?? "3554 接收器键盘";
                    found.Add(new WirelessBatteryDevice(stableId, name, percent,
                        charging ? "2.4 GHz 接收器 · 充电" : "2.4 GHz 接收器", false, "keyboard"));
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch { /* Ignore only this exact receiver collection when it disappears or rejects a read. */ }
                finally { handle?.Dispose(); }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return found.AsReadOnly();
    }

    private static string? TryReadModel(SafeFileHandle handle, HidCaps caps, CancellationToken token)
    {
        if (!TryExchange(handle, caps, 0x05, ModelDeadlineMs, token, out var report)) return null;
        for (int offset = 5; offset >= 4; offset--)
        {
            foreach (var model in KnownModels)
            {
                bool match = true;
                for (int i = 0; i < model.Code.Length; i++)
                    if (report[offset + i] != model.Code[i]) { match = false; break; }
                if (match) return model.Name;
            }
        }
        return null;
    }

    private static bool TryReadBattery(SafeFileHandle handle, HidCaps caps, CancellationToken token, out int percent, out bool charging)
    {
        percent = -1;
        charging = false;
        if (!TryExchange(handle, caps, 0x4A, BatteryDeadlineMs, token, out var report)) return false;
        int level = report[5];
        if (level is < 1 or > 100) return false;
        percent = level;
        charging = (report[6] >> 4) == 1;
        return true;
    }

    private static bool TryExchange(SafeFileHandle handle, HidCaps caps, byte command, int deadlineMs,
        CancellationToken token, out byte[] response)
    {
        response = Array.Empty<byte>();
        HidD_FlushQueue(handle);
        long start = Environment.TickCount64;
        long deadline = start + deadlineMs;
        int sent = 0;
        while (!token.IsCancellationRequested)
        {
            long now = Environment.TickCount64;
            if (now >= deadline) return false;
            if (now >= start + (long)sent * ResendIntervalMs)
            {
                var request = new byte[caps.OutputReportByteLength];
                request[0] = 0x13;
                request[1] = command;
                request[2] = (byte)(sent & 1);
                request[19] = Sum(request, 19);
                if (!WriteReport(handle, request, token)) return false;
                sent++;
            }

            now = Environment.TickCount64;
            int remaining = (int)Math.Min(deadline - now, start + (long)sent * ResendIntervalMs - now);
            if (remaining < 1) remaining = (int)Math.Min(1, deadline - now);
            if (!ReadReport(handle, caps.InputReportByteLength, remaining, token, out var data, out int bytesRead))
                continue;
            if (bytesRead < ReportLength) continue;
            if (data[0] == 0 && bytesRead > ReportLength)
            {
                Buffer.BlockCopy(data, 1, data, 0, bytesRead - 1);
                bytesRead--;
            }
            if (data[0] != 0x13 || ((data[1] & 0x7F) != command)) continue;
            bool echo = true;
            for (int i = 4; i <= 18; i++) if (data[i] != 0) { echo = false; break; }
            if (echo) continue;
            bool checksum = data[19] == Sum(data, 19);
            bool zeroTrailerSinglePacket = data[19] == 0 && data[2] == 1 && data[3] == 0;
            if (!checksum && !zeroTrailerSinglePacket) continue;
            response = data;
            return true;
        }
        token.ThrowIfCancellationRequested();
        return false;
    }

    private static bool WriteReport(SafeFileHandle handle, byte[] report, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var evt = CreateEventW(IntPtr.Zero, true, false, null);
        if (evt.IsInvalid) return false;
        IntPtr buffer = Marshal.AllocHGlobal(report.Length);
        try
        {
            Marshal.Copy(report, 0, buffer, report.Length);
            var overlapped = new NativeOverlapped { EventHandle = evt.DangerousGetHandle() };
            bool ok = WriteFile(handle, buffer, (uint)report.Length, IntPtr.Zero, ref overlapped);
            if (!ok && Marshal.GetLastWin32Error() != ErrorIoPending) return false;
            uint wait = WaitForSingleObject(evt, 250);
            if (wait != WaitObject0)
            {
                CancelIoEx(handle, ref overlapped);
                WaitForSingleObject(evt, Infinite);
                token.ThrowIfCancellationRequested();
                return false;
            }
            return GetOverlappedResult(handle, ref overlapped, out uint written, false) && written == report.Length;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool ReadReport(SafeFileHandle handle, int reportLength, int timeoutMs, CancellationToken token,
        out byte[] report, out int bytesRead)
    {
        report = new byte[reportLength];
        bytesRead = 0;
        token.ThrowIfCancellationRequested();
        using var evt = CreateEventW(IntPtr.Zero, true, false, null);
        if (evt.IsInvalid) return false;
        IntPtr buffer = Marshal.AllocHGlobal(reportLength);
        try
        {
            var overlapped = new NativeOverlapped { EventHandle = evt.DangerousGetHandle() };
            bool ok = ReadFile(handle, buffer, (uint)reportLength, IntPtr.Zero, ref overlapped);
            if (!ok && Marshal.GetLastWin32Error() != ErrorIoPending) return false;
            uint wait = WaitForSingleObject(evt, (uint)Math.Clamp(timeoutMs, 0, 1000));
            if (wait != WaitObject0)
            {
                CancelIoEx(handle, ref overlapped);
                WaitForSingleObject(evt, Infinite);
                token.ThrowIfCancellationRequested();
                return false;
            }
            if (!GetOverlappedResult(handle, ref overlapped, out uint count, false)) return false;
            bytesRead = (int)count;
            if (bytesRead > reportLength) return false;
            Marshal.Copy(buffer, report, 0, bytesRead);
            return true;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool TryGetInterface(IntPtr set, ref SpDeviceInterfaceData iface, out string path, out SpDeviceInfoData info)
    {
        path = string.Empty;
        info = new SpDeviceInfoData { CbSize = (uint)Marshal.SizeOf<SpDeviceInfoData>() };
        SetupDiGetDeviceInterfaceDetailW(set, ref iface, IntPtr.Zero, 0, out uint needed, IntPtr.Zero);
        if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer || needed < 8) return false;
        IntPtr detail = Marshal.AllocHGlobal((int)needed);
        IntPtr infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SpDeviceInfoData>());
        try
        {
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            Marshal.StructureToPtr(info, infoPtr, false);
            if (!SetupDiGetDeviceInterfaceDetailW(set, ref iface, detail, needed, out _, infoPtr)) return false;
            int pathOffset = 4;
            path = Marshal.PtrToStringUni(IntPtr.Add(detail, pathOffset)) ?? string.Empty;
            info = Marshal.PtrToStructure<SpDeviceInfoData>(infoPtr);
            return path.Length > 0;
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
            Marshal.FreeHGlobal(infoPtr);
        }
    }

    private static Guid? TryGetContainerId(IntPtr set, ref SpDeviceInfoData info)
    {
        var key = new DevPropKey { FormatId = DeviceContainerIdKeyFmtid, PropertyId = 2 };
        if (!SetupDiGetDevicePropertyW(set, ref info, ref key, out uint type, out Guid value,
                (uint)Marshal.SizeOf<Guid>(), out _, 0) || type != DevpropTypeGuid)
            return null;
        return value;
    }

    private static Guid GetHidClassGuid()
    {
        HidD_GetHidGuid(out var guid);
        return guid;
    }

    private static byte Sum(byte[] buffer, int length)
    {
        int sum = 0;
        for (int i = 0; i < length; i++) sum += buffer[i];
        return (byte)sum;
    }

    private const uint DevpropTypeGuid = 0x0000000D;
    private const uint ErrorNoMoreItems = 259;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct HidAttributes
    {
        internal uint Size;
        internal ushort VendorId;
        internal ushort ProductId;
        internal ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidCaps
    {
        internal ushort Usage, UsagePage;
        internal ushort InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        internal ushort Reserved0, Reserved1, Reserved2, Reserved3, Reserved4, Reserved5, Reserved6, Reserved7;
        internal ushort Reserved8, Reserved9, Reserved10, Reserved11, Reserved12, Reserved13, Reserved14, Reserved15, Reserved16;
        internal ushort NumberLinkCollectionNodes;
        internal ushort NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        internal ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        internal ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        internal uint CbSize;
        internal Guid InterfaceClassGuid;
        internal uint Flags;
        internal UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInfoData
    {
        internal uint CbSize;
        internal Guid ClassGuid;
        internal uint DevInst;
        internal UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        internal Guid FormatId;
        internal uint PropertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeOverlapped
    {
        internal IntPtr Internal;
        internal IntPtr InternalHigh;
        internal uint Offset;
        internal uint OffsetHigh;
        internal IntPtr EventHandle;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HidAttributes attributes);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);
    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HidCaps capabilities);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_FlushQueue(SafeFileHandle hidDeviceObject);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData, IntPtr detailData, uint detailDataSize, out uint requiredSize, IntPtr deviceInfoData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr deviceInfoSet, ref SpDeviceInfoData deviceInfoData, ref DevPropKey propertyKey, out uint propertyType, out Guid propertyBuffer, uint propertyBufferSize, out uint requiredSize, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(SafeFileHandle file, IntPtr buffer, uint bytesToWrite, IntPtr bytesWritten, ref NativeOverlapped overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(SafeFileHandle file, IntPtr buffer, uint bytesToRead, IntPtr bytesRead, ref NativeOverlapped overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOverlappedResult(SafeFileHandle file, ref NativeOverlapped overlapped, out uint bytesTransferred, [MarshalAs(UnmanagedType.Bool)] bool wait);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(SafeFileHandle file, ref NativeOverlapped overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateEventW(IntPtr eventAttributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
}

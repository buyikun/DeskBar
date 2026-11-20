using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DeskBar.Services;

/// <summary>Reads only HID's standard battery usages; never sends vendor protocol commands.</summary>
internal static class GenericHidBatteryReader
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const uint ErrorNoMoreItems = 259;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint DevpropTypeGuid = 0x0000000D;
    private const uint HidpStatusSuccess = 0x00110000;
    private const int MaxReportLength = 4096;
    private const int MaxValueCaps = 1024;

    private const ushort UsagePageBatterySystem = 0x0085;
    private const ushort UsageBatteryStrength = 0x0020;
    private const ushort UsageAbsoluteStateOfCharge = 0x0065;
    private const ushort UsagePageDigitizers = 0x000D;
    private const ushort UsagePageUps = 0x0084;

    private static readonly DevPropKey DeviceContainerIdKey = new(
        new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);

    internal static IReadOnlyList<WirelessBatteryDevice> Read(
        CancellationToken token, IReadOnlySet<string> bluetoothContainerIds)
    {
        if (bluetoothContainerIds is null) throw new ArgumentNullException(nameof(bluetoothContainerIds));

        var result = new List<WirelessBatteryDevice>();
        var seenDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Guid hidGuid = HidGuidHolder.HidGuid;
        IntPtr set = SetupDiGetClassDevsW(ref hidGuid, null, IntPtr.Zero,
            DigcfPresent | DigcfDeviceInterface);
        if (set == InvalidHandleValue)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevsW(HID) failed");

        try
        {
            var collections = EnumerateCollections(set, token);
            var digitizerIds = new HashSet<DeviceKey>();
            foreach (var collection in collections)
                if (collection.UsagePage == UsagePageDigitizers)
                    digitizerIds.Add(collection.DeviceKey);

            foreach (var collection in collections)
            {
                token.ThrowIfCancellationRequested();
                if (collection.UsagePage == UsagePageUps) continue;
                if (digitizerIds.Contains(collection.DeviceKey)) continue;
                if (!string.IsNullOrEmpty(collection.ContainerId) &&
                    bluetoothContainerIds.Contains(collection.ContainerId)) continue;

                string id = !string.IsNullOrEmpty(collection.ContainerId)
                    ? $"hid:{collection.Vid:X4}:{collection.Pid:X4}:{collection.ContainerId}"
                    : $"hid:{collection.Vid:X4}:{collection.Pid:X4}:{collection.Path}";
                if (seenDevices.Contains(id)) continue;

                try
                {
                    if (TryReadCollection(collection, token, out int percent, out string name))
                    {
                        seenDevices.Add(id);
                        result.Add(new WirelessBatteryDevice(id, name, percent, "HID", false));
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // One disconnected or malformed HID collection must not stop the rest of the scan.
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return Array.AsReadOnly(result.ToArray());
    }

    private static List<HidCollection> EnumerateCollections(IntPtr set, CancellationToken token)
    {
        var result = new List<HidCollection>();
        for (uint index = 0; ; index++)
        {
            token.ThrowIfCancellationRequested();
            var interfaceData = new SpDeviceInterfaceData { CbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>() };
            if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref HidGuidHolder.HidGuid, index, ref interfaceData))
            {
                uint error = (uint)Marshal.GetLastWin32Error();
                if (error == ErrorNoMoreItems) break;
                throw new InvalidOperationException($"SetupDiEnumDeviceInterfaces(HID) failed: {error}");
            }

            uint required = 0;
            SetupDiGetDeviceInterfaceDetailW(set, ref interfaceData, IntPtr.Zero, 0, out required, IntPtr.Zero);
            if (required < 8 || required > 65536) continue;

            IntPtr detail = Marshal.AllocHGlobal((int)required);
            try
            {
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                var devInfo = new SpDeviceInfoData { CbSize = (uint)Marshal.SizeOf<SpDeviceInfoData>() };
                if (!SetupDiGetDeviceInterfaceDetailW(set, ref interfaceData, detail, required, out _, ref devInfo)) continue;

                string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4)) ?? string.Empty;
                if (path.Length == 0) continue;
                IntPtr handle = CreateFileW(path, GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                if (handle == InvalidHandleValue) continue;
                try
                {
                    var attributes = new HidAttributes { Size = (uint)Marshal.SizeOf<HidAttributes>() };
                    if (!HidD_GetAttributes(handle, ref attributes)) continue;
                    if (!HidD_GetPreparsedData(handle, out IntPtr preparsed)) continue;
                    try
                    {
                        if (HidP_GetCaps(preparsed, out var caps) != HidpStatusSuccess) continue;
                        if (caps.InputReportByteLength > MaxReportLength || caps.FeatureReportByteLength > MaxReportLength) continue;
                        result.Add(new HidCollection(path, ReadContainerId(set, ref devInfo), attributes.VendorId,
                            attributes.ProductId, caps.UsagePage, caps, ReadProductName(handle)));
                    }
                    finally { HidD_FreePreparsedData(preparsed); }
                }
                finally { CloseHandle(handle); }
            }
            finally { Marshal.FreeHGlobal(detail); }
        }
        return result;
    }

    private static bool TryReadCollection(HidCollection collection, CancellationToken token, out int percent, out string name)
    {
        percent = -1;
        name = string.IsNullOrWhiteSpace(collection.ProductName) ? "Wireless device" : collection.ProductName;
        IntPtr handle = CreateFileW(collection.Path, GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle == InvalidHandleValue) return false;
        try
        {
            if (!HidD_GetPreparsedData(handle, out IntPtr preparsed)) return false;
            try
            {
                if (HidP_GetCaps(preparsed, out var caps) != HidpStatusSuccess) return false;
                if (!TryReadReport(handle, preparsed, caps, HidpReportType.Feature, token, collection.Vid, out percent) &&
                    !TryReadReport(handle, preparsed, caps, HidpReportType.Input, token, collection.Vid, out percent))
                    return false;
                return percent is >= 0 and <= 100;
            }
            finally { HidD_FreePreparsedData(preparsed); }
        }
        finally { CloseHandle(handle); }
    }

    private static bool TryReadReport(IntPtr handle, IntPtr preparsed, HidpCaps caps,
        HidpReportType reportType, CancellationToken token, ushort vendorId, out int percent)
    {
        percent = -1;
        ushort number = reportType == HidpReportType.Feature ? caps.NumberFeatureValueCaps : caps.NumberInputValueCaps;
        ushort reportLength = reportType == HidpReportType.Feature ? caps.FeatureReportByteLength : caps.InputReportByteLength;
        if (number == 0 || number > MaxValueCaps || reportLength == 0 || reportLength > MaxReportLength) return false;

        var values = new HidpValueCaps[number];
        if (HidP_GetValueCaps(reportType, values, ref number, preparsed) != HidpStatusSuccess) return false;
        foreach (var value in values)
        {
            token.ThrowIfCancellationRequested();
            ushort usage = value.IsRange != 0 ? value.Range.Range.UsageMin : value.Range.NotRange.Usage;
            if (!IsBatteryUsage(value.UsagePage, usage)) continue;

            var report = new byte[reportLength];
            report[0] = value.ReportId;
            bool received = reportType == HidpReportType.Feature
                ? HidD_GetFeature(handle, report, reportLength)
                : HidD_GetInputReport(handle, report, reportLength);
            if (!received) continue;

            uint raw = 0;
            if (HidP_GetUsageValue(reportType, value.UsagePage, value.LinkCollection, usage,
                    out raw, preparsed, report, reportLength) != HidpStatusSuccess) continue;

            long signedValue = unchecked((int)raw);
            if (vendorId == 0x05AC && raw <= 100)
                percent = (int)raw; // Apple HID products report percent despite a 0..255 descriptor range.
            else if (value.LogicalMax > value.LogicalMin)
                percent = (int)Math.Clamp((signedValue - value.LogicalMin) * 100L /
                    ((long)value.LogicalMax - value.LogicalMin), 0L, 100L);
            else if (raw <= 100)
                percent = (int)raw;

            if (percent is >= 0 and <= 100) return true;
            percent = -1;
        }
        return false;
    }

    private static bool IsBatteryUsage(ushort page, ushort usage) =>
        (page == 0x0006 && usage == UsageBatteryStrength) ||
        (page == UsagePageBatterySystem && usage == UsageAbsoluteStateOfCharge);

    private static string ReadContainerId(IntPtr set, ref SpDeviceInfoData info)
    {
        var key = DeviceContainerIdKey;
        bool queried = SetupDiGetDevicePropertyW(set, ref info, ref key, out uint type, null, 0, out uint required, 0);
        if (queried || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer ||
            type != DevpropTypeGuid || required < 16 || required > 64)
            return string.Empty;
        var bytes = new byte[required];
        if (!SetupDiGetDevicePropertyW(set, ref info, ref key, out type, bytes, required, out _, 0) || type != DevpropTypeGuid)
            return string.Empty;
        var guid = new Guid(bytes.AsSpan(0, 16));
        return guid == Guid.Empty ? string.Empty : guid.ToString("B");
    }

    private static string ReadProductName(IntPtr handle)
    {
        var text = new char[256];
        if (!HidD_GetProductString(handle, text, (uint)(text.Length * sizeof(char)))) return string.Empty;
        int length = Array.IndexOf(text, '\0');
        return new string(text, 0, length < 0 ? text.Length : length).Trim();
    }

    private static readonly IntPtr InvalidHandleValue = new(-1);
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint OpenExisting = 3;

    private readonly record struct DeviceKey(ushort VendorId, ushort ProductId)
    {
        public override string ToString() => $"{VendorId:X4}:{ProductId:X4}";
    }

    private sealed record HidCollection(string Path, string ContainerId, ushort Vid, ushort Pid,
        ushort UsagePage, HidpCaps Caps, string ProductName)
    {
        internal DeviceKey DeviceKey => new(Vid, Pid);
    }

    private static class HidGuidHolder
    {
        internal static Guid HidGuid = GetHidGuid();
        private static Guid GetHidGuid()
        {
            HidD_GetHidGuid(out Guid guid);
            return guid;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidAttributes
    {
        internal uint Size;
        internal ushort VendorId;
        internal ushort ProductId;
        internal ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct HidpCaps
    {
        internal ushort Usage;
        internal ushort UsagePage;
        internal ushort InputReportByteLength;
        internal ushort OutputReportByteLength;
        internal ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] internal ushort[] Reserved;
        internal ushort NumberLinkCollectionNodes;
        internal ushort NumberInputButtonCaps;
        internal ushort NumberInputValueCaps;
        internal ushort NumberInputDataIndices;
        internal ushort NumberOutputButtonCaps;
        internal ushort NumberOutputValueCaps;
        internal ushort NumberOutputDataIndices;
        internal ushort NumberFeatureButtonCaps;
        internal ushort NumberFeatureValueCaps;
        internal ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct HidpValueCaps
    {
        internal ushort UsagePage;
        internal byte ReportId;
        internal byte IsAlias;
        internal ushort BitField;
        internal ushort LinkCollection;
        internal ushort LinkUsage;
        internal ushort LinkUsagePage;
        internal byte IsRange;
        internal byte IsStringRange;
        internal byte IsDesignatorRange;
        internal byte IsAbsolute;
        internal byte HasNull;
        internal byte Reserved;
        internal ushort BitSize;
        internal ushort ReportCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)] internal ushort[] Reserved2;
        internal uint UnitsExp;
        internal uint Units;
        internal int LogicalMin;
        internal int LogicalMax;
        internal int PhysicalMin;
        internal int PhysicalMax;
        internal ValueUnion Range;
        [StructLayout(LayoutKind.Explicit, Size = 16)]
        internal struct ValueUnion
        {
            [FieldOffset(0)] internal UsageRange Range;
            [FieldOffset(0)] internal UsageNotRange NotRange;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        internal struct UsageRange
        {
            internal ushort UsageMin, UsageMax, StringMin, StringMax, DesignatorMin, DesignatorMax, DataIndexMin, DataIndexMax;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        internal struct UsageNotRange
        {
            internal ushort Usage, Reserved1, StringIndex, Reserved2, DesignatorIndex, Reserved3, DataIndex, Reserved4;
        }
    }

    private enum HidpReportType
    {
        Input = 0,
        Output = 1,
        Feature = 2
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
        internal Guid FmtId;
        internal uint Pid;
        internal DevPropKey(Guid fmtId, uint pid) { FmtId = fmtId; Pid = pid; }
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetAttributes(IntPtr hidDeviceObject, ref HidAttributes attributes);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetPreparsedData(IntPtr hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", EntryPoint = "HidD_GetFeature", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetFeature(IntPtr hidDeviceObject, [Out] byte[] reportBuffer, uint reportBufferLength);

    [DllImport("hid.dll", EntryPoint = "HidD_GetInputReport", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetInputReport(IntPtr hidDeviceObject, [Out] byte[] reportBuffer, uint reportBufferLength);

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetProductString(IntPtr hidDeviceObject, [Out] char[] buffer, uint bufferLength);

    [DllImport("hid.dll")]
    private static extern uint HidP_GetCaps(IntPtr preparsedData, out HidpCaps capabilities);

    [DllImport("hid.dll")]
    private static extern uint HidP_GetValueCaps(HidpReportType reportType, [Out] HidpValueCaps[] valueCaps, ref ushort valueCapsLength, IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern uint HidP_GetUsageValue(HidpReportType reportType, ushort usagePage, ushort linkCollection,
        ushort usage, out uint usageValue, IntPtr preparsedData, [In] byte[] report, uint reportLength);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
        ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize, out uint requiredSize, ref SpDeviceInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr deviceInfoSet, ref SpDeviceInfoData deviceInfoData,
        ref DevPropKey propertyKey, out uint propertyType, byte[]? propertyBuffer, uint propertyBufferSize,
        out uint requiredSize, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

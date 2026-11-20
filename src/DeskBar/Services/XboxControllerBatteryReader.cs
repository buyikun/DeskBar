using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DeskBar.Services;

internal static class XboxControllerBatteryReader
{
    private const uint ErrorNoMoreItems = 259;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;
    private const uint DevpropTypeGuid = 0x0000000D;
    private const uint DevpropTypeString = 0x00000012;
    private const string XboxHidId = "VID&0002045E_PID&02E0";
    private static readonly IntPtr InvalidHandleValue = new(-1);
    private static readonly DevPropKey ContainerIdKey = new(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);
    private static readonly DevPropKey FriendlyNameKey = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    internal static IReadOnlyList<WirelessBatteryDevice> Read(CancellationToken token, IReadOnlySet<string> existingIds)
    {
        int onlineSlots = 0;
        for (uint user = 0; user < 4; user++)
        {
            token.ThrowIfCancellationRequested();
            if (XInputGetState(user, out _) == 0) onlineSlots++;
        }
        if (onlineSlots != 1) return Array.Empty<WirelessBatteryDevice>();

        Guid classGuid = Guid.Empty;
        IntPtr set = SetupDiGetClassDevsW(ref classGuid, "BTHENUM", IntPtr.Zero, DigcfPresent | DigcfAllClasses);
        if (set == InvalidHandleValue) throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevsW(BTHENUM) failed");
        try
        {
            var devices = new Dictionary<Guid, Candidate>();
            for (uint index = 0; ; index++)
            {
                token.ThrowIfCancellationRequested();
                var info = new SpDevinfoData { CbSize = (uint)Marshal.SizeOf<SpDevinfoData>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref info))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems) break;
                    throw new Win32Exception(error, "SetupDiEnumDeviceInfo(BTHENUM) failed");
                }

                string instanceId = GetInstanceId(set, ref info);
                bool isRoot = instanceId.StartsWith("BTHENUM\\DEV_", StringComparison.OrdinalIgnoreCase);
                bool isXboxHid = instanceId.Contains(XboxHidId, StringComparison.OrdinalIgnoreCase);
                if (!isRoot && !isXboxHid) continue;
                if (!TryGetProperty(set, ref info, ContainerIdKey, out uint type, out byte[] bytes) ||
                    type != DevpropTypeGuid || bytes.Length < 16) continue;

                var container = new Guid(bytes.AsSpan(0, 16));
                if (container == Guid.Empty) continue;
                if (!devices.TryGetValue(container, out var candidate))
                {
                    candidate = new Candidate();
                    devices.Add(container, candidate);
                }
                if (isRoot)
                {
                    candidate.HasRoot = true;
                    string name = ReadStringProperty(set, ref info, FriendlyNameKey);
                    if (!string.IsNullOrWhiteSpace(name)) candidate.Name = name;
                }
                if (isXboxHid) candidate.HasXboxHid = true;
            }

            var matches = new List<WirelessBatteryDevice>();
            foreach (var pair in devices)
            {
                string id = pair.Key.ToString("B");
                Candidate candidate = pair.Value;
                if (!candidate.HasRoot || !candidate.HasXboxHid || existingIds.Contains(id)) continue;
                string name = string.IsNullOrWhiteSpace(candidate.Name) ? "Xbox Wireless Controller" : candidate.Name;
                matches.Add(new WirelessBatteryDevice(id, name, -1, "Xbox Wireless · 电量未报告", false, "controller"));
            }
            return matches.Count == 1 ? Array.AsReadOnly(matches.ToArray()) : Array.Empty<WirelessBatteryDevice>();
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static string GetInstanceId(IntPtr set, ref SpDevinfoData info)
    {
        var buffer = new StringBuilder(512);
        return SetupDiGetDeviceInstanceIdW(set, ref info, buffer, buffer.Capacity, out _)
            ? buffer.ToString()
            : string.Empty;
    }

    private static string ReadStringProperty(IntPtr set, ref SpDevinfoData info, DevPropKey key)
    {
        if (!TryGetProperty(set, ref info, key, out uint type, out byte[] bytes) || type != DevpropTypeString)
            return string.Empty;
        return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
    }

    private static bool TryGetProperty(IntPtr set, ref SpDevinfoData info, DevPropKey key, out uint type, out byte[] bytes)
    {
        type = 0;
        bytes = Array.Empty<byte>();
        if (SetupDiGetDevicePropertyW(set, ref info, ref key, out type, null, 0, out uint required, 0)) return false;
        if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer || required == 0 || required > 4096) return false;
        bytes = new byte[required];
        return SetupDiGetDevicePropertyW(set, ref info, ref key, out type, bytes, required, out _, 0);
    }

    private sealed class Candidate
    {
        internal string Name = string.Empty;
        internal bool HasRoot;
        internal bool HasXboxHid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        internal ushort Buttons;
        internal byte LeftTrigger;
        internal byte RightTrigger;
        internal short ThumbLX;
        internal short ThumbLY;
        internal short ThumbRX;
        internal short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        internal uint Packet;
        internal XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        internal Guid Format;
        internal uint Pid;
        internal DevPropKey(Guid format, uint pid) { Format = format; Pid = pid; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        internal uint CbSize;
        internal Guid ClassGuid;
        internal uint DevInst;
        internal UIntPtr Reserved;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint user, out XInputState state);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SpDevinfoData info);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SpDevinfoData info, StringBuilder id, int capacity, out int required);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SpDevinfoData info, ref DevPropKey key, out uint type, byte[]? data, uint size, out uint required, uint flags);

    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}

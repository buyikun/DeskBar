using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeskBar.Services;

/// <summary>
/// 无线设备的三种显示模式（按设备设置）：
/// <list type="bullet">
/// <item><c>always</c> 常显 —— 断开后仍用最后一次读数显示（stale 时压暗）。</item>
/// <item><c>connected</c> 连接时显示、断开隐藏 —— 默认。</item>
/// <item><c>never</c> 完全不显示。</item>
/// </list>
/// </summary>
internal static class WirelessBatteryDisplayModes
{
    public const string Always = "always";
    public const string Connected = "connected";
    public const string Never = "never";

    public static bool IsKnown(string? value) => value is Always or Connected or Never;

    public static string Normalize(string? value) => IsKnown(value) ? value! : Connected;

    /// <summary>按钮/菜单上的短标签。</summary>
    public static string Label(string? value) => Normalize(value) switch
    {
        Always => "常显",
        Never => "不显示",
        _ => "连接时"
    };
}

internal sealed class WirelessBatteryHistoryEntry
{
    public WirelessBatteryHistoryEntry() { }

    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "other";
    public int Percent { get; set; } = -1;
    public string Connection { get; set; } = string.Empty;
    public DateTime LastSeenUtc { get; set; }
    /// <summary>显示模式，见 <see cref="WirelessBatteryDisplayModes"/>。默认「连接时显示」。</summary>
    public string DisplayMode { get; set; } = WirelessBatteryDisplayModes.Connected;
    /// <summary>旧版二态「隐藏」字段，仅用于迁移成 <see cref="DisplayMode"/> = never，不再写入。</summary>
    public bool Hidden { get; set; }
    /// <summary>
    /// 上一次已知的连接状态。**必须落盘**：模式二（连接时显示/断开隐藏）依赖它，
    /// 若不落盘，每次启动它都从 false 起步，会把「接着的设备」判成断开、把胶囊整块藏掉。
    /// </summary>
    public bool IsConnected { get; set; }
    /// <summary>
    /// 连续「没被读到」的起始时刻；读到就清空，落盘以便跨重启保留。
    /// 用于去抖：扫描周期 60 秒，AULA 这类省电设备单次漏读也会整轮缺席，
    /// 只有连续缺席超过 DisconnectGrace 才算真的断开。
    /// </summary>
    public DateTime? MissingSinceUtc { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ConnectionKnown { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsStale { get; set; }
    public WirelessBatteryHistoryEntry Copy() => (WirelessBatteryHistoryEntry)MemberwiseClone();
}

internal sealed class WirelessBatteryDevice
{
    internal WirelessBatteryDevice(string id, string name, int percent, string connection, bool isStale, string type = "other", bool isConnected = true, bool connectionKnown = true, string displayMode = WirelessBatteryDisplayModes.Connected)
    {
        Id = id;
        Name = name;
        Percent = percent;
        Connection = connection;
        IsStale = isStale;
        Type = AppSettings.IsKnownWirelessBatteryType(type) && type != "auto" ? type : "other";
        IsConnected = isConnected;
        ConnectionKnown = connectionKnown;
        DisplayMode = WirelessBatteryDisplayModes.Normalize(displayMode);
    }

    internal string Id { get; }
    internal string Name { get; }
    internal int Percent { get; }
    internal string Connection { get; }
    internal bool IsStale { get; }
    internal bool IsConnected { get; }
    internal bool ConnectionKnown { get; }
    internal string DisplayMode { get; }
    internal string Type { get; }
    /// <summary>按自身显示模式判断现在该不该出现在任务栏上。</summary>
    internal bool IsVisibleOnTaskbar => DisplayMode switch
    {
        WirelessBatteryDisplayModes.Never => false,
        WirelessBatteryDisplayModes.Always => true,
        _ => IsConnected
    };
    internal string DisplayPercent => Percent is >= 0 and <= 100 ? $"{Percent}%" : "--";
}

internal sealed class WirelessBatteryService : IDisposable
{
    private static readonly DevPropKey DeviceContainerIdKey = new(
        new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);
    private static readonly DevPropKey BluetoothBatteryKey = new(
        new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 2);
    private static readonly DevPropKey IsConnectedKey = new(
        new Guid("83DA6326-97A6-4088-9453-A1923F573B29"), 15);

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Timer _timer;
    private readonly AppSettings _settings;
    private IReadOnlyList<WirelessBatteryDevice> _currentDevices = Array.Empty<WirelessBatteryDevice>();
    private string _statusText = "正在等待首次扫描";
    private bool _started;
    private bool _disposed;

    internal WirelessBatteryService(AppSettings settings)
    {
        _settings = settings;
        _currentDevices = BuildHistoryDevices(settings.GetWirelessBatteryHistory(), Array.Empty<WirelessBatteryDevice>(), connectedKnown: false, forceUnknown: true);
        _timer = new Timer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    internal event Action<IReadOnlyList<WirelessBatteryDevice>>? DevicesChanged;

    internal IReadOnlyList<WirelessBatteryDevice> CurrentDevices
    {
        get { lock (_stateLock) return _currentDevices; }
    }

    internal string StatusText
    {
        get { lock (_stateLock) return _statusText; }
    }

    internal void Start()
    {
        lock (_stateLock)
        {
            if (_disposed || _started) return;
            _started = true;
            _timer.Change(RefreshInterval, RefreshInterval);
        }
        _ = RefreshAsync();
    }

    internal async Task RefreshAsync()
    {
        if (_shutdown.IsCancellationRequested) return;

        try
        {
            if (!await _refreshGate.WaitAsync(0, _shutdown.Token).ConfigureAwait(false)) return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            IReadOnlyList<WirelessBatteryDevice> found;
            bool scanComplete = true;
            try
            {
                var bluetoothDevices = Array.Empty<WirelessBatteryDevice>();
                bool anyScanSucceeded = false;
                try
                {
                    bluetoothDevices = (await Task.Run(() => EnumerateDevices(_shutdown.Token), _shutdown.Token).ConfigureAwait(false)).ToArray();
                    anyScanSucceeded = true;

                    Log.Debug($"wireless battery Bluetooth scan: {bluetoothDevices.Length} devices");
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return; }
                catch (Exception ex) { scanComplete = false; Log.Debug($"wireless battery Bluetooth scan failed: {ex.GetType().Name}: {ex.Message}"); }

                var merged = new List<WirelessBatteryDevice>(bluetoothDevices);
                try
                {
                    var knownIds = new HashSet<string>(merged.Select(device => device.Id), StringComparer.OrdinalIgnoreCase);
                    merged.AddRange(await Task.Run(() => XboxControllerBatteryReader.Read(_shutdown.Token, knownIds), _shutdown.Token).ConfigureAwait(false));
                    anyScanSucceeded = true;

                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return; }
                catch (Exception ex) { scanComplete = false; Log.Debug($"wireless battery Xbox scan failed: {ex.GetType().Name}: {ex.Message}"); }
                try
                {
                    var foundByHyperX = await HyperXHidBatteryReader.ReadAsync(_shutdown.Token).ConfigureAwait(false);
                    merged.AddRange(foundByHyperX);
                    anyScanSucceeded = true;

                    Log.Debug($"wireless battery HyperX scan: {foundByHyperX.Count} devices");
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return; }
                catch (Exception ex) { scanComplete = false; Log.Debug($"wireless battery HyperX scan failed: {ex.GetType().Name}: {ex.Message}"); }
                try
                {
                    var foundByAula = await Task.Run(() => AulaReceiverBatteryReader.Read(_shutdown.Token), _shutdown.Token).ConfigureAwait(false);
                    merged.AddRange(foundByAula);
                    anyScanSucceeded = true;

                    Log.Debug($"wireless battery AULA scan: {foundByAula.Count} devices");
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return; }
                catch (Exception ex) { scanComplete = false; Log.Debug($"wireless battery AULA scan failed: {ex.GetType().Name}: {ex.Message}"); }
                try
                {
                    var bluetoothContainers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var device in bluetoothDevices)
                        if (device.Id.Length != 0) bluetoothContainers.Add(device.Id);
                    var foundByGenericHid = await Task.Run(() => GenericHidBatteryReader.Read(_shutdown.Token, bluetoothContainers), _shutdown.Token).ConfigureAwait(false);
                    merged.AddRange(foundByGenericHid);
                    anyScanSucceeded = true;

                    Log.Debug($"wireless battery generic HID scan: {foundByGenericHid.Count} devices");
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return; }
                catch (Exception ex) { scanComplete = false; Log.Debug($"wireless battery generic HID scan failed: {ex.GetType().Name}: {ex.Message}"); }
                found = merged.AsReadOnly();
                if (!anyScanSucceeded) { PublishFailure(); return; }
                Log.Debug($"wireless battery scan complete: {found.Count} devices");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                PublishFailure();
                return;
            }

            if (!_shutdown.IsCancellationRequested) PublishSuccess(found, scanComplete);
        }
        finally
        {
            try { _refreshGate.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    internal void SetDisplayMode(string id, string mode)
    {
        IReadOnlyList<WirelessBatteryDevice> updated;
        lock (_stateLock)
        {
            if (_disposed || !_currentDevices.Any(device => StringComparer.OrdinalIgnoreCase.Equals(device.Id, id))) return;
            _settings.SetWirelessBatteryDisplayMode(id, mode);
            var history = _settings.GetWirelessBatteryHistory();
            updated = BuildHistoryDevices(history, _currentDevices.ToArray(), connectedKnown: false);
            _currentDevices = updated;
            _statusText = updated.Count == 0 ? "尚无无线设备记录" : $"设备记录 {updated.Count} 台";
        }
        NotifyDevicesChanged(updated);
    }

    internal void RemoveDevice(string id)
    {
        IReadOnlyList<WirelessBatteryDevice> updated;
        lock (_stateLock)
        {
            if (_disposed || !_currentDevices.Any(device => StringComparer.OrdinalIgnoreCase.Equals(device.Id, id))) return;
            _settings.RemoveWirelessBatteryHistory(id);
            var history = _settings.GetWirelessBatteryHistory();
            updated = BuildHistoryDevices(history, _currentDevices.ToArray(), connectedKnown: false);
            _currentDevices = updated;
            _statusText = updated.Count == 0 ? "尚无无线设备记录" : $"设备记录 {updated.Count} 台";
        }
        NotifyDevicesChanged(updated);
    }

    private void NotifyDevicesChanged(IReadOnlyList<WirelessBatteryDevice> devices)
    {
        Action<IReadOnlyList<WirelessBatteryDevice>>? handler;
        lock (_stateLock) handler = _disposed ? null : DevicesChanged;
        try { handler?.Invoke(devices); }
        catch { }
    }

    private void OnTimer(object? state) => _ = RefreshAsync();

    private void PublishSuccess(IReadOnlyList<WirelessBatteryDevice> devices, bool scanComplete)
    {
        Action<IReadOnlyList<WirelessBatteryDevice>>? handler;
        lock (_stateLock)
        {
            if (_disposed) return;
            var history = _settings.MergeWirelessBatteryHistory(devices, scanComplete);
            _currentDevices = BuildHistoryDevices(history, devices, connectedKnown: scanComplete);
            _statusText = _currentDevices.Count == 0 ? "尚无无线设备记录" : $"设备记录 {_currentDevices.Count} 台 · 本轮读取 {devices.Count} 台";
            handler = DevicesChanged;
            devices = _currentDevices;
        }
        try { handler?.Invoke(devices); }
        catch { }
    }

    private static IReadOnlyList<WirelessBatteryDevice> BuildHistoryDevices(
        IReadOnlyList<WirelessBatteryHistoryEntry> history,
        IReadOnlyList<WirelessBatteryDevice> found,
        bool connectedKnown = true,
        bool forceUnknown = false)
    {
        var byId = new Dictionary<string, WirelessBatteryDevice>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in found) byId[device.Id] = device;
        var result = new List<WirelessBatteryDevice>(history.Count);
        foreach (var entry in history)
        {
            bool connected = byId.TryGetValue(entry.Id, out var live);
            // 连接状态：found 里有就该用**它自己的** IsConnected；没有则沿用 history 里
            // 「去抖后」的值（可能还在宽限期内）。不能写成 `connected || entry.IsConnected` ——
            // SetDisplayMode 会把旧设备列表整个传进 found，那样所有历史设备都会被判成已连接。
            bool isConnected = connected ? live!.IsConnected : entry.IsConnected;
            string name = connected && !string.IsNullOrWhiteSpace(live!.Name) ? live.Name : entry.Name;
            bool hasFreshPercent = connected && live!.Percent is >= 0 and <= 100;
            int percent = hasFreshPercent ? live!.Percent : entry.Percent;
            string connection = connected ? live!.Connection : entry.Connection;
            string type = connected ? live!.Type : entry.Type;
            bool knownState = !forceUnknown && (connected ? live!.ConnectionKnown : entry.ConnectionKnown);
            bool stale = !connected || (connected && live!.IsStale) || (!hasFreshPercent && percent >= 0);
            result.Add(new WirelessBatteryDevice(entry.Id, name, percent, connection, stale, type, isConnected, knownState, entry.DisplayMode));
        }
        return Array.AsReadOnly(result.ToArray());
    }

    private void PublishFailure()
    {
        Action<IReadOnlyList<WirelessBatteryDevice>>? handler;
        IReadOnlyList<WirelessBatteryDevice> stale;
        lock (_stateLock)
        {
            if (_disposed) return;
            var previous = _currentDevices;
            var copy = new WirelessBatteryDevice[previous.Count];
            for (var i = 0; i < previous.Count; i++)
            {
                var device = previous[i];
                copy[i] = new WirelessBatteryDevice(device.Id, device.Name, device.Percent, device.Connection, true, device.Type, device.IsConnected, device.ConnectionKnown, device.DisplayMode);
            }
            stale = Array.AsReadOnly(copy);
            _currentDevices = stale;
            _statusText = "无线设备电量采集失败，显示的数据可能已过期";
            handler = DevicesChanged;
        }
        try { handler?.Invoke(stale); }
        catch { }
    }

    private IReadOnlyList<WirelessBatteryDevice> EnumerateDevices(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DigcfAllClasses | DigcfPresent);
        if (set == InvalidHandleValue) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevsW failed");

        try
        {
            var mains = new Dictionary<string, MainNode>(StringComparer.OrdinalIgnoreCase);
            var batteryNodes = new Dictionary<string, BatteryNode>(StringComparer.OrdinalIgnoreCase);
            for (uint index = 0; ; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new SpDevinfoData { CbSize = (uint)Marshal.SizeOf<SpDevinfoData>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref info))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems) break;
                    throw new InvalidOperationException($"SetupDiEnumDeviceInfo failed: {error}");
                }

                try
                {
                    var instanceId = GetInstanceId(set, ref info);
                    if (!IsBluetoothInstance(instanceId)) continue;

                    var containerId = GetContainerId(set, ref info);
                    var stableId = containerId is Guid container && container != Guid.Empty
                        ? container.ToString("B")
                        : instanceId;
                    var name = ReadStringProperty(set, ref info, FriendlyNameKey);
                    if (string.IsNullOrWhiteSpace(name)) name = ReadStringProperty(set, ref info, DeviceDescKey);
                    if (string.IsNullOrWhiteSpace(name)) name = "Bluetooth device";

                    if (IsMainBluetoothNode(instanceId))
                    {
                        bool? connected = TryReadConnected(set, ref info, out var isConnected) ? isConnected : null;
                        mains[stableId] = new MainNode(name, connected);
                    }

                    if (!TryReadBattery(set, ref info, out var percent)) continue;
                    var batteryConnected = TryReadConnected(set, ref info, out var nodeConnected) ? nodeConnected : true;
                    var candidate = new BatteryNode(StripServiceSuffix(name), percent, batteryConnected);
                    if (!batteryNodes.TryGetValue(stableId, out var old) || (!old.Connected && candidate.Connected))
                        batteryNodes[stableId] = candidate;
                }
                catch
                {
                    // A malformed or disappearing device node must not abort the remaining scan.
                }
            }

            var results = new List<WirelessBatteryDevice>(batteryNodes.Count);
            foreach (var pair in batteryNodes)
            {
                if (!(mains.TryGetValue(pair.Key, out var main) ? main.Connected ?? pair.Value.Connected : pair.Value.Connected)) continue;
                var item = pair.Value;
                var name = mains.TryGetValue(pair.Key, out main) && !string.IsNullOrWhiteSpace(main.Name)
                    ? main.Name
                    : item.Name;
                results.Add(new WirelessBatteryDevice(pair.Key, name, item.Percent, "蓝牙", false));
            }
            results.Sort((left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));
            return Array.AsReadOnly(results.ToArray());
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static bool IsBluetoothInstance(string id) =>
        id.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("BTHLE\\", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("BTHLEDEVICE\\", StringComparison.OrdinalIgnoreCase);

    private static bool IsMainBluetoothNode(string id) =>
        id.StartsWith("BTHENUM\\DEV_", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("BTHLE\\DEV_", StringComparison.OrdinalIgnoreCase);

    private static string GetInstanceId(IntPtr set, ref SpDevinfoData info)
    {
        var buffer = new StringBuilder(512);
        return SetupDiGetDeviceInstanceIdW(set, ref info, buffer, buffer.Capacity, out _) ? buffer.ToString() : string.Empty;
    }

    private static Guid? GetContainerId(IntPtr set, ref SpDevinfoData info)
    {
        if (!TryGetProperty(set, ref info, DeviceContainerIdKey, out var type, out var bytes) || type != DevpropTypeGuid || bytes.Length < 16)
            return null;
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static string ReadStringProperty(IntPtr set, ref SpDevinfoData info, DevPropKey key)
    {
        if (!TryGetProperty(set, ref info, key, out var type, out var bytes) ||
            (type != DevpropTypeString && type != DevpropTypeStringList)) return string.Empty;
        return Encoding.Unicode.GetString(bytes).TrimEnd('\0').Split('\0')[0];
    }

    private static bool TryReadBattery(IntPtr set, ref SpDevinfoData info, out int percent)
    {
        percent = -1;
        if (!TryGetProperty(set, ref info, BluetoothBatteryKey, out var type, out var bytes) ||
            type != DevpropTypeByte || bytes.Length < 1 || bytes[0] > 100) return false;
        percent = bytes[0];
        return true;
    }

    private static bool TryReadConnected(IntPtr set, ref SpDevinfoData info, out bool connected)
    {
        connected = false;
        if (!TryGetProperty(set, ref info, IsConnectedKey, out var type, out var bytes) ||
            type != DevpropTypeBoolean || bytes.Length < 1) return false;
        connected = bytes[0] != 0;
        return true;
    }

    private static bool TryGetProperty(IntPtr set, ref SpDevinfoData info, DevPropKey key, out uint type, out byte[] bytes)
    {
        type = 0;
        bytes = Array.Empty<byte>();
        if (SetupDiGetDevicePropertyW(set, ref info, ref key, out type, null, 0, out var required, 0)) return false;
        var error = Marshal.GetLastWin32Error();
        if (error != ErrorInsufficientBuffer || required == 0 || required > 4096) return false;
        bytes = new byte[checked((int)required)];
        return SetupDiGetDevicePropertyW(set, ref info, ref key, out type, bytes, (uint)bytes.Length, out _, 0);
    }

    private static string StripServiceSuffix(string name)
    {
        string[] suffixes = { " Hands-Free AG", " Hands-Free", " Avrcp Transport", " Stereo", " Audio Gateway" };
        foreach (var suffix in suffixes)
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return name[..^suffix.Length];
        return name;
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
            _statusText = "已停止";
        }
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timer.Dispose();
        _shutdown.Cancel();
    }

    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint ErrorNoMoreItems = 259;
    private const uint DevpropTypeByte = 0x00000003;
    private const uint DevpropTypeString = 0x00000012;
    private const uint DevpropTypeStringList = 0x00002012;
    private const uint DevpropTypeGuid = 0x0000000D;
    private const uint DevpropTypeBoolean = 0x00000011;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    private static readonly DevPropKey FriendlyNameKey = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    private static readonly DevPropKey DeviceDescKey = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 2);

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        internal Guid fmtid;
        internal uint pid;
        internal DevPropKey(Guid fmtid, uint pid) { this.fmtid = fmtid; this.pid = pid; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        internal uint CbSize;
        internal Guid ClassGuid;
        internal uint DevInst;
        internal UIntPtr Reserved;
    }

    private sealed record MainNode(string Name, bool? Connected);
    private sealed record BatteryNode(string Name, int Percent, bool Connected);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SpDevinfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr deviceInfoSet, ref SpDevinfoData deviceInfoData, StringBuilder deviceInstanceId, int deviceInstanceIdSize, out int requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr deviceInfoSet, ref SpDevinfoData deviceInfoData, ref DevPropKey propertyKey, out uint propertyType, byte[]? propertyBuffer, uint propertyBufferSize, out uint requiredSize, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
}

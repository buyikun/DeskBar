# 蓝屏 DRIVER_POWER_STATE_FAILURE (0x9F) 排查报告

- 机器：COMPUTER（Administrator）
- 系统：Windows 11 25H2，内核 10.0.26200.9711（Build 26200，UBR 9711）
- 崩溃时间：2026-11-03 21:11:38
- 转储：`C:\Windows\Minidump\110326-8265-01.dmp`（5,888,932 字节，已提权复制为 `D:\ai\computer\crash-110326.dmp`）
- 取证方式：无 WinDbg 环境，直接按 `_DUMP_HEADER64` / `_TRIAGE_DUMP64` 结构自行解析内核转储；全部结论来自转储内原始结构，非推测。

## 一、结论

**S3 睡眠唤醒后，Intel 蓝牙 USB 设备（VID_8087&PID_0032）所在的 USB 设备栈未能完成系统电源恢复 IRP，电源 IRP 看门狗判定设备对象阻塞超时，触发 0x9F 蓝屏。**

责任设备栈（自 PDO 向上，转储内逐个 DO 解析得到）：

| 层 | 设备对象 | 驱动 | 类型 | StackSize |
|---|---|---|---|---|
| PDO | `0xFFFF95029309FC00` | `\Driver\USBHUB3`（Microsoft USB3 集线器） | 0x22 | 12 |
| filter | `0xFFFF95028DFE1DA0` | `\Driver\ACPI`（Microsoft） | 0x32 | 13 |
| filter | `0xFFFF95028DFDFD00` | `\Driver\hrdevmon`（**火绒安全，第三方，Start=0**） | 0x32 | 14 |
| FDO | `0xFFFF95028BF32D60` | `\Driver\ibtusb`（**Intel 蓝牙 USB，第三方，24.10.0.4**） | 0x32 | 15 |
| FDO | `0xFFFF95028BF3BD60` | `\Driver\BTHUSB`（Microsoft 蓝牙 USB 传输） | 0x41 | 18 |

设备身份（注册表 `HKLM\SYSTEM\CurrentControlSet\Enum\USB`）：

```
USB\VID_8087&PID_0032   Service=BTHUSB   @oem120.inf,%ibt_usb%;英特尔(R) 无线 Bluetooth(R)
```

## 二、转储证据链

### 1. 蓝屏参数（与事件日志 1001 完全一致）

```
0x38 BugCheckCode = 0x0000009F
0x40 P1 = 0x0000000000000003   -> "A device object has been blocking an Irp for too long a time"
0x48 P2 = 0xFFFF95029309FC00   -> Physical Device Object（USBHUB3 的 PDO）
0x50 P3 = 0xFFFF83896E607A00   -> nt!_TRIAGE_9F_POWER
0x58 P4 = 0xFFFF9502A6F828B0   -> 被阻塞的 IRP
```

`_TRIAGE_DUMP64` 位于文件 0x2000，`BrokenDriverOffset = 0`（`KiBugCheckDriver` 未设置，故没有现成的"责任驱动名"，必须自行跟栈）。

### 2. PDO 的 `_DEVICE_OBJECT`（转储内存映射块 [2]，file 0x1E460）

```
Type            = 3            (IO_TYPE_DEVICE_OBJECT)
Size            = 0x178
DriverObject    = 0xFFFF95028D977E10
NextDevice      = 0xFFFF95028DFFEC00
AttachedDevice  = 0xFFFF95028DFE1DA0
DeviceType      = 0x22         (FILE_DEVICE_UNKNOWN，USB 设备 PDO 的典型类型)
StackSize       = 12
```

顺链读 `_DRIVER_OBJECT`：`\Driver\USBHUB3`，`DriverStart = 0xFFFFF8008BE90000`。

### 3. AttachedDevice 链 = 完整蓝牙 USB 设备栈

```
0xFFFF95029309FC00 (USBHUB3)  -> AttachedDevice 0xFFFF95028DFE1DA0 (ACPI)
0xFFFF95028DFE1DA0 (ACPI)     -> AttachedDevice 0xFFFF95028DFDFD00 (hrdevmon)
0xFFFF95028DFDFD00 (hrdevmon) -> AttachedDevice 0xFFFF95028BF32D60 (ibtusb)
0xFFFF95028BF32D60 (ibtusb)   -> AttachedDevice 0xFFFF95028BF3BD60 (BTHUSB)
0xFFFF95028BF3BD60 (BTHUSB)   -> AttachedDevice 0
```

各 `_DRIVER_OBJECT` 的 `DriverName` 直接读出：

```
\Driver\USBHUB3  DriverStart=0xFFFFF8008BE90000
\Driver\ACPI     DriverStart=0xFFFFF800402B0000   <- 调用栈上 0xFFFFF800402B5292 就是 ACPI.sys
\Driver\hrdevmon DriverStart=0xFFFFF80041A50000
\Driver\ibtusb   DriverStart=0xFFFFF80093200000
\Driver\BTHUSB   DriverStart=0xFFFFF80093470000
```

### 4. 被阻塞的 IRP（file 0x208E6，块 [21]）

```
Type            = 6            (IO_TYPE_IRP)
Size            = 0x628        (= 0xD0 header + 19 x 0x48 IO_STACK_LOCATION)
MdlAddress      = 0
Flags           = 0
StackCount      = 0x13 (19)
CurrentLocation = 0x10 (16)
IoStatus.Status = 0            (未完成)
CurrentStackLocation = 0xFFFF9502A6F82DB8 = IRP+0x508 = 栈位置 16
```

栈位置 16 / 17 / 18 的 `_IO_STACK_LOCATION`：

```
loc 16  MJ=0x16 (IRP_MJ_POWER)  MN=0x02 (IRP_MN_SET_POWER)  Ctrl=0xE1
        DeviceObject = 0xFFFF95028DFE1DA0  (ACPI filter DO)      <<< CURRENT
        CompletionRoutine = 0xFFFFF800ADFA16A0 (nt 内)
loc 17  MJ=0x16  MN=0x02  DeviceObject = 0xFFFF95028BF32D60  (ibtusb DO)
loc 18  MJ=0x16  MN=0x02  DeviceObject = 0xFFFF95028BF3BD60  (BTHUSB DO)
```

`Parameters.Power`：

```
SystemContext = 0
Type          = 0   (SystemPowerState)
State         = 1   (PowerSystemWorking -> 唤醒/恢复方向)
ShutdownType  = 0   (PowerActionNone)
```

### 5. BTHUSB 的电源上下文结构（块 [22]，VA 0xFFFF9502ADDD5B40）

该结构内同时持有本案例的全部关键对象，构成闭环：

```
+0x10 = 0xFFFF9502A6F828B0   <- 被阻塞的 IRP
+0x18 = 0xFFFF95029309FC00   <- 蓝屏 Arg2 的 PDO
+0x20 = 0xFFFF95028BF3BD60   <- BTHUSB 的设备对象
+0x28 = 0xFFFF95028DFE1DA0   <- ACPI 设备对象（= IRP 当前位置的设备）
```

## 三、事件日志时间线（System 日志）

```
17:16:52  进入 S3 睡眠（Kernel-Power Id=42 常态，本机长期高频睡眠）
21:10:03  Kernel-General Id=1：系统时间跳变 13981095 ms（与硬件时钟同步）
21:10:04  Power-Troubleshooter Id=1：从低功耗状态恢复
          唤醒源 = Device - AMD USB 3.10 可扩展主机控制器 - 1.20 (Microsoft)
21:10:04  Netwtw14 7018/7021；BTHUSB Id=18（无法存储蓝牙链接密钥）
21:11:09  BTHUSB Id=3  警告：发送到适配器的命令已经超时；适配器没有响应。   <<< 崩溃前唯一异常
21:11:38  蓝屏 0x9F（唤醒后仅 94 秒）
21:14:38  重启完成
21:14:46  WER Id=1001 记录 0x9F 与上述四个参数
```

其余同时段事件均为常态噪音：`Kernel-PnP Id=219`（`ROOT\DISPLAY\0000/0001/0002` 的 `WUDFRd` 加载失败，0xC0000365，每次开机都有）、`SCM Id=7000`（火绒 `HRWSCCtrl` 数字签名校验失败）、`SCM Id=7026`（引导驱动 `dam` 未加载）。

## 四、旁证与排除项

- **不是硬件故障**：全机无任何 `WHEA-Logger` 机器检查异常事件。
- **不是睡眠本身**：S3 睡眠极频繁（10/21–11/3 几乎每天多次，Id=42 常态化），此前从未蓝屏；`Minidump` 目录内仅此一个转储，说明这是**首次 0x9F**。
- **不是第三方虚拟显示器**：Parsec / OrayIddDriver / MuMu 三个虚拟显示适配器只有 attach/detach 且全部 `STATUS_SUCCESS`，无错误事件，设备栈与此案例无关。
- **时间与超时**：唤醒瞬间系统时间前跳 3.88 小时，会令基于时间的电源 IRP 看门狗立刻判定"已超时"，这解释了为什么唤醒后仅 94 秒就报 `Arg1=3`（阻塞超时）而非常见的 10 分钟。
- **相关驱动版本**：
  - `ibtusb.sys` 24.10.0.4（2025-11-18，Intel 官方，第三方）
  - `bthusb.sys` 10.0.26100.8655（Microsoft）
  - `usbhub3.sys` 10.0.26100.1 / `USBXHCI.SYS` 10.0.26100.8972 / `ACPI.sys` 10.0.26100.1（Microsoft）
  - `hrdevmon` 火绒设备监控，`Start=0`（引导启动，第三方）
- **另一处可疑枚举**：`USB\VID_0000&PID_0002  Unknown USB Device (Device Descriptor Request Failed)` —— 存在一个 USB 设备描述符请求失败的记录，与 USB 链路稳定性相关，建议一并留意。

## 五、处置方案（按优先级）

1. **更新 Intel 蓝牙驱动**（首要）
   当前 `ibtusb.sys` 24.10.0.4。到 Intel 官网 / 主板厂商页面取最新"Intel 无线 Bluetooth"驱动重新安装；若已是最新，则**回滚到上一个版本**或改用 Microsoft 通用蓝牙驱动测试。装完重启并观察。

2. **更新芯片组 / USB 控制器驱动与 BIOS**
   唤醒源是 `AMD USB 3.10 可扩展主机控制器`。安装 AMD 官方芯片组驱动（含 USB 过滤器与 xHCI 驱动），并检查主板 BIOS 是否为新版（涉及 ACPI / S3 恢复路径）。

3. **关闭快速启动**（当前 `HiberbootEnabled = 1`）
   ```powershell
   powercfg /hibernate on
   reg add "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power" /v HiberbootEnabled /t REG_DWORD /d 0 /f
   ```
   或在"控制面板 → 电源选项 → 选择电源按钮的功能 → 更改当前不可用的设置"中取消勾选"启用快速启动"。

4. **隔离火绒的 USB 过滤驱动**
   `hrdevmon` 以 `Start=0` 引导加载并挂在本蓝牙设备栈上。作为验证手段：临时退出/卸载火绒 → 反复休眠唤醒 20 次以上，观察是否复现。若不再复现即可锁定为叠加因素。

5. **USB 电源管理逐项排查**
   设备管理器中对该 Intel 蓝牙设备及其上游"USB 根集线器 (USB 3.0)"：取消勾选"允许计算机关闭此设备以节约电源"，并在"电源管理"里关闭"允许此设备唤醒计算机"，做对照测试。

6. **可选：暂时禁用蓝牙验证因果**
   若以上仍复现，禁用蓝牙适配器后连续睡眠唤醒测试；不复现即确认蓝牙 USB 链路是触发点。

7. **更新 Intel 蓝牙固件**
   通过 Intel 驱动支持助手或主板厂商工具检查蓝牙模块固件更新。

## 六、复现判别方法

蓝屏若再次发生，直接确认 `C:\Windows\Minidump` 是否出现新转储；若有，检查 `WER Id=1001` 的参数 `P2`（PDO）是否仍为 `USBHUB3` 的 PDO 且 `P4` 指向的 IRP 仍为 `IRP_MJ_POWER / IRP_MN_SET_POWER`。若相同，则说明第 1–4 步中某一项尚未解决。

## 附：本次取证产物

- `D:\ai\computer\crash-110326.dmp` —— 提权复制出的内核转储副本
- `D:\ai\computer\parse9.ps1` ~ `parse13.ps1` —— 转储解析脚本（头部 / TRIAGE / 驱动表 / 字符串池 / 内存映射表 / 设备栈 / IRP 栈）
- `D:\ai\computer\elevate-copy.ps1` —— 提权复制脚本（UAC）

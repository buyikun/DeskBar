# DeskBar 开发者与 AI 协作规范 (AGENTS.md)

> 本文件是 DeskBar 的**单一事实来源**。只写「会让人改错」的内容，不重复能从代码直接看出的事实。
> 凡与旧注释、旧文档、旧提交说明冲突，一律以本文件 + 本机实测为准。最近校准：2026-11-08。

---

## 1. 技术栈与运行基准

### 1.1 框架与工具链
- **.NET 8.0**，`net8.0-windows10.0.19041.0`，C# 12，WPF 为主；WinForms 仅用于 `TrayIconHost` 的 `NotifyIcon`。
- **自包含专用 SDK**：`D:\ai\deskbar\.dotnet\dotnet.exe`（8.0.427）。**禁止依赖全局 dotnet**——`build.ps1` / `publish.ps1` 已把 `TEMP`、`TMP`、`APPDATA`、`LOCALAPPDATA`、`USERPROFILE`、`DOTNET_CLI_HOME`、`DOTNET_ROOT`、`NUGET_PACKAGES`、`PATH` 共 9 项重定向到仓库内隐藏目录。
- **DPI**：`app.manifest` 声明 `permonitorv2`。构建时恒定出现一条 `WFAC010` 警告（要求改用 `ApplicationHighDpiMode`），属于既存项，**不要为了让警告消失而改动 DPI 模式**。
- **隐式 using 例外**：`UseWindowsForms` 会注入 `System.Drawing` / `System.Windows.Forms`，与 WPF 同名类型（`Color`/`Brush`/`Point`/`ComboBox`…）冲突。`DeskBar.csproj` 用 `<Using Remove=...>` 全局移除，**不要删掉这两行**，否则会出现大范围类型歧义。

### 1.2 依赖红线：零 NuGet
- `src/DeskBar/obj/project.assets.json` 中 `"libraries": {}` —— 项目**没有任何 PackageReference**，只引用 `Microsoft.NETCore.App`、`Microsoft.WindowsDesktop.App`、`Microsoft.Windows.SDK.NET.Ref` 三个框架。
- **新增任何 NuGet 包前，必须在对话里说明理由并取得用户同意。** 这是体积与内存预算的硬约束，不是风格偏好。
- 原生依赖仅 4 个 DLL，位于 `src/DeskBar/NativeBinaries/`：
  - **`ExplorerTAP.dll`** — 核心组件。注入 `explorer.exe`，通过 COM 接口 `ITaskbarAppearanceService`（IID `5bcf9150-c28a-4ef2-913c-4c3ea2f5ead0`）直接控制 Win11 任务栏 XAML 外观（亚克力 / 透明 / 模糊）。任务栏透明与居中止岛**全部依赖它**。
  - `msvcp140.dll`、`vcruntime140.dll`、`vcruntime140_1.dll` — 前者所需的 VC++ 运行库。
- `csproj` 用 `<Link>%(FileName)%(Extension)</Link>` 把上述 DLL **平铺**到输出根目录。**这条规则不可改成保留子目录**，否则 `LoadLibrary("ExplorerTAP.dll")` 的相对路径直接失效。

### 1.3 原生互操作面
| 能力 | 实现要点 |
| --- | --- |
| 窗口 / 区域 | `user32`、`gdi32`：`SetWindowPos`、`SetWindowRgn`+`CombineRgn`、`FindWindowW("Shell_TrayWnd")`、`SetWindowLongPtr(GWLP_HWNDPARENT=-8)` |
| DWM | `dwmapi`：`DWMWA_BORDER_COLOR(34)` 剥掉 1px 边框、`DWMSBT_NONE` 先关系统材质、`DWMWA_USE_IMMERSIVE_DARK_MODE` 深色标题栏 |
| 系统媒体 | WinRT `Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager`（GSMTC） |
| 任务栏几何 | UIA 主路径（`Taskbar.TaskbarFrameAutomationPeer`）+ Win32 兜底（`Start` / `ReBarWindow32` / `MSTaskSwWClass` / `TrayNotifyWnd`） |
| 硬件指标 | `ntdll!NtQuerySystemInformation` 单次内核遍历 + `pdh.dll` 裸 P/Invoke |
| 桌面图标 | `SHELLDLL_DefView` → `SysListView32`（`LVM_HITTEST` 排除图标命中） |
| 全局鼠标 | `WH_MOUSE_LL` 低级钩子，装在独立 STA 线程 + 原生消息泵 |

### 1.4 资源预算（别突破）
- 常驻工作集目标 **15–30 MB**，CPU **0.0%–0.1%**，常态**零磁盘写入**。
- 内存靠 `EmptyWorkingSet` + `GC.Collect` 周期维护（启动后一次，之后每 2 分钟一次）。
- 任何「为极小概率场景预建缓存 / 常驻轮询 / 后台线程」的改动都直接侵蚀这条预算，必须先论证。

---

## 2. 目录与架构分工

### 2.1 路径约定
- 源码根：`D:\ai\deskbar\`，唯一工程 `src/DeskBar/DeskBar.csproj`（无 `.sln`）。
- 发布产物：`D:\ai\deskbar\out_publish\`。
- 正式部署目录：`D:\app\DeskBar\`（含 `DeskBar.exe`、`ExplorerTAP.dll`、`logs\`）。
- 用户配置：软件运行目录 `AppContext.BaseDirectory\settings.json`（即 `D:\app\DeskBar\settings.json`，启动时自动兼容迁移旧 `%APPDATA%\DeskBar\settings.json`）。
- 运行日志：`AppContext.BaseDirectory\logs\`，即 `D:\app\DeskBar\logs\`。

### 2.2 源码分层（谁负责什么）
| 层 | 内容 |
| --- | --- |
| `App.xaml.cs`（~640 行） | 启动编排：单实例互斥体 + 唤醒事件、主题装配、`SaveSettings()` 运行时同步、owner 自愈巡检、内存整理。**所有全局副作用的入口** |
| `Views/` | 四大浮岛窗口 + 设置窗 + 监控详情窗 + 托盘宿主 |
| `Services/` | 23 个服务，见下 |
| `Interop/` | `NativeMethods.cs`（P/Invoke 声明）、`DesktopWindows.cs`（桌面窗口发现） |
| `Models/` | `ProcessMetricItem` |
| `Themes/` | `Tokens.xaml`（**只有非颜色令牌**：字号、圆角）+ `MenuStyles.xaml`（右键菜单样式） |

**关键颜色不在 XAML 里**。颜色全部由代码下达：`TonePalette.cs`（描边/悬停/面板/文字/菜单）与 `App.ApplyAppTheme()`（设置窗界面底色）。改颜色请改这两处，不要在 XAML 里写死。

`Services/` 分工：
- **任务栏外观**：`TaskbarService`（几何扫描、区域裁剪、缓动）、`NativeTaskbarAppearanceService`（ExplorerTAP 注入 + COM 外观）、`AcrylicWindowHelper`（窗口亚克力、owner 绑定、Z 序）、`TonePalette`
- **主题**：`ThemeService`（状态 + 写注册表）、`ThemeSchedule`（纯函数的时段计算）
- **媒体**：`MediaSessionService`（GSMTC 会话与封面）、`MediaSourceRouter`（选源打分）、`MediaWhitelist`（白名单解析）、`LyricService`（歌词调度）、`MediaCacheLocator`（播放器目录发现）、`KrcDecoder`、`QrcDecoder`、`SafeArtwork`、`PlayerWindowControl`
- **硬件**：`SystemMonitorService`、`ProcessListMerger`
- **系统集成**：`StartupService`（HKCU Run）、`DesktopIconService`、`DoubleClickToggle`、`AppIconHelper`、`Log`、`AppSettings`

### 2.3 四浮岛模型
1. **左：系统监控浮岛** `SystemMonitorCapsuleWindow` — CPU/GPU/内存/上下行网速；点击展开 `SystemMonitorDetailWindow`（Top5 进程）。
2. **中：应用区描边** `IslandBorderWindow(Middle)` — 贴住任务栏运行应用组，纯描边、鼠标 100% 穿透。
3. **右：音乐胶囊** `MusicCapsuleWindow` — 封面、歌名歌手、逐字歌词、上一曲/暂停/下一曲。
4. **托盘区描边** `IslandBorderWindow(Tray)` — 框选托盘通知图标群。

### 2.4 三种融合模式
| 模式 | 行为 |
| --- | --- |
| `island_acrylic`（默认） | 用 `CombineRgn`+`SetWindowRgn` 把任务栏挖成悬浮岛；浮岛自带独立亚克力。**前提是 ExplorerTAP 注入成功**，否则自动降级为「只监视几何、不裁任务栏」 |
| `acrylic` | 整条任务栏毛玻璃；浮岛走 `flat-on-taskbar`（`AccentState=0`），不挖洞、不叠模糊层 |
| `clear` | 整条完全透明 |

`island_clear` 已在配置归一化时合并进 `island_acrylic`，UI 早已移除，**`TaskbarService.ApplyStyle` 里那个 `case "island_clear"` 是死分支，不要当作可用模式引用**。

### 2.5 音色（tone）与昼夜方案
- 合法 tone 只有 5 个：`light`、`dark`、`natural`、`softmist`、`sheertights`（见 `AppSettings.IsKnownTone`）。
- 深浅判定：`sheertights` / `dark` = 深色底；`softmist` / `light` = 浅色底；`natural` = 跟随系统。
- 任务栏着色：`softmist=0x25525252`、`sheertights=0x25323232`、`light=0xA0FFFFFF`、`dark=0x90202020`、`natural=0x00000000`。
- **渲染路径一律读 `App.Settings.EffectiveTone`，禁止直接读 `AcrylicTone`**。自动主题按时段把结果写进 `RuntimeTone`（`[JsonIgnore]`，不落盘），`EffectiveTone => RuntimeTone ?? AcrylicTone`。读错会让描边比底色慢一拍。
- 昼夜方案是显式字段（`DayThemeLight`/`NightAcrylicTone` 等），不再是「读系统深浅色反推」。

---

## 3. 构建、运行与验证命令

```powershell
# 1) 编译
powershell -NoProfile -ExecutionPolicy Bypass -File D:/ai/deskbar/build.ps1
# 2) 发布（会先清空 out_publish）
powershell -NoProfile -ExecutionPolicy Bypass -File D:/ai/deskbar/publish.ps1
# 3) 部署并启动（杀旧进程 → 清日志 → 复制 → 拉起 → 打印 PID/WS）
cd /d/ai/deskbar && bash deploy.sh
```

**⚠️ 两个 `.ps1` 都不传播退出码**（脚本内无 `exit`，`powershell -File` 外层恒返回 0）。判断成败只能靠：
- 读 `build_diag.log` 末尾（成功会有 `已成功生成。`，失败是 `1 个错误` 之类）；
- 或查 `$LASTEXITCODE`。

**完整链条固定为 `build.ps1` → `publish.ps1` → `deploy.sh`**。只 build 不 publish 会让 `out_publish\` 停留在旧版，制造「改了却没生效」的假象。

**部署脚本只认 `deploy.sh`**。`deploy.ps1` / `deploy2.ps1` 是历史遗留，行为互不一致（前者不跳过 `ExplorerTAP.dll` 且用 `-ErrorAction Continue` 静默吞掉覆盖失败）。**不要用它们，也不要「顺手统一」**。

### 辅助工具
```powershell
# 后端改动后，检查设置 UI 是否需要跟进（只检测、不改源码）
powershell -NoProfile -ExecutionPolicy Bypass -File D:/ai/deskbar/sync-frontend.ps1
# 重置同步基线
powershell -NoProfile -ExecutionPolicy Bypass -File D:/ai/deskbar/sync-frontend.ps1 -Init

# 窗口枚举：pid / hwnd / 可见性 / 矩形 / 类名 / 标题
python scripts/winlist.py [pid]
# 闪烁检测：抓 50 帧只测描边区域灰度，避开文字刷新造成的假阳性
python scripts/flick7.py mon|tray
# 任务栏截图
powershell -NoProfile -ExecutionPolicy Bypass -File shot_taskbar.ps1
```

### 测试现状（别高估）
- **没有单元测试框架**（无 xunit / MSTest / NUnit），没有 CI，没有断言式测试。
- `tests/TestOwnedApp.cs`、`tests/TestRecreateApp.cs` 是**各自带 `Main` 的独立控制台程序**，验证 `SetWindowLongPtr(-8)` 设 owner 后 WPF `Closed` 行为；无 `.csproj`、无脚本引用，需手动 `csc` 编译，**不是测试工程**。
- 根目录 87 个 `.ps1`、`scripts/` 下 227 个 `.py` 是历史探针（截图比对、窗口探测、崩溃转储解析、补丁生成）。**禁止当产品代码重构、合并或清理**——它们是这轮调试的证据链。
- 验收标准 = `build` + `publish` + `deploy` 全绿，再用 `winlist.py` / `flick7.py` / 截图比对确认行为。

---

## 4. 避坑指南（踩过的坑与禁忌）

### 4.1 编辑安全（最高优先级）
- **禁止用跨行正则（`re.DOTALL` / `re.S` / 跨行非贪婪匹配）批量改 C# 源码**。曾因贪婪匹配把 `MusicCapsuleWindow.xaml.cs` 整块 110 行切掉。
- 替换必须用**含上下文的精确唯一字面量**，并在替换前断言命中数为 1。
- **保持原行尾**：仓库内 `.cs` / `.xaml` 普遍是 CRLF，改前检测，写回沿用，禁止混入 LF。
- **编码保护**：`AppSettings.cs` 等文件含中文 XML 注释，禁用外部重格式化工具整体覆盖。
- **`.ps1` 脚本禁止出现任何非 ASCII 字符**（含中文注释）。Windows PowerShell 5.1 对无 BOM 的 `.ps1` 按 OEM/ANSI 解码，中文会直接 `ParserError`。`sync-frontend.ps1` 文件头就写死了这条约束。

### 4.2 任务栏裁剪与 Z 序
- **`SetWindowPos(hwnd, X, ...)` 是把 `hwnd` 插到 `X` 的正下方**（即 `X` 盖在上面）。要放到任务栏上方，得插到「任务栏上方那个窗口」的正下方——用 `AcrylicWindowHelper.TaskbarLayerAnchor()` 取锚点。
- **移动浮岛只许** `SetWindowPos(hwnd, IntPtr.Zero, ..., SWP_NOACTIVATE | SWP_NOZORDER)`；Z 序交给 `KeepAboveTaskbar` 单独处理。传 `HWND_TOPMOST` 会让锚点可能返回自身或已销毁句柄，整次调用失效且**位置不生效**。
- **层级靠 owner，不靠 TOPMOST**：用 `SetWindowLongPtr(GWLP_HWNDPARENT)` 把浮岛 owner 绑到 `Shell_TrayWnd`，由内核保证 owned 窗口恒在 owner 之上。`App` 每 2 秒巡检补绑（`EnsureBoundToTaskbar`）。任何新增的周期性 topmost 都会与托盘弹出菜单（音量/网络/日历）抢置顶，表现为**剧烈频闪**。
- **`KeepAboveTaskbar` 一旦判定已在任务栏上方就必须什么都不做** —— 别改成「每次都抬」。
- **`RaiseAboveTaskbarIfCovered` 目前全仓无调用方**，是遗留路径。想复用它来「压过截图工具遮罩」是无效的：**代码里从来没有实现截图遮罩检测**，只有两处注释描述。真正的保护是「与任务栏同层」的锚点关系——截图遮罩作为顶层窗口天然压在浮岛之上。**若确实要实现，得在 `KeepAboveTaskbar` 里新写逐层检索，不要依赖现有注释。**
- **裁剪任务栏前必须同时校验两点**：`NativeTaskbarAppearanceService.IsAvailable` 为真，且边界值非负。缺任一条都会把开始区 / 搜索框 / 时钟区整块挖掉，且没有磨砂层补偿——用户看到的是「任务栏功能全废、点空白处穿透」。注入失败时走降级分支（只监视几何、一像素不动）。
- **周期性几何重排必须先判「未变即 return」**，并配 `SWP_NOOWNERZORDER`。

### 4.3 任务栏几何与 UIA 防抖
- 任务栏句柄**每轮都要重新 `FindWindowW("Shell_TrayWnd")`** —— explorer 重启会换句柄，缓存句柄必然失效。
- 抖动是常态，必须三层过滤：**死区（收缩 8px / 扩张 2px）+ 连续确认 + 收缩额外 160ms 延迟**。
- **扩张判定必须是两侧同时向外**（`desiredC1 < _targetC1 && desiredC2 > _targetC2`）。单侧 1px 漂移走无死区快通道，就会把 UIA 动画抖动放大成任务栏边界抽搐。
- **按住鼠标左键期间必须完全冻结重排**（`GetAsyncKeyState(VK_LBUTTON) & 0x8000`），松手后再同步最终位置。否则拖拽托盘图标时几何会打架。
- **隐藏浮岛必须 ≥900ms 时长门控**。托盘图标重排瞬间 `GetWindowRect` 偶发返回高度为 0，单帧命中就 `Hide`，表现为瞬时闪烁消失。
- 描边窗口必须在 `SourceInitialized` 里附加 `WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` 实现点击穿透；首帧先放到 `-32000` 算好几何再显示，避免闪一下。

### 4.4 外观引擎与降级
- **描边色由 `TonePalette` 按 tone 下发，不在 XAML 里写死**：深色底 `0x40FFFFFF`、浅色底 `0x33000000`，都是 1px（`BorderThickness="1"`）。旧文档所说的「统一 `#38000000`、统一 `CornerRadius=9`」**已过时**——实际圆角按组件各不相同（监控浮岛 `9`、音乐胶囊 `10`、描边层 `24`），改动前先看对应 XAML。
- 各窗口必须在 `OnSourceInitialized` 里调 `AcrylicWindowHelper.RemoveWindowBorder(this)`（`DWMWA_BORDER_COLOR(34)`），否则 DWM 会额外叠一圈 1px 高光边框。
- **注入失败与管理员权限无关**。Explorer 与 DeskBar 同为 Medium 完整性级别，同级注入本就允许。**「必须管理员权限才能接管任务栏外观」是被本机实测证伪的历史断言**（日志里 `elevated=False` 但注入成功、`0x80070005` 出现 0 次、计划任务根本不存在）。真实失败原因是 `ExplorerTAP.dll` 缺失、版本不匹配，或 Explorer 正在重启。**任何权限相关文案不要回退到旧说法。**
- 注入失败后 **30 秒内不再重试**。任务栏几何扫描是 100ms 级循环，每次真去注入会把日志刷爆并拖慢扫描。
- `SetTaskbarBlur` 不一定存在（导出集合随版本变化），**必须先探测再用**；调用不存在的导出只返回失败、不抛异常，光看返回值会静默丢失外观。
- 新增 tone 必须**同时改三处**：`AppSettings.IsKnownTone`、`NativeTaskbarAppearanceService.GetColorForTone`、`TonePalette.IsDark`。漏一处颜色就静默退化。
- 已知不一致：`GetColorForTone` 保留了 `"auto"` 分支，但 `IsKnownTone` 不含 `auto`。`auto` 只在 `Normalize()` 里被迁移成软雾/黑丝。**不要新增 `auto` 用法**。

### 4.5 主题与设置的初始化顺序（极易踩）
- `ThemeService` 的**每个属性 setter 都会立刻触发 `CheckTimeAndApply()`**。直接顺序赋值会让第一个 `LightStart` 用「半初始化配置」（默认 07:00/19:00 + 默认 tone + 改系统主题=true）抢先写注册表并广播 `WM_SETTINGCHANGE`。
  **铁律：先 `AutoTimeEnabled = false` → 批量赋值 → 再按用户设置恢复。** 启动路径与 `SaveSettings()` 路径共两处，改一处必须同步另一处。
- `AutoTimeEnabled` 的 setter 在订阅 `PlanApplied` 之前就写了 `_appliedPlan`，导致后续「方案未变化」短路、事件永不触发。启动末尾必须显式补一次 `ApplyToneForCurrentPlan(force: true)`。
- **新增设置项要同步四处**：`AppSettings` 字段 + 默认值 → XAML `x:Name` + 事件 → `LoadFromSettings()` 赋值 → 事件处理器里带 `if (_loading) return;` 守卫再 `Persist()`。漏掉 `_loading` 守卫，载入期就会把用户配置回写成默认值。
- 任何 `ComboBox` 选项必须带 `Tag`，且值要出现在 `IsKnownTone` / `Normalize` 白名单里，否则启动时静默回落。
- 设置**没有防抖、也不是原子写**（直接 `File.WriteAllText`）。滑块拖动、白名单逐字输入都会逐次落盘。新增高频控件前先确认写盘频率。
- 改 tone 的 alpha 值，必须同步更新下拉项里的「xx% 覆盖」文案与色块 ToolTip（这些是硬编码文本）。
- `RuntimeTone` 是 `[JsonIgnore]` 运行时态。**误加持久化会让用户手选的色调被时段值覆盖。**
- 关闭设置窗是 `e.Cancel = true` + `Hide()`，**不是退出进程**。

### 4.6 媒体与歌词子系统
- **播放控制只走 GSMTC 的 `TrySkipPreviousAsync` / `TryTogglePlayPauseAsync` / `TrySkipNextAsync`**，不得改成模拟按键或窗口消息。注意：当前 UI **丢弃返回的 bool**，所以「点了没反应」是已知现状，不是回归 bug——别为此加弹出提示，除非用户明确要求。
- **歌词匹配不得放宽「候选唯一」判据**。歧义时宁可无歌词，也不要串到翻唱/同名版本。
- **QRC 的 DES 位序与 KRC 的 16 字节常量是格式常量**，换成标准 TripleDES / 标准 XOR 一定解不出。
- **封面只能经 `SafeArtwork.DownloadAsync`**：强制 https、拒私网与 DNS 重绑定、重定向 ≤3 跳、体积 ≤1MB、魔数验真类型。封面 URL 来自第三方接口，属不可信输入，绕过就是 SSRF 面。
- **禁止为封面新建磁盘缓存目录**。现有设计是实时下载 → 内存解码 → `Freeze()` → 交 GC，保障零额外磁盘 I/O。
- **位图跨线程前必须 `Freeze()`**；后台回到 UI 必须 `Dispatcher.CheckAccess()` / `BeginInvoke`。
- 定时器（16ms 滚动、16ms ZGuard、50ms 跟随、120ms 悬停、400ms 定位、100ms 歌词）**在 `Closed` 里必须全部 `Stop()` 并解绑事件**，否则向已 Close 的窗口 `Show()` 会抛 `InvalidOperationException`。
- `NOACTIVATE` 窗口下 `MouseEnter` / `MouseLeave` 不可靠，悬停只能轮询光标坐标。
- 文本宽度有两个反向陷阱：`TxtArtist` 必须设 `Width = NaN` 交给 `ScrollViewer` 测量；而行级 LRC 的 `TxtLyricPlayed` 反而必须设自然宽度（`NaN` 会被钳到 164px）。
- 右键菜单需临时摘掉 `NOACTIVATE` 抢前台，关闭后还原并 `PostMessage(WM_NULL)` 归还前台。
- **歌词对象缓存是纯内存 `ConcurrentDictionary`，无 TTL、无容量上限**（`LyricService`）。长会话会随曲目数线性增长——这是已知设计取舍，属于内存而非磁盘，不违反「拒绝磁盘缓存」原则。

### 4.7 硬件监控采集
- 采样由 **`DispatcherTimer` 在 UI 线程每 1000ms 驱动**，当前没有后台线程。
- **禁止引入 `PerformanceCounter` / WMI / `nvidia-smi` / NVML / DXGI**。指标全部走已有通道：
  - CPU 与 Top5 进程：`NtQuerySystemInformation(SystemProcessInformation)` 单次遍历，失败回退 `GetSystemTimes`
  - 内存：`GlobalMemoryStatusEx`
  - 网速：缓存 `NetworkInterface[]` + `IPv4Statistics` 字节差 / `TickCount64`
  - GPU 与磁盘：**单条 PDH query**，`\GPU Engine(*)\Utilization Percentage`、`\PhysicalDisk(_Total)\Disk Read|Write Bytes/sec`
- **PDH 计数器必须用 `PdhAddEnglishCounterW`**。换回 `PdhAddCounterW`，中文系统上找不到 `\GPU 引擎`，指标直接消失。
- PDH handle 全程复用、`_pdhTried` 门闩保证只开一次，仅在 `Dispose` 里 `PdhCloseQuery`。单项失败只禁用该项，不影响其余。
- `ReadGpuPercent` 的 `AllocHGlobal` 必须配 `FreeHGlobal`，两段式调用以 `PDH_MORE_DATA (0x800007D2)` 为判据。
- `NtQuerySystemInformation` 的结构体偏移是**硬编码**的（UserTime@40、KernelTime@48、PID@80、WorkingSet@144）；缓冲区 1MB 起，遇 `STATUS_INFO_LENGTH_MISMATCH` 翻倍，上限 32MB。
- **若将来把采样挪到后台线程**：详情窗用的是同步 `Dispatcher.Invoke`（会卡 UI），胶囊用的是 `BeginInvoke`。必须先统一成 `BeginInvoke`。
- GPU 不可用时（`GpuPercent = -1`）→ 详情窗显示 `N/A`，胶囊直接 `Collapsed` 隐藏该项。不要改成显示 0%。

### 4.8 部署与诊断
- **部署必须跳过 `ExplorerTAP.dll`**：它已被注入 `explorer.exe`，文件被占用，复制必然失败。代价是**更新该 DLL 时不会自动同步**，需手动替换且先重启 explorer。
- `deploy.sh` 会**清空 `D:\app\DeskBar\logs\*`**。这是有意为之（每次干净启动便于诊断），但意味着：**部署后就看不到上一次运行的日志了**。排查崩溃请先取日志、再部署。
- 日志默认**静默**（`EnableDebugLog=false` 时只有 Error 写入 `crash.log`，超过 512KB 直接删除，不轮转）。排查问题必须先到诊断页打开调试日志、再复现。
- 单实例用 `Local\DeskBar_SingleInstance` 互斥体 + `Local\DeskBar_WakeEvent` 唤醒事件。提权实例创建的 `Local\` 互斥体普通进程打不开，因此**必须捕获 `UnauthorizedAccessException` 并让位退出**，不能让它崩。
- 副实例退出必须走 `Environment.Exit(0)`，**绝不能执行 `OnExit` 的清理逻辑**——否则会把主实例的任务栏区域与托盘状态一并破坏。
- 浮岛自愈靠 `IsWindow(h)` 判定（句柄曾存在但现在失效才是真死），并**限速 5 秒一次全量重建**。未 Show / Handle 尚未初始化的窗口绝不是 dead，判错会无限重构。
- `WM_TASKBARCREATED` 只注册、未消费；真正的自愈走上面的 2 秒轮询。不要以为挂了消息处理。

---

## 5. AI 协作行为准则

1. **中文思考与回复**。推理过程与最终答复一律中文；代码、命令、路径、标识符、报错原文按原样保留。
2. **先读后改**。动手前必须读目标文件的确切行号、签名与上下文，禁止凭推测下补丁。
3. **闭环验证**。每次改动后跑 `build.ps1` → `publish.ps1`；涉及 UI / 任务栏必须 `deploy.sh` 并用探针或截图确认。**判定成败看 `build_diag.log`，不看退出码。**
4. **禁止代劳重启宿主**。不得声称「正在重启 DSH」，不写延时脚本或计划任务。需要外部重启生效的变更，交给用户手动操作并附验证清单。
5. **保持极致精简**。不引入与意图无关的依赖，不过度封装，不加常驻轮询。守护 15–30MB 内存与零磁盘写入。
6. **不清理历史探针**。根目录与 `scripts/` 下的 `.ps1` / `.py` 是调试证据链，不是垃圾。
7. **过时断言要当场纠正**。若发现注释、文案、旧文档与实测结论冲突（例如残留的「需要管理员权限」说法），以本文件为准，并在改动时顺手修正，不要沿用。

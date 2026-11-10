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
- **更新**：`UpdateService`（GitHub Releases latest 接口 + 加速前缀改写）。检查只在用户点击时发起，不做常驻轮询；开启自动检查时启动 8 秒后静默查一次，发现新版本只走托盘气泡、不弹窗。下载地址由 `UpdateAccelPrefix` 前缀改写，留空即直连 `https://github.com`；`api.github.com` 的 API 请求**不**经过加速站。

### 2.3 四浮岛模型
1. **左：系统监控浮岛** `SystemMonitorCapsuleWindow` — CPU/GPU/内存/上下行网速；点击展开 `SystemMonitorDetailWindow`（Top5 进程）。
2. **中：应用区描边** `IslandBorderWindow(Middle)` — 贴住任务栏运行应用组，纯描边、鼠标 100% 穿透。
3. **右：音乐胶囊** `MusicCapsuleWindow` — 封面、歌名歌手、逐字歌词、上一曲/暂停/下一曲。
4. **托盘区描边** `IslandBorderWindow(Tray)` — 框选托盘通知图标群。

### 2.4 三种融合模式
| 模式 | 行为 |
| --- | --- |
| `island_acrylic`（默认） | 用 `CombineRgn`+`SetWindowRgn` 把任务栏挖成悬浮岛；浮岛自带独立亚克力。**前提是 ExplorerTAP 注入成功**，否则自动降级为「只监视几何、不裁任务栏」 |
| `acrylic` | 整条任务栏毛玻璃；浮岛不挖洞、不叠模糊层。**旧说法「浮岛走 `flat-on-taskbar`（`AccentState=0`）」已于 2026-11-11 废弃**，见下方浮岛透明度那条 |
| `clear` | 整条完全透明（**但浮岛不是**，见下条） |

`island_clear` 已在配置归一化时合并进 `island_acrylic`，UI 早已移除，**`TaskbarService.ApplyStyle` 里那个 `case "island_clear"` 是死分支，不要当作可用模式引用**。

**两条不可违背的设计原则（2026-11-11 用户明确要求，任何外观改动先对照这两条）**：
1. **任务栏是一个整体，任何部分都不能有割裂感**：浮岛、描边、任务栏本体的材质/透明度/颜色必须读作同一层，不能出现「这里比旁边深一块 / 透一截」。
2. **浮岛的层面就是任务栏层面**：浮岛不是「盖在任务栏上的另一个东西」，它就是任务栏的一部分 —— 所以它**不给自己定独立材质**，而是让下面的任务栏透上来。
   - `island_acrylic` 是唯一例外：任务栏被挖洞，浮岛那一块**已经不在任务栏上**（底下是壁纸），所以必须自带 tone 材质（`AcrylicWindowHelper` 里只有 `style == "island_acrylic"` 才下发 `GradientColor`）。
   - `acrylic` / `clear` 下浮岛**真透明**（跳过 accent + `AllowsTransparency=True`）→ 露出它下面的任务栏本身 → 天然同层同貌。**这是「整条模式跳过 accent」的根据，不要理解成「省掉一层模糊」。**
   - **两个浮岛的根 Border 都必须铺一层 alpha=1 的 `#01000000`**（监控浮岛写在 `SystemMonitorCapsuleWindow.xaml:33`，音乐胶囊写在 `MusicCapsuleWindow.xaml:84` 并由 `MusicCapsuleWindow.xaml.cs:591` 的 `RootBorder.Background = HitTestBackdrop;` 兜住）。肉眼看不见，**但它决定鼠标能不能点到**：整条模式下浮岛是 `WS_EX_LAYERED` 分层窗口，**alpha=0 的像素不参与命中测试**，`Brushes.Transparent` 会让整块空白区穿透到下面的任务栏（右键弹出的是任务栏菜单）。2026-11-11 用户报「右键音乐组件框内部分会直接穿透到任务栏」就是这么来的。**不要为了「彻底透明」把它改回 `Transparent`。**
   - 任何「给浮岛单独设一个更好看的底色 / 磨砂度 / 阴影」的改动都违反第 1 条。

- **浮岛在整条模式（`acrylic` / `clear`）下是**真透明**，这是 2026-11-11 实现的行为**：两个浮岛窗口的 `AllowsTransparency` 由 `TaskbarStyle` 决定（`src/DeskBar/Views/SystemMonitorCapsuleWindow.xaml.cs:50`、`src/DeskBar/Views/MusicCapsuleWindow.xaml.cs:84`：`AllowsTransparency = !AcrylicWindowHelper.IsIslandStyle();`）——岛模式 `false`（否则 accent 亚克力材质失效），整条模式 `true`（WPF 真透明）。同时 `src/DeskBar/Services/AcrylicWindowHelper.cs:35-39` 与 `src/DeskBar/Views/MusicCapsuleWindow.xaml.cs` 的 `ApplyAccent` 都在整条模式下**不调 accent 直接返回**（日志打出 `accent style=<style> transparent-skip`）。任务栏之所以能全透明，是因为它是 explorer 自己的窗口被设成全透明，机制与浮岛无关。
  - **`AllowsTransparency` 不能在运行时切换**，所以切风格必须整窗重建：收口在 `src/DeskBar/Views/SettingsWindow.xaml.cs:480` 的 `RecreateWidgetWindows()`（**不是** `RefreshWidgetBackgrounds()`，后者只刷背景，浮岛会停在旧的分层形态）。实测从改 ComboBox 到新窗口带材质出现约 424ms（日志 `recreating widget windows...` → `widget windows recreated successfully`），进程未重启。
  - **两条 ctypes 实测教训（别再踩）**：① 对 `AllowsTransparency=True` 的浮岛再叠 accent，窗口会**整块画不出来**（设 `AccentState=4` + `DwmExtendFrameIntoClientArea` 后截图只剩桌面）；② `AllowsTransparency=False` 的窗口，`AccentState` 0/1/2/3/4 **没有一档能真透明** —— 0 与 1 是纯黑板 `(98,98,98)`、2 是偏亮的浅灰蓝雾 `(207,220,230)`、3/4 是暖灰白雾 `(206,196,191)`。
  - **例外：进程详情窗不透明**。`src/DeskBar/Views/SystemMonitorDetailWindow.xaml.cs:104` 传 `forceMaterial: true` 调 `AcrylicWindowHelper.Apply` —— 它是点开才出现的浮层面板、不是贴在任务栏上的常驻浮岛，真透明会让文字压在壁纸上看不清。`Apply` 的 `forceMaterial` 参数就是为它加的。
  - **内存不是这次的负担**：2026-11-11 实测 clear + 分层（`AllowsTransparency=True`）在 20–200 秒内于 67.6–96.5MB 之间波动且有周期性压降，岛模式非分层为 74.1–92.1MB，**两档量级相当**；与 §1.4 的 15–30MB 目标有差距是既存状况，与本次改动无关。

- **竖屏（纵向）时自动退出**（2026-11-11 用户要求）：主显示器 `h > w` 就主动 `Shutdown()`。判据是 `src/DeskBar/App.xaml.cs` 的 `private static bool IsScreenPortrait()`（读 `GetSystemMetrics(0/1)`，与任务栏几何是同一套口径），调用点挂在**已有的 `_ownerGuardTimer`（2 秒巡检，`StartOwnerGuard()`）的 Tick 开头** —— **不要为它新开轮询、也不要挂 `SystemEvents.DisplaySettingsChanged`**。理由：桌面浮岛全部按横向任务栏设计，竖屏下会停在错误位置；进程启动时若已是竖屏，也会在下一次 tick（≤2 秒）退出。

### 2.4.1 描边几何（只有方圆角一种，形状设置项已删除）

**2026-11-11 用户要求「删掉边缘形状设置，删掉胶囊圆，默认方圆角」**，于是 `AppSettings.ShapeStyle` 字段、设置页「边缘形状」下拉（`CmbShapeStyle` / `OnShapeStyleChanged` / `TxtShapeDesc`）、`ShapeGeometry.Normalize/Current/IsCircle`、`TaskbarService.CircleCornerOffsets` 全部**已删除**。现在处处只剩方圆角一档。**不要把 circle 档或形状设置项加回来。**

迁移注意：老 `settings.json` 里残留的 `"ShapeStyle": "..."` 会被 `JsonSerializer` 的未知字段策略静默忽略（不报错），下次 `Persist()` 自动消失；不需要写迁移代码。

- **几何唯一出口是 `ShapeGeometry`**（`Services/ShapeGeometry.cs`）。描边圆角、上下内缩、左右留白都从它取，禁止在窗口里重新写死数值 —— 之前 `IslandBorderWindow` 两句 `island ? 24 : 24` 是同一个值，形状切换完全没反应。
- **组件不跟随任何形状**（2026-11-11 用户明确要求）：两个浮岛的圆角由「DWM 8px + XAML `CornerRadius=8`」决定（见本节末尾那条），与 `ShapeGeometry` 无关。曾有一版把组件也联动（`ShapeGeometry.WidgetRadius` + 两个窗口的 `SizeChanged → ApplyShape()`）；用户要求「组件恢复原样」，已整体移除，**不要加回来**。
- **`CornerRadius` 不能超过高度一半**：超过之后 WPF 会把角画成 `rx=r`、`ry=高度/2` 的**椭圆弧**（离屏渲染实测；旧说法「双角互削成异形」不准确，实测是椭圆化 + 横线被吃短），视觉上就不再读作圆角。
- **方圆角半径 = 半高的一半**：`ShapeGeometry.BorderRadius(heightDip)` 返回 `heightDip / 2.0 * 0.5`（H=44 → 11、H=48 → 12）。历史注记：曾经有 `island` / `circle` 双档（circle 取半高 = 完整胶囊），以及更早一版按「横线保留 12px」反解的 `MinStraightLineDip`（常量已不存在）—— **两套都随设置项一起删了，不要复活**。
- **端点留白必须向外扩**（`BorderEdgePadPx = 16`）：按钮高亮块 40px 高，在 44px 描边窗里距窗顶仅 2px，正是圆弧内缩处；方圆角半径 11 在 dy=2 处内缩约 4.7px，留白 16px 让横线完整越过图标、不卡任何边角。向外扩才不缩短框宽。
- **托盘侧曾有一个吃留白的 bug**：旧写法 `l = rtTrayLeft` 把 `TaskbarService` 给的留白整个抹掉，窗左缘与「^」按钮左缘完全重合。现为 `l = rtTrayLeft - BorderEdgePadPx`，另加夹取防止托盘框压住音乐胶囊（胶囊右缘 = `trayLeft - 14`，与 `MusicCapsuleWindow.GapToTrayPx` 同源）。
- **描边窗口与两个浮岛都上下各内缩 `BorderInsetPx = 2`**：任务栏底边就是屏幕底边，四角圆角的下半原本被屏幕裁掉，看起来只剩上角。内缩后四角完整，圆角上界也随之变为 `(高 - 4) / 2`。内缩里的 `height` 是任务栏原始高度，改顺序会让圆角算出越界值。
  - 描边侧由 `src/DeskBar/Views/IslandBorderWindow.xaml.cs:194` 的 `insetH = height - inset * 2` 现算；浮岛侧是**把窗口高度直接写死成 44**（`src/DeskBar/Views/SystemMonitorCapsuleWindow.xaml:14`、`src/DeskBar/Views/MusicCapsuleWindow.xaml:15`）—— 两个浮岛的定位本来就是垂直居中（`y = tbTop + (tbHeight - h) / 2`，见 `SystemMonitorCapsuleWindow.xaml.cs:342` 与 `MusicCapsuleWindow.xaml.cs:906/912`），高度一改位置自动跟随，**不需要单独挪 `Top`**。
  - **改 `BorderInsetPx` 必须同步这两个 `Height`（= 48 − 2 × inset）**，否则浮岛重新被屏幕裁掉底角、与描边框错位。
  - 2026-11-11 实测（`clear`、DPI 100%）：改前描边框 44px / 浮岛 48px，上下边缘各错 2px；改后四个窗口（中间应用区、托盘区、监控浮岛、音乐胶囊）的矩形统一为 y **1394–1438**。
- **岛模式挖洞的顶角曲线只有一套**（`TaskbarService.SmoothCornerOffsets`，10 行，`CurrentCornerOffsets` 直接返回它）。历史上的 `CircleCornerOffsets`（16 行、更饱满）已随胶囊圆档删除。区域签名 `isig` 因此**不再带形状键**（只剩几何数字），形状切换这个概念已经不存在了。
- **组件圆角与内边距一律用 XAML 固定值**（两个浮岛都是 `CornerRadius=8`，见本节末尾「两处同值、同心」那条；`Padding` 监控 `10,0`、音乐 `8,0`）。注意组件的 `8` **不是** `ShapeGeometry.BorderRadius` 算出来的 `11` —— 它必须等于 DWM `DWMWCP_ROUND` 的 8px 才能同心，改任一处都要同步另一处。历史注记：曾有一版按「内容顶边处的弧线内缩量」动态推开内边距 —— `dy = rIn - (高-内容高)/2`、`rIn = r - 1px边框`、封面尺寸读 `BorderCover.Height`，并由 `SizeChanged → ApplyShape()` 在运行时重算（构造期 `ActualHeight=0`，需回落 XAML 的 `Height`）；已随组件联动一起删除。**若将来重新给组件上大圆角，这段避让算法是必需的**，否则方角封面会被弧线切到。
- **浮岛圆角必须「两处同值、同心」—— DWM 的 8px 与 XAML 的 `CornerRadius=8`**（2026-11-11 反复踩过，这里记最终解法）：
  - **accent / 亚克力材质铺满整个窗口矩形，且是 DWM 合成的 —— `SetWindowRgn` 裁不动它。** 实测：`GetWindowRgn` 返回 `COMPLEXREGION`（区域确实设上了）、`SetWindowRgn` 返回 1（成功）、外层再加 `Loaded` 兜时序，但截图里材质四角**依然是直角**。所以「用 `SetWindowRgn` 把窗口裁圆」这条路对带 accent 的窗口是无效的，不要再试（对照脚本 `.scratch/probe_corner_methods.py`）。
  - **唯一能裁掉那层材质的是 DWM 自己的圆角**：`AcrylicWindowHelper.EnableRoundedCorners(this)`（`DWMWA_WINDOW_CORNER_PREFERENCE = 33` 设 `DWMWCP_ROUND = 2`）。实测其半径是 **8px @100% DPI**，且随 DPI 缩放。
  - 因此两个浮岛窗口都**必须调它**，并且 **XAML 的 `CornerRadius` 必须写成同一个数 `8`**，描边线才会与材质边缘同心。作用是「谁裁材质、谁画描边」分工：**DWM 裁材质，XAML `CornerRadius` 画描边**。
  - **只在岛模式调**：`if (AcrylicWindowHelper.IsIslandStyle())` —— 整条模式（`acrylic` / `clear`）浮岛是真透明窗口（`AllowsTransparency=True`）、没有 accent 材质要裁，对分层窗口设 DWM 圆角还可能让它画不出来（见 §2.4 的两条 ctypes 教训）。
  - 三种状态实测对照（`island_acrylic`，`SystemMonitorCapsuleWindow`）：① **不调 DWM 圆角** = 方角材质 + 圆角描边线（用户报「谁会希望有一个角的？既然是圆就不要有角啊」）；② **DWM 圆角 8px + XAML `CornerRadius=11`** = 材质与描边**不同心**、四角留一条孤立弧线（用户报「边角不太对」）；③ **DWM 圆角 8px + XAML `CornerRadius=8`** = 同心、四角干净 ← **正解**。改任一处都必须同步另一处。
  - `SettingsWindow` 与 `SystemMonitorDetailWindow` 也在 `OnSourceInitialized` 里调了它（普通窗口、没有圆角 Border，取系统标准圆角）—— 那两处是既有的，不要删。

### 2.5 音色（tone）与昼夜方案
- 合法 tone 只有 5 个：`light`、`dark`、`natural`、`softmist`、`sheertights`（见 `AppSettings.IsKnownTone`）。
- 深浅判定：`sheertights` / `dark` = 深色底；`softmist` = 浅色底；`natural` = 跟随系统；`light` = 固定浅色底（2026-11-11 起不再跟随系统，见下条）。
- 任务栏着色：`softmist=0x25525252`、`sheertights=0x25323232`、`light=0x8CEAE6DE`、`dark=0x90202020`、`natural=0x00000000`。
  - **`softmist` / `sheertights` 的 15% 是用户选定的外观，不要为了提高文字对比度擅自调高**（2026-11-10 试过 45% / 68%，用户明确要求改回）。这两档 alpha 只有 14.5%，壁纸占 85%，因此 `IsDark` 的明暗假设一旦与壁纸相反，字就会看不清 —— 实测暗壁纸下 `softmist` 合成 `#35352E`、配浅色档深字 `#262626` 只有 **1.23:1**。这是**已知取舍**：调高 alpha 能修好文字，但会把「柔雾 / 黑丝」变成近乎实心的板子，属于产品外观决策，交用户定。
  - WaterSprite-TaskbarMediaBar（MIT）的同类做法是 `源代码/soda-player/src/Widget.vue:47` `.container{background:light-dark(rgb(255 255 255 / 32%),rgb(32 32 32 / 60%))}`，且它的 `Widget.vue:48` 停靠态是 `background:transparent`、全仓也没有壁纸自适应（`luminance|getpixel|blit` 零命中）。**它并没有解决低 alpha + 反色壁纸这个组合**，别把它当现成答案。
  - **任务栏字形颜色不归 DeskBar 管**：`ITaskbarAppearanceService`（`NativeTaskbarAppearanceService.cs:18-40`）只有背景 brush/blur/border 三类方法，没有任何前景色入口，Explorer 按系统深浅自己下发字色（本机 `SystemUsesLightTheme=0` 时恒为白字）。所以「白字压白底」只能靠底色避让。实测：把 `SystemUsesLightTheme` 与 `AppsUseLightTheme` 都设为 1 并重启 explorer，在 `ColorPrevalence=2` 的机器上任务栏仍是深底白字，此路不通。
  - WCAG 反解：白字要 4.5:1，底色相对亮度须 ≤0.1833（灰阶 ≤`#777777`）；任何浅色底不论 alpha 多低，亮壁纸下都 ≥`#D0D0D0`，白字恒 <1.6:1 —— **降 alpha 修不好白字可读性**。
  - `light` 的历史形状：最早是**按系统深浅分两支**（系统浅色 55% 暖米白 `0x8CEAE6DE` / 系统深色 72% 暖墨 `0xB82A2C28`）；**2026-11-11 用户要求「只留一个浅」后改成固定 55% 暖米白 `0x8CEAE6DE`**，`0xB82A2C28` 已从 `GetColorForTone` 移除（只在 `TonePalette.cs` 注释里留作历史）。更早的 `0xA0FFFFFF`（63% 纯白）已废，用户报「刺眼白板」。
  - **`light` 现在固定浅色底，所以深色系统下必然「白字压浅底」**：本机 `SystemUsesLightTheme=0`，Explorer 恒下发白字，压在 `0x8CEAE6DE` 的合成色上远低于 4.5:1。这是**用户明确选择并接受的取舍**（2026-11-11），**不要替用户改回「按系统深浅分两支」**。
  - **`TonePalette.IsDark` 必须与 `GetColorForTone` 同源**：`light` 自 2026-11-11 起固定浅色底，IsDark 恒为 `false`；两项必须同步改，否则会出现浅底浅字。
  - 改 `light` 的颜色值，必须同时改这 **7 处**：① `GetColorForTone` 定义（`NativeTaskbarAppearanceService.cs` 里 `"light" =>` 那一行）；② `TonePalette.IsDark` 的 `"light"` 判定；③ `SettingsWindow.xaml` 三处 `Tag="light"` 下拉项文案（含「xx% 覆盖」）；④ `TonePalette.cs` 里 `CapsuleTextBrush` 的 `<remarks>` 四组对比度数字（改色后全部失效）；⑤ 本节「深浅判定」与「任务栏着色」两行；⑥ 本节 `light` 的历史与取舍子条；⑦ §2.6 胶囊前景的对比度数字。**色块 ToolTip 不用手改** —— `SettingsWindow.xaml.cs:266-267` 由 `color.A` 现算，`ToneDisplayName` 只提供短名、不含颜色值。
- **渲染路径一律读 `App.Settings.EffectiveTone`，禁止直接读 `AcrylicTone`**。自动主题按时段把结果写进 `RuntimeTone`（`[JsonIgnore]`，不落盘），`EffectiveTone => RuntimeTone ?? AcrylicTone`。读错会让描边比底色慢一拍。
- 昼夜方案是显式字段（`DayThemeLight`/`NightAcrylicTone` 等），不再是「读系统深浅色反推」。

### 2.6 浮岛胶囊的前景（`TonePalette.Capsule*`）
胶囊贴在任务栏上，底色随 tone 变，所以**前景不能在 XAML 里写死** —— 一律 `{DynamicResource CapsuleXxxBrush}`，由各窗口 `ApplyToneBorder()` 按 `EffectiveTone` 下发。共 8 个令牌：`CapsuleTextBrush`（主文字：指标数值、已唱歌词、进度条填充）、`CapsuleTextMutedBrush`（标签、歌名-歌手、**未唱歌词层 `TxtArtist`**）、`CapsuleTextFaintBrush`（禁用按钮）、`CapsuleButtonForegroundBrush`、`CapsuleHoverForegroundBrush`、`CapsuleTrackBrush`（进度条轨道）、`CapsuleCoverBackBrush`（无封面占位）、`CapsuleControlsMaskBrush`（控制区渐变，唯一的非纯色，自己 `lock` + `Freeze`）。

- **新窗口用这些键时，`<Window.Resources>` 里必须补默认值**（每个键一条 `<SolidColorBrush>`）。漏了首帧会回落 WPF 默认黑字，在深色任务栏上一闪而过。`SystemMonitorCapsuleWindow.xaml` 曾漏过 3 个。
- **主文字不用纯黑纯白**（2026-11-10 用户实测「歌词白色太白、黑色太黑」）：深色档 `#EDEAE5` 暖白、浅色档 `#0A0A0A` 近黑（原 `#262626` 炭灰，2026-11-10 同日加深）。两个值都由 WCAG AA 反推并留最不利壁纸余量 —— 深色档原先在 `0xB82A2C28`（light 旧深色支，已废）上三档壁纸 12.33 / 8.29 / 5.19:1；浅色档在 `0x8CEAE6DE` 上 4.52 / 7.64 / 12.38:1。
- **浅色档不能再浅**：试过 `#333333`，亮底好看但深壁纸下只有 3.77:1，不合格。想更柔和就必须放弃 AA，那是另一个决策。
- `CapsuleTextMutedBrush` 是 `#99` 半透明，最不利情况 3.4–3.5:1（低于 AA）—— 这是**有意为之**：它只承载标签/未唱层，靠比主文字弱来表达层级；不要为了凑 4.5:1 把它提到和主文字一样亮，那会让逐字高亮失去对比。

### 2.7 歌词字体（用户自备，`LyricFont.cs`）
**程序不内置任何字体文件。** 好看的歌词字体（MiSans 等可变字体）单文件 10MB+，且许可常不允许随第三方程序分发（MiSans 明确「不适用本项目 MIT」，见 WaterSprite 的 `THIRD_PARTY_NOTICES.md:10-15`）。所以做成 `AppSettings.MusicCapsuleLyricFontPath` + 设置页「选择…」，由用户导入 `.ttf/.otf/.ttc`。

- **族名必须从文件里读**（`GlyphTypeface(uri).FamilyNames`，优先 `en-US`），**不许用文件名推**：用户会把 `simsun.ttc` 改名成「我的歌词字体.otf」。拿到族名后用 `new FontFamily(uri, "./#" + familyName)` 构造，再用 `Typeface.TryGetGlyphTypeface` 回验一次。
- **实测边界（`.scratch/fonttest/Probe*.cs`，别重新踩）**：① 目标字重在文件里不存在时 WPF 自动挑最接近的（SimSun 只有 Normal/Bold，请求 `SemiBold` 解析成 `Bold`），不抛异常也不难看；② 族名写错不抛异常，只是 `TryGetGlyphTypeface` 返回 false、静默退默认字体；③ 选到非字体文件抛 `FileFormatException`（用来给用户报错），所以 `LyricFont` 里必须单独 catch 它；④ 加载后 WPF 占用句柄（`FileShare.None` 打开会 IOException），但**改名/替换仍成功** —— 换字体不必退出程序；⑤ 同一路径换内容受 WPF 字体缓存影响，**要重开 DeskBar 才生效**，设置页提示语按这个写。
- **失败结果也要缓存**（`LyricFont._cachedPath` / `_cachedError`），否则每次 `ApplyToneBorder`（tone 刷新会调）都会重读一次坏文件。
- 换字体后**必须清 `_charWidths` 与 `_charWidthCache`**，否则逐字高亮的像素宽度还是旧字体的，整行会错位。
- 字体**只作用于歌词两层**（`TxtArtist` / `TxtLyricPlayed`），`TxtTitle` 保持界面字体 —— 用户导入装饰字体不该把曲目名也变样。
- **不给 `MusicCapsuleWindow` 加 `using System.Windows.Media`**：项目同时开 `UseWindowsForms`，`System.Drawing` 里有同名 `FontFamily`；字段写全限定名 `System.Windows.Media.FontFamily` 即可（该文件原本只 using 了 Media.Animation）。`LyricFont.cs` 内部可以正常 using，但**必须同时 using `System.Windows`**，否则 `FontStyles` / `FontWeights` / `FontStretches` 全找不到（已踩）。

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

**⚠️ 两个 `.ps1` 都不传播退出码**（脚本内无 `exit`，`powershell -File` 外层恒返回 0）。判断成败必须三条同时成立（2026-11-10 实测校准）：
- **跑前先删 `build_diag.log`** —— `-flp` 是覆盖写，dotnet 早崩会残留上一次的成功日志，直接读会得到「陈旧成功」假绿；
- `build_diag.log` 的 **mtime 已更新**，且**全文**含 `已成功生成。`（**它不在文件末尾**：实测在第 80267 行，末尾四行是警告数 / 错误数 / 空行 / `已用时间 …`，只 `tail` 会漏判）；
- 末尾段含 `0 个错误`（失败会是 `1 个错误` 之类）。
- 脚本把 9 个环境变量重定向到仓库内，且日志路径相对 cwd，**必须在 `D:\ai\deskbar` 下执行**；判据串是中文，**放 bash 判，不要放 PowerShell**。
- `$LASTEXITCODE` 只在同一条 PowerShell 会话里可查，跨进程取不到——不能作为独立判据。

**完整链条固定为 `build.ps1` → `publish.ps1` → `deploy.sh`**。只 build 不 publish 会让 `out_publish\` 停留在旧版，制造「改了却没生效」的假象。

**部署脚本只认 `deploy.sh`**。`deploy.ps1` / `deploy2.ps1` 是历史遗留，行为互不一致（前者不跳过 `ExplorerTAP.dll` 且用 `-ErrorAction Continue` 静默吞掉覆盖失败）。**不要用它们，也不要「顺手统一」**。

### 辅助工具
```powershell
# 后端改动后，检查设置 UI 是否需要跟进（只检测、不改源码）
powershell -NoProfile -ExecutionPolicy Bypass -File D:/ai/deskbar/sync-frontend.ps1
# 重置同步基线
powershell -NoProfile -ExecutionPolicy Bypass -File D:/ai/deskbar/sync-frontend.ps1 -Init

# ⚠️ `python` 不在 PATH —— WindowsApps 里那个是商店桩（无输出、EXIT=49）。
# 用 DSH 运行时自带的解释器（实测 Python 3.12.14）：
#   $PY = "D:/DeepSeek/.dsh/dsh-runtimes/dsh-primary-runtime/dependencies/python/python.exe"
# 窗口枚举：pid / hwnd / 可见性 / 矩形 / 类名 / 标题
& $PY scripts/winlist.py [pid]
# 闪烁检测：抓 50 帧只测描边区域灰度，避开文字刷新造成的假阳性（无基线阈值，只能靠改动前后对比）
& $PY scripts/flick7.py mon|tray
# 任务栏截图：-Out 是必填参数，漏了报 MissingMandatoryParameter；默认抓屏幕底部 160px
# 2560x1440 屏幕抓 0,1280 → 图高 160，屏幕 y = 图 y + 1280（写像素探针时必须换算）
powershell -NoProfile -ExecutionPolicy Bypass -File shot_taskbar.ps1 -Out .scratch/verify/taskbar.png
```

**验证设置项要会唤起设置窗**：它是 `Hide()` 关掉的常驻窗口，进程里一直存在但不可见。用 `.scratch/wake.py`（`OpenEventW(EVENT_MODIFY_STATE=0x0002, False, "Local\DeskBar_WakeEvent")` + `SetEvent`）唤起，再用 `windows_computer_use_list_windows` 拿 hwnd/pid。**控件 id 形如 `uia:rt:7-<pid>-<n>`，带 pid，重启 DeskBar 后全部失效**，必须重新 `accessibility_tree` 取。
- WPF `ComboBox` **不能用 `click` 展开**（实测时开时收，不可靠）——必须用 `windows_computer_use_invoke` 的 `action: "expand"`；下拉项在弹出的独立 Popup 里，要在 `scope: "desktop"` 里 `find`，再用 `invoke`（method: SelectionItem）选中。

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
- **保持原行尾**：改前必须检测，写回**沿用该文件原有的行尾**。**本仓实测普遍是 LF，不是 CRLF**（`src/DeskBar` 下 42 个 `.cs/.xaml` 有 40 个纯 LF，唯二纯 CRLF 的是 `src/DeskBar/App.xaml.cs` 与 `src/DeskBar/Views/SettingsWindow.xaml`，`git ls-files --eol` 显示它们 `w/crlf`）——旧版此处写的「普遍是 CRLF」已被实测证伪。等价的可执行检查 = `git diff --numstat` 的增删数不得等于文件总行数（整文件重写＝行尾翻转）。`core.autocrlf=true` 下 `git diff` 会打印 `LF will be replaced by CRLF` 警告，那是索引规范化的提示，不代表工作区已被改动。
- **编码保护**：`AppSettings.cs` 等文件含中文 XML 注释，禁用外部重格式化工具整体覆盖。
- **`.ps1` 脚本禁止出现任何非 ASCII 字符**（含中文注释）。Windows PowerShell 5.1 对无 BOM 的 `.ps1` 按 OEM/ANSI 解码，中文会直接 `ParserError`。`sync-frontend.ps1` 文件头就写死了这条约束。
- **删除目录前必须展开看内容，不能靠目录名推断归属**。曾把 `C:\Users\Administrator\.local` 判为 uv 残留、准备整个删除，展开后发现它是 billion-context 的图片缓存（`state\billion-context\retrieve\img\session-*\`）加 opencode 的 `state\opencode\service.json`。`.local` / `.config` / `.cache` 这类约定俗成目录会被多个工具共用，**`.local\bin` 为空不代表整个 `.local` 可删**。同理，`D:\app\<name>` 这类手工收纳目录里也可能混着别人的东西。

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
- **描边色由 `TonePalette` 按 tone 下发，不在 XAML 里写死**：深色底 `0x40FFFFFF`、浅色底 `0x33000000`，都是 1px（`BorderThickness="1"`）。旧文档所说的「统一 `#38000000`、统一 `CornerRadius=9`」**已过时**——两个浮岛的圆角都是 `8`（固定在各自 XAML 里，必须等于 DWM `DWMWCP_ROUND` 的 8px，见 §2.4.1 末尾）；描边层圆角**不写在 XAML**，由 `ShapeGeometry.BorderRadius` 按实际高度实时下发（只有方圆角一档，见 §2.4.1）。
- 各窗口必须在 `OnSourceInitialized` 里调 `AcrylicWindowHelper.RemoveWindowBorder(this)`（`DWMWA_BORDER_COLOR(34)`），否则 DWM 会额外叠一圈 1px 高光边框。
- **注入失败与管理员权限无关**。Explorer 与 DeskBar 同为 Medium 完整性级别，同级注入本就允许。**「必须管理员权限才能接管任务栏外观」是被本机实测证伪的历史断言**（日志里 `elevated=False` 但注入成功、`0x80070005` 出现 0 次、计划任务根本不存在）。真实失败原因是 `ExplorerTAP.dll` 缺失、版本不匹配，或 Explorer 正在重启。**任何权限相关文案不要回退到旧说法。**
- 注入失败后 **30 秒内不再重试**。任务栏几何扫描是 100ms 级循环，每次真去注入会把日志刷爆并拖慢扫描。
- **explorer 重启后 COM 掉线必须能被自愈**（2026-11-10 踩过，已修）。`RefreshCurrentAppearance` 里 explorer 死亡抛的是 **`InvalidCastException`**（原文 `Unable to cast COM object … QueryInterface … IID '{5BCF9150-…}' … RPC 服务器不可用 (0x800706BA)`），**它不是 `COMException` 的子类**。所以这里必须是 `catch (Exception ex) when (ex is COMException or InvalidCastException)` —— 只 catch `COMException` 会让异常落到下面的通用 catch，`_service` 永不置空，`EnsureInitialized()` 的 `if (_service != null) return true;` 永久短路，重连路径再也不进，**外观彻底失能、只能重启 DeskBar**。现场特征：日志每 3 秒刷一条 `RefreshCurrentAppearance error: … 0x800706BA`、从不恢复。修复后同样实验（kill explorer → WMI 拉起）约 38 秒自动重连并打出 `NativeTaskbarAppearanceService initialized successfully via COM.`，上述错误计数为 0。期间出现的 `InjectExplorerTAP returned 0x800401E3`（CO_E_CLASSSTRING）是 explorer 正重启的瞬时失败，属正常。
- **本机 kill explorer 不会自动重启**（桌面变黑且不恢复）。必须手动走 WMI 拉起：`Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{CommandLine='C:\Windows\explorer.exe'}`（理由同 4.9，`Start-Process` 起的进程挂在宿主 Job Object 上）。
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
- 改 tone 的 alpha 值，必须同步更新下拉项里的「xx% 覆盖」文案（`SettingsWindow.xaml` 里三处 ComboBox 各一行，共 3 处硬编码文本）。**色块 ToolTip 不用手改**——`SettingsWindow.xaml.cs:266-267` 由 `color.A` 现算，改定义即自动跟随（旧版本此处把它们都写成「硬编码文本」）。
- `RuntimeTone` 是 `[JsonIgnore]` 运行时态。**误加持久化会让用户手选的色调被时段值覆盖。**
- 关闭设置窗是 `e.Cancel = true` + `Hide()`，**不是退出进程**。

### 4.6 媒体与歌词子系统
- **重建出来的窗口必须主动补拉既有状态 —— 事件只为「变化」而发，不会补发**（2026-11-11 踩过，表现为「切换布局后音乐胶囊没了」）：切任务栏风格走 `RecreateWidgetWindows()`（`App.xaml.cs:539`）整窗重建，但 `SyncMusicCapsule()` / `SyncSystemMonitor()`（`:379` / `:426`）在服务已非 null 时只调 `ReevaluateAsync()`，**不重新 `AttachService`**；而 `MusicCapsuleWindow.OnMediaInfoChanged`（`MusicCapsuleWindow.xaml.cs:195`）是**唯一**给 `_current` 赋值的地方，新窗口的 `_current` 恒为 null → `RefreshFromSettings()`（`:281`）走 `HideCapsule()` 分支 → 窗口永远不显示。修法两半缺一不可：① `AttachService` 在 `_service is not null` 分支末尾用 `if (_service.CurrentInfo is MediaTrackInfo snapshot) OnMediaInfoChanged(snapshot);` 主动补拉（`:149`）；② `App.xaml.cs:399` / `:445` 把 `_capsule?.AttachService(_media)` / `_monitorWindow?.AttachService(_monitor)` 放在 `if (_media is null)` 之外**无条件执行**。监控浮岛没这个坑，因为它的 `RefreshFromSettings()` 只看 `SystemMonitorEnabled`、不依赖数据是否已到。
- **播放控制只走 GSMTC 的 `TrySkipPreviousAsync` / `TryTogglePlayPauseAsync` / `TrySkipNextAsync`**，不得改成模拟按键或窗口消息。注意：当前 UI **丢弃返回的 bool**，所以「点了没反应」是已知现状，不是回归 bug——别为此加弹出提示，除非用户明确要求。
- **歌词匹配不得放宽「候选唯一」判据**。歧义时宁可无歌词，也不要串到翻唱/同名版本。
- **QRC 的 DES 位序与 KRC 的 16 字节常量是格式常量**，换成标准 TripleDES / 标准 XOR 一定解不出。
- **封面只能经 `SafeArtwork.DownloadAsync`**：强制 https、拒私网与 DNS 重绑定、重定向 ≤3 跳、体积 ≤1MB、魔数验真类型。封面 URL 来自第三方接口，属不可信输入，绕过就是 SSRF 面。
- **禁止为封面新建磁盘缓存目录**。现有设计是实时下载 → 内存解码 → `Freeze()` → 交 GC，保障零额外磁盘 I/O。
- **WPF `BitmapImage` 解不了带 EXIF(APP1) 的 JPEG，报错原文是「无法从流中读取」**——这个措辞会把排查引向网络或流，实际是解码器拒绝。`MediaSessionService.LoadBitmapFromBytes` 失败时先走 `StripApp1Segment` 剥掉 APP1 重试；GSMTC 缩略图与本地封面共用这条路径，别只修一处。
- **读 GSMTC 缩略图流必须按 `LoadAsync` 的实际返回值建数组**，不得用 `streamRef.Size` 声明长度：Windows 侧可能短投递，按声明长度 `ReadBytes` 必抛异常。
- **QQ 音乐封面缓存按「歌手_专辑_编号.jpg」命名，编号是分辨率档位**（实测 `_4`=500×500、`_0`=150×150），存在 `QQMusicPicture`。匹配必须用 `归一化(歌手)+归一化(专辑)` 全等，**不得退回「标题 contains」**——只有专辑名恰好等于歌名时才碰巧命中（《幻听》的专辑是「梦游计」，用歌名永远匹配不到）。`props.AlbumTitle` 在 QQ 音乐下有值，是这套判据的前提。多档位候选取编号最大的那张。
- **`TryGetLocalCover` 的缓存必须连失败结果一起缓存**（成功 30min / 失败 5min / 上限 64 条）。只缓存成功会让「这首歌没有封面」随每次刷新重扫目录。
- 上述封面兜底判据与缓存策略借鉴 WaterSprite-TaskbarMediaBar（`cache-media.cjs:14,40` 的专辑匹配与占位图排除、`kuwo-artwork.cjs:24,38` 的失败短缓存与容量淘汰、`cover-reader.cs:22-39` 的流读取写法）。再借鉴它时先 clone 读实现。
- **位图跨线程前必须 `Freeze()`**；后台回到 UI 必须 `Dispatcher.CheckAccess()` / `BeginInvoke`。
- 定时器（16ms 滚动、16ms ZGuard、50ms 跟随、120ms 悬停、400ms 定位、100ms 歌词）**在 `Closed` 里必须全部 `Stop()` 并解绑事件**，否则向已 Close 的窗口 `Show()` 会抛 `InvalidOperationException`。
- `NOACTIVATE` 窗口下 `MouseEnter` / `MouseLeave` 不可靠，悬停只能轮询光标坐标。
- 文本宽度有两个反向陷阱：`TxtArtist` 必须设 `Width = NaN` 交给 `ScrollViewer` 测量；而行级 LRC 的 `TxtLyricPlayed` 反而必须设自然宽度（`NaN` 会被钳到 164px）。
- 右键菜单需临时摘掉 `NOACTIVATE` 抢前台，关闭后还原并 `PostMessage(WM_NULL)` 归还前台。
- **歌词对象缓存是纯内存 `ConcurrentDictionary`，无 TTL、无容量上限**（`LyricService._cache`）。长会话会随曲目数线性增长——这是已知设计取舍，属于内存而非磁盘，不违反「拒绝磁盘缓存」原则。
- **但歌词的「失败」绝对不能缓存（2026-11-10 踩过的坑，别再犯）**：`TryGetLocalQrc`（`src/DeskBar/Services/LyricService.cs:648`）筛选候选时带「文件名时长与上报时长 4 秒容差」判据（`:686-699`），而**切歌瞬间上报的 `duration` 还是上一首的**，第一次匹配必然落空；解除这个暂时性失败，靠的正是 3 秒轮询带来的自动重试。曾加过失败缓存（本地 5min / 在线 45s），代价是把「下一轮本来就能成功」的歌冻住数分钟，用户看到的就是**「切歌后一直没歌词」**。封面侧有 `CoverMissTtlMs` **不代表**歌词侧也能有——两者的失败含义不同（前者是「这张图确实没有」，后者常常只是「这一瞬间还没准备好」）。
- 诊断打点 `Log.Debug($"lyrics fetch: …")` 打在**真发起**处（`_cache` 未命中时），出现一行 = 回退链跑了一次。

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

### 4.9 进程托管：从 AI 宿主启动的 DeskBar 会被连带杀死

**根因**：从 DSH 派生的进程继承同一个 Windows **Job Object**，该 Job 带 `KILL_ON_JOB_CLOSE` —— 宿主退出即连带强杀后代（实测 `IsProcessInJob` 为 `True`）。

**日志判据**：强杀不走 `OnExit`。`logs\deskbar-YYYYMMDD.log` 在正常 tick 中**突然中断且无 `DeskBar exiting` 行** = 被宿主带走，不是崩溃。

**启动必须用 WMI**（`deploy.sh` 已改），**禁止 `Start-Process` / `cmd start` / `& .\DeskBar.exe`**：

```powershell
Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{CommandLine='D:\app\DeskBar\DeskBar.exe'}
```

**验证**：`Get-CimInstance Win32_Process -Filter "Name='DeskBar.exe'"` 的 `ParentProcessId` 应为 `WmiPrvSE.exe`；若为 `powershell.exe` / `bash.exe` / `node.exe` 则仍在宿主链上。

**只影响未重新登录的窗口期**：`HKCU\...\Run` 有自启动项，登录后正常恢复，故表现为间歇性。

### 4.10 发版：Git Tag ≠ GitHub Release

`git tag` + `git push --tags` **不产生** GitHub Release，也不打包上传 zip。后果：仓库首页 Releases 仍显示旧版，且**软件内「检查更新」读 `releases/latest`，看不到新 tag**——用户会被告知「已是最新版本」。

**五步流程**：

1. 改 `src/DeskBar/DeskBar.csproj` 的 `<Version>` → `build.ps1` → `publish.ps1` → `deploy.sh`；
2. `git commit` → `git tag vX.Y.Z` → `git push origin master --tags`；
3. `out_publish/*` 打包为 `DeskBar-vX.Y.Z-win-x64.zip`；
4. `POST https://api.github.com/repos/buyikun/DeskBar/releases`（body 含 `tag_name`/`name`/`body`/`draft:false`/`prerelease:false`）；
5. 取返回的 `upload_url` 去掉 `{?name,label}` 后缀，`POST <upload_url>?name=<zip名>`，`Content-Type: application/zip`，`--data-binary @本地zip`。

**验收**：`releases/latest` 的 `tag_name` 为新版本，且 `assets[].state == "uploaded"`、`size` 与本地 zip 一致。

**两个坑**：
- **bash 里禁止手写含 `{` / `$` 的 JSON**（`upload_url` 的 `{?name,label}`、`body` 的 markdown 都会截断命令，**静默失败**）。JSON 写独立文件，用 `--data-binary @file`。
- 发布/上传/删除/**部署**这类外部副作用，**汇报前必须读回真值**，不能凭脚本"没报错"就宣布完成。

#### 4.10.1 发版前置门槛

**必须先判定必要性并取得用户明确确认，才可以动手。** 判据：**改动是否触及 `src/` 里会被编译进 exe/dll 的代码？**

| 改动 | 发版？ |
| --- | --- |
| `src/` 业务代码（UI、任务栏、媒体、监控、更新） | ✅ |
| 只改 `AGENTS.md` / `docs/` / `deploy.sh` / `build.ps1` / 探针 | ❌ 走 `chore:` |
| 只改 `.csproj` 版本号 | ❌ 版本号是发版的一部分，不是理由 |

`out_publish/` 只有 11 个运行时文件，**`deploy.sh` 与 `AGENTS.md` 不在包内**——不触及 `src/` 的改动，新版与旧版**功能完全等价**，发出去只让用户白下 7MB。

**语义**：用户说「推送」= push commit；说「发布」/「发版」才是发版。**不得自行升级语义。**

（曾把 `deploy.sh` 修 Job Object 当发版理由、升 v2.3.2 发了 Release，而那 bug 只影响开发者本机；已删除该 Release 与 tag 并回退版本。）

---

## 5. AI 协作行为准则

1. **中文思考与回复**。推理过程与最终答复一律中文；代码、命令、路径、标识符、报错原文按原样保留。
2. **先读后改**。动手前必须读目标文件的确切行号、签名与上下文，禁止凭推测下补丁。
3. **闭环验证**。每次改动后跑 `build.ps1` → `publish.ps1`；涉及 UI / 任务栏必须 `deploy.sh` 并用探针或截图确认。**判定成败看 `build_diag.log`，不看退出码。**
4. **禁止代劳重启宿主**。不得声称「正在重启 DSH」，不写延时脚本或计划任务。需要外部重启生效的变更，交给用户手动操作并附验证清单。
5. **保持极致精简**。不引入与意图无关的依赖，不过度封装，不加常驻轮询。守护 15–30MB 内存与零磁盘写入。
6. **不清理历史探针**。根目录与 `scripts/` 下的 `.ps1` / `.py` 是调试证据链，不是垃圾。
7. **过时断言要当场纠正**。若发现注释、文案、旧文档与实测结论冲突（例如残留的「需要管理员权限」说法），以本文件为准，并在改动时顺手修正，不要沿用。

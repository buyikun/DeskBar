# Windows 桌面增强工具 — GitHub 同类项目调研与落地方案

> 目标：把「任务栏透明/亚克力」「跟随系统主题」「双击桌面显隐图标」「设置面板+托盘」合并成一个程序。
> 本报告基于 GitHub 开源先遣专家团 5 名成员的一手检索 + 协调者本机实测裁定冲突。
> 数据采样时刻：2026-10-27。本机：Windows 11 build 26200 / 25H2，2560×1440 单屏。

---

## 一、结论先行

**三句话总结：**

1. **功能③④（双击显隐图标、托盘+设置面板）有 MIT 项目可以直接照着做，本机路径已实测跑通 —— 这是该先做的部分。**
2. **功能①（任务栏透明/亚克力）是整个项目唯一的高风险项，且在 Win11 上"自己调 Win32 `SetWindowCompositionAttribute`"这条路已被本机实测否定 —— 必须换路线，不能按常规排期。**
3. **功能②（跟随系统主题）风险低，但要注意：你参考的那个「系统自动主题（Tuyang）」开源本体其实并没有真正监听系统变化。**

**技术选型推荐：C# / .NET 8 + WPF + `H.NotifyIcon`（MIT，771★，活跃），单进程托盘应用。**
理由见第五节 —— 关键在于"是否自研任务栏注入"这个分水岭。

---

## 二、同类项目对照表（值得看的 12 个）

| # | 项目 | 定位 | 技术栈 | ★ | 最近提交 | License | 状态 |
|---|---|---|---|---|---|---|---|
| 1 | [TranslucentTB](https://github.com/TranslucentTB/TranslucentTB) | 任务栏透明/亚克力 | C++ | 20663 | 2026-10-13 | **GPL-3.0** | 活跃 |
| 2 | [Windows-Auto-Night-Mode](https://github.com/AutoDarkMode/Windows-Auto-Night-Mode) | 深浅色自动切换全家桶 | C# + Rust | 9785 | 2026-10-15 | **GPL-3.0** | 活跃 |
| 3 | **[polterdesk](https://github.com/EternityX/polterdesk)** | **双击桌面显隐图标+热键+托盘+设置窗** | **Rust** | 7 | 2026-06-14 | **MIT** | 单人维护 |
| 4 | **[Hide-Icons](https://github.com/emp0ry/Hide-Icons)** | 桌面图标显隐+热键+托盘+自更新 | C++ Win32 单文件 | 35 | 2026-10-23 | **MIT** | 活跃 |
| 5 | [DeskHider](https://github.com/iandiv/DeskHider) | 热键隐藏桌面图标 | AHK v1 | 88 | 2023-10-18 | MIT | 实质停更 |
| 6 | [ZenDesktop](https://github.com/Liset999/ZenDesktop) | 桌面+任务栏增强（Windhawk mod 集） | C++ | 706 | 2026-10-26 | **GPL-3.0** | 活跃 |
| 7 | [Windows_AutoTheme](https://github.com/tuyangJs/Windows_AutoTheme) | **你参考的「系统自动主题」本体** | Tauri 2 + Web | 172 | 2026-09-06 | **MIT** | 活跃 |
| 8 | [ExplorerBlurMica](https://github.com/Maplespe/ExplorerBlurMica) | 资源管理器 Blur/Acrylic/Mica | C++ | 4265 | 2026-10-12 | LGPL-3.0 | 活跃 |
| 9 | [MicaForEveryone](https://github.com/MicaForEveryone/MicaForEveryone) | 应用标题栏 backdrop | C# | 5259 | 2026-07-17 | MIT | 活跃 |
| 10 | [Windhawk](https://github.com/ramensoftware/windhawk) | 注入框架 + mod 商城 | Rust + C++ | 9576 | 2026-10-25 | **GPL-3.0** | 活跃 |
| 11 | [TaskbarX](https://github.com/ChrisAnd1998/TaskbarX) | 任务栏图标居中（经典） | VB.NET | 7966 | 2024-01-21 | MIT | **已归档** |
| 12 | [RoundedTB](https://github.com/RoundedTB/RoundedTB) | 任务栏圆角/边距 | C# | 3109 | 2023-10-16 | **GPL-3.0** | **已归档** |

**读表要点：**

- **第 3 行 polterdesk 是最该看的一个** —— 它的功能与你的需求几乎 1:1（双击显隐图标、全局热键、托盘右键菜单、设置窗口、开机启动、"同时隐藏任务栏"选项），而且是 **MIT**，代码只有 13 个源文件，可以合法借鉴。star 只有 7，会被按 star 排序的人直接漏掉。
- **第 4 行 Hide-Icons 是"实现最干净"的那个** —— 单文件 Win32，README 明说"不用偷改 `SysListView32`，走系统自己的命令"。MIT。
- **第 11、12 行不要碰** —— 都已归档。按 star 排序会先选中它们，这是本领域最典型的坑。
- **GPL-3.0 的（1、2、6、10、12）只能读思路，不能复制代码。** 你如果打算闭源分发，抄一行都算传染。

---

## 三、可借鉴点与对应源码位置

### 3.1 功能③ 双击桌面空白显隐图标 —— 两条路线，各有可取

**路线 A：直接显隐桌面图标 ListView（polterdesk 走这条）**

- 定位桌面图标窗口：`polterdesk/src/desktop/finder.rs:13-74`（MIT, commit `f2e3e8b`）
  - `FindWindowW("Progman")` → 发 `0x052C` 让 WorkerW 层生成 → 遍历 WorkerW 找 `SysListView32` 子窗口 → Win11 回退到 Progman 下直接找 → 最后 `EnumChildWindows` 兜底
- 显隐：`polterdesk/src/desktop/toggle.rs:5-26`
  - 隐藏 `ShowWindow(lv, SW_HIDE)`；显示 **`SW_SHOWNA`**（不抢焦点，这个细节很重要）
- 判态：`IsWindowVisible(lv)`

**路线 B：发系统命令让资源管理器自己切换（Hide-Icons 走这条）**

- `Hide Icons/main.cpp:344-375`（MIT, commit `583132b`）
  - 用 COM `CLSID_ShellWindows` + `FindWindowSW(SWC_DESKTOP, SWFO_NEEDDISPATCH)` 拿到真正的 `SHELLDLL_DefView`（比 `FindWindow` 可靠），失败再扫 Progman/WorkerW
  - 切换只发一句：`PostMessage(defView, WM_COMMAND, 29698, 0)`
- 状态回读：`main.cpp:104-114` 读 `HKCU\...\Explorer\Advanced\HideIcons`，**`val == 0` 表示可见**（即 `HideIcons=1` = 已隐藏）
- 还处理了 `RegisterWindowMessage("TaskbarCreated")`（explorer 重启后重挂）、单实例 Mutex、托盘、自更新

> **建议：两个都用。** 用 B 做实际切换（系统自己维护注册表状态，与右键菜单天然一致），用 A 的 `IsWindowVisible` 做状态校验兜底。两条路在我本机都验证了可行（见第四节）。

**双击判定（核心难点）**：`polterdesk/src/winapi_thread/hook.rs`
- `WH_MOUSE_LL` 低层鼠标钩子，全局
- 双击判定用 `GetDoubleClickTime()` + `GetSystemMetrics(SM_CXDOUBLECLK/SM_CYDOUBLECLK)`，**不要自己写死 500ms**
- "是否在桌面空白"：`WindowFromPoint` + 类名白名单 `{Progman, WorkerW, SysListView32, SHELLDLL_DefView}`
- 命中图标则放行（返回 `CallNextHookEx`），否则 `return LRESULT(1)` 吞掉双击
- 关键：钩子必须在**有消息泵的线程**上安装，否则会被系统静默摘除

### 3.2 功能② 跟随系统主题

- **写值**：`HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize` 下的 `AppsUseLightTheme` / `SystemUsesLightTheme`（1=浅色，0=深色）
- **广播通知**：`Windows_AutoTheme/src-tauri/src/lib.rs:49-66`（MIT）
  `SendMessageTimeoutW(HWND_BROADCAST, WM_SETTINGCHANGE, 0, L"ImmersiveColorSet", SMTO_ABORTIFHUNG, 1000, NULL)`
- **监听系统变化（关键）**：不要轮询。用 `RegNotifyChangeKeyValue` 监听 `Themes\Personalize` 键 —— 参考 `AutoDarkMode/AutoDarkModeSvc/Handlers/RegistryHandler.cs`（GPL，只读思路）

> ⚠️ **发现**：你参考的 Tuyang 那个工具，其本体 `Windows_AutoTheme` 实际是**用前端定时表判断**，并没有监听系统事件；而且它用了三层回退（`cmd /C start .theme` → PowerShell → 直接写注册表）来应用主题，说明作者自己也没找到稳定方案。**别把它的做法当正确答案。**

### 3.3 功能① 任务栏透明/亚克力 —— 这里必须换路线

**先纠正一个常见误解：TranslucentTB 在 Win11 上不是靠 `SetWindowCompositionAttribute` 实现的。**

- `TranslucentTB/TranslucentTB/taskbar/taskbarattributeworker.hpp` 里确实声明了 `PFN_SET_WINDOW_COMPOSITION_ATTRIBUTE`，但源码同时把任务栏分成三型（原文注释）：
  - `Classic` = Windows 10
  - `Mixed` = Win11 22000 / 早期 22621
  - `XAML` = Win11 22621+
- Win11 新版走的是 **XAML 分支**：注入 `ExplorerTAP` DLL，用 `Windows.UI.Xaml.Media.AcrylicBrush`（刻意选 `BackgroundSource = Backdrop` 而非 `HostBackdrop`），替换任务栏背景 `Shape` 元素的 `Fill`
- 官方 README 原文也写明：`**Blur**: ... **Windows 10 and Windows 11 build 22000 only.**`

**这条路有多重？** 架构师实测统计 TranslucentTB 的 `ExplorerTAP` 注入组件：源码约 **38KB**（`.cpp/.hpp`），加 IDL 与工程文件共约 **58KB**，还需要 11 个 `WinEventHook` + COM IDL + 跨进程契约 + explorer 崩溃恢复 + 卸载时主动杀 explorer。**这不是一个周末能做完的东西。**

**已知的坑（都已核实）：**
- XAML Diagnostics 是**单消费者**：Windhawk 的 `windows-11-taskbar-styler` README 明说它基于 TranslucentTB 的 ExplorerTAP，**同时只能有一个消费者**，与 TranslucentTB / ExplorerBlurMica 会冲突
- explorer 重启后效果丢失，需要重挂
- 未签名 DLL 注入会被 Defender/EDR 拦
- 卸载/更新注入 DLL 时，TranslucentTB 的做法是 `TerminateProcess(explorer)` 主动杀掉资源管理器

### 3.4 功能④ 设置面板 + 托盘 + 开机启动

- **托盘**：`Shell_NotifyIcon`（`NIM_ADD` / `NIM_SETVERSION` / `NIM_DELETE`），按 `NOTIFYICON_VERSION_4` 解析 lParam
- **explorer 重启重挂**：`RegisterWindowMessage(L"TaskbarCreated")`
- **单实例**：`CreateMutexA(0, FALSE, "Local\\<你的名字>_TrayApp")`
- **开机启动**：
  - 裸 exe → `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，**路径必须带引号**（带空格路径否则会被 `CreateProcess` 拆错），参考 `polterdesk/src/startup.rs:5-51`
  - MSIX 打包 → WinRT `StartupTask`，但这要求在 HKLM 写 4 个策略键，否则菜单项**会被灰掉**（TranslucentTB README 明写）
- **状态自愈**：`polterdesk/src/winapi_thread/mod.rs:197-234` 用 `SetTimer` 250ms 周期校验，防止"用户从右键菜单手动改了图标显隐，但你的程序状态没跟上"

### 3.5 工程化可借鉴（次要但省事）

- **配置用 JSON + JSON Schema**：TranslucentTB 仓库有 `settings.schema.json`（171 行），编辑器能补全、加载即校验、出错能定位到字段。可照抄字段语义：每个场景一套外观 + `battery_saver_appearance`（电池模式独立外观）+ `ignored_windows`（按窗口排除）+ `hide_tray` + `verbosity`
- **发布资产配 `.sha256`**：AutoDarkMode 每个 release 资产都配一个校验文件，低成本高信任
- **单一版本源**：AutoDarkMode 是反面教材 —— `version.xml` 写 `10.0.0.0` 而实际发布 `11.1.1.1`，每个 csproj 各写各的 `<Version>` 会漂移。用 `MinVer` / `Nerdbank.GitVersioning` 从 git tag 自动出
- **不要抄的**：TranslucentTB 用 Azure Pipelines（`.azp/ci.yml`），AutoDarkMode 用 `publish.bat` 手工批处理 —— 成熟产品未必用现代工具链，这是机会点不是模仿对象

---

## 四、本机实测裁定（协调者亲自跑，用于解决成员间冲突）

专家团成员之间出现了 **4 处实质性冲突**，我逐一在本机实测裁定：

| # | 冲突 | 成员说法 | 我的实测 | 裁定 |
|---|---|---|---|---|
| 1 | `MSTaskSwWClass` 是否存在 | 侦察员："不存在"；验收员："存在 hwnd=0x1014A" | `EnumChildWindows` 枚举到 `0x0001014A cls=MSTaskSwWClass`；但 `FindWindowExW(Shell_TrayWnd,'MSTaskSwWClass')` 返回 **NULL** | **验收员对**。侦察员用单层 `FindWindowExW` 查找，深层子窗口必须用 `EnumChildWindows` 递归 |
| 2 | `SysListView32` 可见性能否做判据 | 验收员 R4："恒为 False，绝不能用"；polterdesk 源码：正是用它 | 初始（图标已隐藏）`False` → `SW_SHOWNOACTIVATE` 后 **`True`** → `SW_HIDE` 后 **`False`** | **验收员错**。它只在"已隐藏"状态做了单点观测。`IsWindowVisible` 正常跟随，polterdesk 的做法是对的 |
| 3 | Win11 任务栏外观用什么 API | 综合师：`SetWindowCompositionAttribute`；架构师：XAML/TAP；验收员：SWCA 实测失效 | 采纳架构师 + 验收员（二者互补，不冲突） | **架构师最准**。综合师只看到 Classic 分支的声明，没看到 XAML 分支 |
| 4 | 功能③ "唯一精确命中 DeskHider" | 综合师：唯一命中 | 本地一手 README 显示 polterdesk 功能 3+4 齐全（MIT），Hide-Icons 也命中 | **综合师遗漏**。它自述受检索工具限制（`web_search` 无 API key、GitHub code search 需认证），漏掉了这两位侦察员找到的项目 |

**其他本机实测值：**

```
Windows 11 build 26200 (25H2)
Shell_TrayWnd = 0x00010136 (2560x48)
  ├─ Windows.UI.Composition.DesktopWindowContentBridge   ← XAML 岛证据
  ├─ MSTaskSwWClass 0x0001014A vis=True                  ← 任务栏按钮宿主
  └─ TrayNotifyWnd / ReBarWindow32 ...
Progman 0x0001015E
  ├─ SHELLDLL_DefView 0x00010168 vis=True
  └─ SysListView32    0x0001016A vis=False ('FolderView')

HideIcons = 1       (Int32，1=已隐藏)
TaskbarAl = 1       (Win11 任务栏居中开关)
AppsUseLightTheme = 0 / SystemUsesLightTheme = 0 / EnableTransparency = 1
```

**两个必须写进代码的坑：**
1. **判断 Windows 版本必须用 `CurrentBuild`，不能用 `ProductName`** —— 本机 `ProductName` 仍显示 "Windows 10 Enterprise"，但实际是 Win11 25H2（build 26200）。按名字分支会误入 Win10 路径。
2. **`FindWindowW("Progman", NULL)` 在本机匹配不到**，必须带窗口标题 `FindWindowW("Progman", "Program Manager")`。

**工具链现状（实测）：**
- `dotnet --list-sdks` → **空**（只有运行时 `Microsoft.WindowsDesktop.App 8.0.31`）→ 能跑成品，**不能构建**
- `rustc` / `cargo` → 不存在
- Node v24.19.0 / npm / pnpm / git → 齐备

> 也就是说：**在装 .NET 8 SDK 之前，一行 C# 也编译不出来。** 这是开工第一天就会撞上的墙。

---

## 五、落地方案

### 5.1 技术选型：为什么是 C#/.NET 8 + WPF

分水岭只有一个：**你是否自研功能①。**

| 路线 | 适合场景 | 代价 |
|---|---|---|
| **C# / .NET 8 + WPF + `H.NotifyIcon`** ✅ 推荐 | **不自研任务栏注入**，功能②③④全覆盖 | 托盘常驻 60–120MB 内存（未实测）；设置面板最省力 |
| C++ / Win32 | 确定要自研任务栏注入 | 参 TranslucentTB 的规模：注入 DLL + COM IDL + 11 个 WinEventHook |
| Rust + `windows-rs` | 想要极低内存 + 直接复用 polterdesk 的 MIT 代码 | gpui 类 UI 生态薄，设置面板要自己画 |
| Tauri | ✗ 不推荐 | 常驻会带 WebView2 额外进程，与"低内存常驻托盘"目标相反 |
| Electron | ✗ 不推荐 | 常驻内存与需求直接冲突 |

**推荐理由：** 功能②③④本质是 Win32 消息泵 + 注册表 + 钩子，**不需要 Web 技术栈**。C# 写托盘、设置窗、注册表、P/Invoke 都是最省力的，且有 AutoDarkMode 同栈参照（但它是 GPL，同栈 ≠ 可抄代码）。

**任务栏透明/亚克力怎么办？** 三条路，按推荐顺序：

1. **Phase 1 不自研，给指引**：检测用户是否装了 TranslucentTB / Windhawk，在设置面板里放一个"任务栏透明/亚克力"入口，点击跳转或提示安装。**这能让你在两周内交付一个四项功能齐全的产品。**
2. **降级承诺**：只承诺 Win10 + Win11 22000 的效果（走 `SetWindowCompositionAttribute`），新版本 Win11 明确标注不支持。
3. **Phase 2 再自研注入**：如果一定要，先写一份 ADR，明确注入机制、GPL 边界、单消费者冲突处置，预算按 3–5 倍工作量估。

### 5.2 架构：按"实现机制"分三层，不要按 UI 邻近性塞一个栈

| 层 | 功能 | 机制 | 风险 |
|---|---|---|---|
| **A 层** | 任务栏按钮居中、主题跟随、开机自启、托盘、图标显隐开关 | 注册表读写 + 注册表变更通知 + `Shell_NotifyIcon` | 极低 |
| **B 层** | 双击桌面空白切换图标显隐 | 全局低级鼠标钩子 + 桌面窗口定位 + 窗口显隐 | 中 |
| **C 层** | 任务栏透明 / 亚克力 | DLL 注入 explorer + XAML + 替换 Brush | 高 |

### 5.3 实施顺序

**P0（先做，本机就能验收）**
1. 装 `.NET 8 SDK`，跑通 `dotnet new wpf` + `dotnet build`。**这一步不做，后面全是空的。**
2. 建单进程托盘工程，接入 `H.NotifyIcon`（MIT）
3. 做 A 层四项：开机自启、主题跟随（注册表变更通知，非轮询）、托盘可隐藏且能从 exe 呼出、桌面图标手动显隐开关

**P1（B 层，路径已实测）**
4. 封装窗口定位：**只用 `EnumChildWindows`**，禁止单层 `FindWindowExW`；判断 Windows 版本**用 `CurrentBuild` 不用 `ProductName`**
5. 双击切换：`WH_MOUSE_LL` + 类名白名单 + 图标命中排除 + `PostMessage(DefView, WM_COMMAND, 29698, 0)`
6. explorer 重启重挂：`RegisterWindowMessage("TaskbarCreated")`

**P2（C 层，明确决策后再动）**
7. 先做功能①的可行性 spike，再决定是否投入

---

## 六、验收清单

> 这不是"已执行的测试"，是给你的验收步骤。**每项都必须在你自己的 Win11 26200 上通过。**

### D1 构建
- 前置：已装 .NET 8 SDK
- 命令：`dotnet --list-sdks` → 应出现 `8.0.x`
- 回归：`dotnet new wpf -o demo && cd demo && dotnet build -c Release` → 退出码 0

### D2 任务栏透明/亚克力
- 若走"指引第三方"路线：安装 TranslucentTB 后，确认它生效 **且与你的程序同时运行不冲突**（这验证了单消费者互斥风险）
- 若自研：用像素对照法验证 —— 改前后截任务栏同一区域，逐通道差异必须 **显著大于同状态连拍两帧的噪声**（本机噪声底 ≈ 0.021 MAD）
- ⚠️ **判据陷阱**：`SetWindowCompositionAttribute` 无论成败都返回 0，`GetLastError` 也不可靠。**唯一可信判据是像素对照。** 本机实测 ACRYLIC 状态像素差异仅 0.024（≈噪声），即**已失效**

### D3 任务栏居中
- 命令：`reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v TaskbarAl` → 确认写入
- 手测：改 0/1，观察按钮位置；**记录是否需重启 explorer 才生效**（本机未验证此项）

### D4 跟随系统主题
- 手测：设置→个性化→颜色，手动切深/浅色，程序应在 1 个广播周期内跟随
- 回归：浅→深→浅 连续切 3 次无残留；常驻 CPU 占用应 ≈ 0（证明是事件监听而非轮询）

### D5 双击显隐图标
- 三组手测：①桌面空白双击 → 隐藏 ②再双击 → 恢复 ③**在图标上双击 → 必须不触发**（应正常打开该图标）
- 回归：隐藏后按 F5 刷新桌面、右键桌面、重启 explorer，状态与注册表 `HideIcons` 必须一致
- ⚠️ **禁止出现"图标消失且无法恢复"** —— 这是最容易被用户差评的失败模式

### D6 托盘与开机启动
- 托盘图标显隐开关双向生效；关闭托盘后，从 exe 能呼出设置窗
- 开机启动：注销重登后进程确实启动（任务管理器确认），**不是靠注册表残留的假象**
- 若走 MSIX：先验证 StartupTask 不被策略灰掉

### D7 许可证合规
- 依赖树扫描，白名单 MIT / Apache-2.0 / BSD / ISC
- **输出中不得出现 GPL-2.0 / GPL-3.0 / AGPL-3.0 / NOASSERTION**
- 本报告涉及 GPL 项目（TranslucentTB、AutoDarkMode、ZenDesktop、Windhawk、RoundedTB、ExplorerPatcher）**只描述思路，未提供可粘贴实现**

---

## 七、未覆盖范围与数据缺口（诚实声明）

- **没有任何构建实测**：本机无 .NET SDK / 无 Rust / 无 MSVC，所有耗时、体积、内存数字均为空缺或建议用例。
- **只覆盖单屏**：本机 `SM_CMONITORS=1`，副任务栏 / 多显示器逻辑无法验证。
- **没有 Windows 10 机器**：Win10 相关结论（`SetWindowCompositionAttribute` 可用、TaskbarX 式居中）全部停留在文档层面。
- **`web_search` 全程不可用**（无 API key），GitHub code search 需认证未执行 → 可能存在中文社区或近期新仓库漏检。
- **未能核验的项**：`MicaForEveryone` 的 HEAD commit（默认分支非 main）；`limbo666/DesktopFramesPlus` 的真实 license（API 报 `Other`，README 自称 MIT，未读 LICENSE 本体）；`TaskbarAl` 写入是否即时生效；低级鼠标钩子在全屏游戏下的性能。
- **安全提醒**：侦察过程中曾把 GitHub token 落盘到 `recon/.token`，**已删除**。该 token 值曾进入会话记录，**建议你到 GitHub 设置里吊销并重新生成**。

---

## 附：本次调研的原始材料

- 5 份专家报告全文：`recon/experts/`
- 本机实测记录：`recon/VERIFICATION-REALITY-CHECK.md`
- 窗口树/注册表探测脚本：`recon/probe-*.ps1`、`recon/peek*.ps1`（可直接重跑复现第四节全部数据）
- 源码快照：`recon/src/`（polterdesk、Hide-Icons、TranslucentTB、AutoDarkMode、Windows_AutoTheme、ZenDesktop）
- 截屏证据：`recon/evidence/`

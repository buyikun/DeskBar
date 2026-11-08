---
name: sync-frontend
description: Use in the DeskBar settings-UI session when the user says "同步一下" / "同步前端" / "检查有没有新改动", or when another session may have changed backend code (Services, Interop, App.xaml.cs, AppSettings.cs) and the settings window, XAML, or code-behind may need to follow. Detects backend changes since the last sync baseline, decides which ones affect the UI, and applies the UI-side follow-up.
---

# DeskBar 跨会话前端同步

本项目有两个会话分工：**后端会话**改 `src/DeskBar/Services/**`、`Interop/**`、`App.xaml.cs`；**前端会话**（本会话）负责 `src/DeskBar/Views/**`、设置界面与文案。

DSH 的不同 root 会话之间**没有**消息通道（`agentTeams` 与 `subagents` 都只在自己的 Lead 树内），所以同步只能靠工作区里的共享状态自检，不能靠对方主动通知。

## 第 1 步：跑检测

```bash
cd /d/ai/deskbar && powershell -NoProfile -ExecutionPolicy Bypass -File sync-frontend.ps1
```

输出解读：

| 输出行 | 含义 | 要不要动手 |
| --- | --- | --- |
| `BACKEND: no change since baseline.` | 后端自上次同步后没动过 | 不用，直接汇报「已是最新」 |
| `BACKEND: N file(s) changed` + `[modified] path` | 后端有文件变化 | 进第 2 步逐个判断 |
| `SETTINGS CONTRACT: [+] public bool Xxx { get; set; }` | 设置模型新增了字段 | **几乎一定要动前端**：新字段没有对应 UI 控件 |
| `SETTINGS CONTRACT: [-] ...` | 设置模型删了字段 | **一定要动前端**：删掉绑定它的控件，否则留死控件 |
| `TONE CONTRACT: [+] "xxx"` | 新增了材质色调键 | 三处色调下拉（外观页、白天方案、夜晚方案）都要加项 |
| `OVERLAP WARNING: N UI file(s)` | 前端文件被其它会话改过 | **先别动**，先读那几处 diff 看是不是已经改过了，避免覆盖别人 |
| `RESULT: follow-up required.` | 至少一项需要关注 | 继续 |
| `RESULT: up to date, nothing to follow up.` | 干净 | 收工 |

`-Init` 是重设基线（只在确认当前状态就是「已同步」时用）。基线存在 `D:\ai\deskbar\.sync\baseline.json`，已在 `.gitignore` 里。

## 第 2 步：判断哪些改动影响前端

**只看 diff，不要凭文件名猜。** 对每个 `[modified]` 的后端文件：

```bash
cd /d/ai/deskbar && git diff --stat -- <path>   # 先看规模
```

然后读实际改动。按下面三档判：

**A. 必须跟（影响界面显示或交互）**
- `AppSettings.cs` 加/删/改字段 → 设置窗口的控件、`LoadFromSettings()`、`Persist()` 都要跟
- `AppSettings.IsKnownTone` 变 → 三处色调下拉的候选项
- `ThemeService` 的公开属性/事件签名变 → `SettingsWindow.xaml.cs` 里 `UpdateThemeStatus()`、`OnApplyCurrentPlanNow()` 等直接调用点
- `App.xaml.cs` 里 `internal` 成员变（`EffectiveTone`、`ApplyToneNow`、`ApplyToneForCurrentPlan`、`SaveSettings`）→ code-behind 调用点
- `NativeTaskbarAppearanceService.GetColorForTone` 的颜色值变 → 色块 `ApplySwatch()` 会自动跟随（它直接调该方法），但下拉文案里写死的覆盖率百分比（0%/15%/14%/63%/56%）要同步改
- 任何用户能看见的字符串变化（提示语、错误文案）

**B. 不用跟（后端内部实现）**
- 纯算法/性能重构、日志打点、`TaskbarService` 内部几何计算
- `Interop/NativeMethods.cs` 加 P/Invoke 声明（前端不直接调）
- 注释、`using`、格式调整

**C. 要改文案的灰色地带**
后端行为语义变了但接口没变（例如降级条件从「非管理员」改成「DLL 缺失」）→ 改 `UpdateRuntimeHints()` 里的提示语，别动结构。

## 第 3 步：动前端

遵守 `AGENTS.md` 第 3.1 节：**用唯一字面量替换并断言命中 1 次**，禁止跨行贪婪正则改 C#。改 XAML 属性时 `old_string` 必须包含整行的全部保留属性（曾因只写 `Content="..." Tag="..."` 当 old_string，把 `Content` 连带删掉，产生 3 个空 `<ComboBoxItem>`）。

改完做静态自检（两条都必须为空）：

```bash
cd /d/ai/deskbar
# 1) XAML 里的 x:Name 是否覆盖 code-behind 用到的全部标识符
grep -oE 'x:Name="[A-Za-z0-9_]+"' src/DeskBar/Views/SettingsWindow.xaml | sed 's/x:Name="//;s/"//' | sort -u > .tmp/xn.txt
grep -oE '\b(Chk|Txt|Cmb|Sld|Btn|Rdo|Bd|Page)[A-Za-z0-9_]+' src/DeskBar/Views/SettingsWindow.xaml.cs | sort -u > .tmp/cn.txt
comm -13 .tmp/xn.txt .tmp/cn.txt     # 必须为空

# 2) 事件处理器双向对齐
diff <(grep -oE '"On[A-Za-z0-9_]+"' src/DeskBar/Views/SettingsWindow.xaml | tr -d '"' | sort -u) \
     <(grep -oE 'private void On[A-Za-z0-9_]+' src/DeskBar/Views/SettingsWindow.xaml.cs | sed 's/private void //' | sort -u)
```

第 2 条的差集允许出现 `OnWhitelistPresetClick`（它是 `chip.Click += OnWhitelistPresetClick;` 动态挂载的，不是孤儿）。

## 第 4 步：闭环验证

`AGENTS.md` 强制完整链条，**不许只 build 不 publish**：

```bash
cd /d/ai/deskbar
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1      # 必须「已成功生成。」
powershell -NoProfile -ExecutionPolicy Bypass -File publish.ps1    # 输出到 out_publish\
bash deploy.sh                                                     # 复制到 D:\app\DeskBar 并启动
```

编译若报 `error MSB3883: 意外的异常: 拒绝访问。`，那是 Windows 文件 ACL 缺 `WRITE_OWNER`，不是代码错——用 `diagnose-windows-sandbox-acl` 技能修，别去猜代码。

部署后截图验证 UI（`shot_nav.ps1` 按导航名切页）：

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File shot_nav.ps1 -Out D:\ai\deskbar\shots\sync.png -Nav "自动主题"
```

## 第 5 步：更新基线

**只有在前端确已跟进完毕后才执行**：

```bash
cd /d/ai/deskbar && powershell -NoProfile -ExecutionPolicy Bypass -File sync-frontend.ps1 -Init
```

提前 `-Init` 会让那次变更永远查不出来。

## 汇报格式

向用户说清三件事：变了什么（文件 + 行号）、判为「要跟/不跟」的理由、前端实际改了什么或为什么没改。别只说「已同步」。

## 坑

- **PowerShell 5.1 单元素数组坍缩**：管道只返回一行时，`@(...)` 不包围就拿到裸对象，`.Count` 为空字符串，导致判断恒假。脚本里所有 delta 赋值都已用 `@()` 包住，改动时别拆掉。
- **`.ps1` 不许有非 ASCII 字节**：无 BOM 的 UTF-8 脚本会被按 ANSI 解码，中文注释直接 `ParserError`。中文只许出现在传给 .NET 的字符串参数里。
- **真实配置在 `%APPDATA%\DeskBar\settings.json`**（`C:\Users\Administrator\AppData\Roaming\DeskBar\`），仓库内 `.appdata\` 只在构建脚本重定向环境变量时用，改它不影响运行中的 DeskBar。
- **`python` / `python3` 在此环境不可用**（`EXIT=49` 无输出）。用 PowerShell 或编辑工具。

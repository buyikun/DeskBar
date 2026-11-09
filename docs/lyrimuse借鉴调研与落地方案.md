# lyrimuse 借鉴调研与落地方案

> 任务来源：用户要求「学习 https://github.com/Yudaotor/lyrimuse，看看有没有值得借鉴的」。
> 调研方式：取真实源码通读 + 五位领域专家分工核验 + 协调者逐条自查承重引用。
> 日期：2026-11-10。**本文只描述判据与设计思路，不给出可粘贴的第三方实现。**
> 校准基准：`AGENTS.md`（DeskBar 的单一事实来源）。

---

## 零、一句话结论

**lyrimuse 值得读，但它不是 DeskBar 最该借鉴的对象；本次最有价值的产出也不是「从它那里搬什么」，而是它像一面镜子，照出了 DeskBar 歌词路径上三条自己早就该补、而且在封面路径上已经补过的洞，外加一个代码级确证的 KRC 解析缺陷。**

---

## 一、证据基线（所有引用可核对）

### 1.1 快照锚定

| 项 | 值 |
| --- | --- |
| 仓库 | `Yudaotor/lyrimuse` |
| 快照 commit | **`9972c94d949c290cee599025d88e9cbb5134b837`**（2026-11-01T09:54:13Z，默认分支 `main`） |
| 本地快照根 | `D:\ai\deskbar\.scratch\refs\lyrimuse\`（**无 `.git`**，由 codeload tarball 解压，33,522,851 字节） |
| 许可 | **GPL-3.0**（`LICENSE:1-3`，675 行全文；全仓 `grep "any later version"` **0 命中** → 按 GPL-3.0-only 读更安全） |
| 元数据 | 350 stars / 19 forks / 10 open issues；created_at **2026-07-10**（截至快照仅 **4 个月**，无长期生产验证） |

**本文所有 `lyrimuse-engine/...`、`lyrimuse/...`、`shared/...` 形式的引用，均相对上述快照根**；换成 `D:\ai\deskbar\.scratch\refs\lyrimuse\<路径>` 即可打开对应行。核验方法：用 `https://raw.githubusercontent.com/Yudaotor/lyrimuse/9972c94d949c290cee599025d88e9cbb5134b837/<路径>` 打开同一行。

### 1.2 两条引用纪律（本次踩到过）

- **不要用 `pushed_at` 推断代码日期。** 该字段为 `2026-11-09`，但默认分支最新 commit 是 **2026-11-01**。`pushed_at` 涵盖所有分支与引用的推送，用它说明「代码是 11-09 的」是误读。**引用只用 sha + commit 日期。**
- **brief 里的文件名勘误**：`lyricstimelineoffset.go` **不存在**，实际是 `lyrictimelineoffset.go`（499 行）；逐字格式文件是 `lyricswordformat.go`（注意是 word**s**）。照抄错误的文件名会直接 MISS。

### 1.3 本机环境实测（2026-11-10）

| 项 | 实测 |
| --- | --- |
| 工具链 | `go` → command not found；`swift` → command not found；`node` → v24.19.0；`.dotnet/dotnet.exe --version` → **8.0.427** |
| 网络（命令行） | `curl` → `schannel: SEC_E_NO_CREDENTIALS (0x8009030e)`；`git ls-remote` → `cannot create standard input pipe for remote-https: Permission denied` |
| 网络（工具） | `web_fetch` **可用**（`api.github.com` / `raw.githubusercontent.com` 均 HTTP 200）—— 命令行被拦不代表网络不可用 |

**推论**：`lyrimuse-engine` 共 **3803 个 `func Test`**，其中 **181 个测试文件引用真实网络主机** → 即便装上 Go 也**不可能全绿**；且 `go.mod` 声明 `toolchain go1.24.4`，离线需 `GOTOOLCHAIN=local`。**「它有测试」不等于「测试是绿的」—— 本次未运行任何测试。**

### 1.4 DeskBar 侧的两条前置事实

- **DeskBar 仓库根目录没有 `LICENSE` / `COPYING`**（`ls` + `git ls-files` + README 三重确认）→ 无许可 = 默认保留全部权利。**抄 GPL-3.0 代码是双向违规**，且 `AGENTS.md` §1.2 的零 NuGet 红线也让「包引用」这条路不通。**唯一合法路径：只借判据 / 阈值 / 接口形状，自行重写。**
- 全面只读：本次未修改 DeskBar 任何源码或 `AGENTS.md`，未执行 `build` / `publish` / `deploy`，未 clone 任何仓库。唯一写入是只读探针 `.scratch/realitycheck/krc_regex_probe.ps1`。

### 1.5 lyrimuse 的依赖与合规（可复用的方法论）

- **Go 引擎零第三方依赖**：`lyrimuse-engine/go.mod` 仅 16 行、**无 `require`**；`go.sum` **0 字节**。全仓 11 处 `github.com/` `golang.org/x/` 命中**全是注释、UA 字符串或命名空间常量**（逐条核过：`fold.go:16`、`lrclib.go:226`、`lyricswordformat.go:344`、`musicbrainz.go:899`、`t2s.go:12-16`）。
- Swift 侧 `Package.resolved` 仅 2 条：KeyboardShortcuts 1.15.0（MIT）、Sparkle 2.9.4（MIT）。
- `THIRD_PARTY_LICENSES:1-24` 清单**无 GPL/AGPL 传染项**；且项目**主动放弃** `gocc`（其传递依赖 `cedar-go` 为 GPL-2.0-only，与 GPL-3.0 不兼容）——证据见 `t2s.go:12-16`、`match.go:2222`、`THIRD_PARTY_LICENSES:225-227`。
- **可复用的合规方法论**：维护一份「第三方许可清单 + 在代码注释里写明为何弃用某个依赖」，把许可判断落在仓库内而不是某个人脑子里。

---

## 二、结论先行

### 2.1 关于 lyrimuse 本身

1. **可借鉴的是判据、阈值与边界处理，不是功能。** 它的 Go 引擎与 Swift 前端一律不移植（GPL-3.0 + 语言不同 + 规模差 30×）。
2. **它最有价值的部分恰恰是 DeskBar 不该学的。** `match.go` 4,425 行 / 14 个 `scoreTerm` 解的是「多源打分排序选冠军」；而 DeskBar 的产品决策是「候选不唯一就宁可无歌词」。**这是两个相反的问题。**
3. 它真正值得借的是**失败处理**这一个维度，以及若干**否证性判据**（重扣分而非一票否决、批级「整批撤销」、借封面必须 ISRC 相同）。

### 2.2 一个必须写在前面的决定性冲突

lyrimuse 的作者**自己承认**它选的是「宁可显示错的」：

> `lyrimuse-engine/match.go:673-674` 原文：
> 「代价说清楚:真正串错版本的候选(这道闸门当初就是为它设的)现在会在「只有它一条」时被选中,也就是**「宁可显示错的,不要没有」**。数据支持这个取舍 —— 误杀是 5:1,但它确实是个取舍。」

而 DeskBar 的硬约束是反过来的：

> `AGENTS.md:186`：「**歌词匹配不得放宽「候选唯一」判据**。歧义时无歌词，也不要串到翻唱/同名版本。」

**裁定：lyrimuse 的打分/择优机制不可移植到 DeskBar，本文不给出任何「改用打分选最高分」的建议。**（另注：`match.go:665` 的「误杀 5:1」样本仅 **n=6**，即便在 lyrimuse 内部也不足以支撑一般化结论。）

### 2.3 本次调研最高价值的三条产出

| # | 产出 | 性质 |
| --- | --- | --- |
| 1 | **DeskBar 歌词路径缺失败处理**（每 3 秒重跑完整回退链 / 赋值无曲目校验 / 无在途去重）—— 而封面路径**已经做对了** | 代码级确证，**未实机复现** |
| 2 | **KRC 负词始偏移解析缺陷**（丢词 / 标记被当歌词显示） | 代码级确证 + 离线实测复现；触发依赖数据 |
| 3 | **两个比 lyrimuse 更贴合 DeskBar 的借鉴对象**（C# / 宽松许可 / 领域重合） | 仓库级事实 |

---

## 三、lyrimuse 是什么（规模与架构判定）

### 3.1 结构

```
lyrimuse-engine/                      Go 引擎（390 个非测试 .go + 627 个测试 .go，5.34 MB）
lyrimuse/Sources/LyrimuseCore/        Swift 核心（Lyrics / Playback / Networking / Local / Models / Integrations）
lyrimuse/Sources/lyrimuse/            Swift UI（LyricsManager / MenuBar / WebLyrics / UI / TouchBar）
shared/                               players.json、settings-catalog.json、feature-list.json、testdata
scripts/  telemetry-worker/  docs/
```

### 3.2 规模对照（歌词流水线）

| 项 | lyrimuse | DeskBar 对等物 | 倍差 |
| --- | --- | --- | --- |
| 产品源码 | 22.47 MB（Go 5.34 + Swift 17.13） | 全仓 `.cs` + XAML ≈ 0.75 MB | ~30× |
| 歌词流水线 | ~12,027 行 | `LyricService.cs` 1,050 行 | 11.5× |
| 匹配打分 | 4,425 行 / 140 个顶层函数 / 14 个 `scoreTerm` | `LyricService.cs:837-878` 42 行 | ~105× |
| 源健康统计 | 1,020 行 + **必须落盘** | 无 | — |
| 封面侧 | 3,786 行 | `SafeArtwork.cs` 291 + `MediaCacheLocator.cs` 484 = 775 行 | 4.9× |
| 测试 | 627 个测试文件 / 5.30 MB | **0**（无框架） | — |

### 3.3 四个维度的判定：只有「失败处理」有真实差距

| 维度 | lyrimuse | DeskBar | 判定 |
| --- | --- | --- | --- |
| 领域模型 | `enrich.go:132` `enrichEntry`，16+ 字段（歌词/译文/罗马音/多平台 URL/否决记录） | `LyricService.cs:136` `TrackLyrics`，5 字段 | 不展示译文/罗马音/多平台链接，**简化正确，不改** |
| 模块边界 | 引擎 1017 个 `.go` **全是 `package main`**（零编译期边界），纪律靠 627 个测试维持 | `LyricService.cs:164` 静态类，无 DI | lyrimuse 的「模块化」是名义上的；DeskBar 引入分层是**纯负债，不改** |
| 扩展点 | 加源 = 改 `sourcebreaker.go:345` 主机名 switch；加播放器 = 改 `shared/players.json` + 跑生成器 | 加源 = 改 `LyricService.cs:193-211` 的 kind switch | 两边同为「改中心 switch」。`players.json` 解的是 **Go/Swift 双语言同步**，DeskBar 单语言单仓库**该问题不存在** |
| **失败处理** | **8 层**（见 4.3） | **1 层**（6s 超时）+ 串行回退，**无失败记忆、无去重、无目标校验** | **唯一该借鉴的维度** |

### 3.4 过度设计的量化：完整移植不成立

完整移植 lyrimuse 的失败处理体系 ≈ **1,500–2,500 行 C#**，外加一个**必须落盘**的统计文件，外加 13+ 个源的维护量（DeskBar 只有网易云 + QQ 两个在线源）。
**最小可行借鉴集 ≈ 75 行。** 两者差 20–30 倍——这就是「借判据」与「搬系统」的分界线。

---

## 四、可借鉴清单（按可落地性分级）

### 4.1 A 级：可照搬判据（代价低、零依赖、零磁盘写）

**A-1｜KRC 负词始偏移**（本次唯一有完整回归预言机的项，详见第五章）

**A-2｜「负结果也要有记忆」** —— 见第五章 R1。DeskBar 内部**已有先例**：`LyricService.cs:746` `CoverMissTtlMs = 5 * 60 * 1000`、`:747` `CoverCacheLimit = 64`、`:749` `CoverCacheEntry(long At, byte[]? Bytes)`。歌词路径照这个形状补即可，属于**一致性修复**而非新机制。

**A-3｜失败原因用稳定代码**（`lyricsourcefailure.go:25,34,40,43,46,54,67,70,73,81`）
把「失败」拆成可机读的稳定字符串，让诊断文案与判定逻辑解耦。**三条被证伪/踩过的坑值得直接吸收**：
- `:34` 记录了**已被证伪的 `region_restricted` 误判史**；
- `:40` 明确「用户没连 Apple Music」**不是故障**；
- `:81` 明确「一个请求都没发」也必须归因，否则会被算进「其余 N 个源」而掩盖真故障。
配套判据：`server_error` **只算 5xx**（4xx 是源在正常说话）。

**A-4｜超时分层：每源超时 + 一轮总截止**（`lyricsourcetimeouts.go:8-25` 各源 4s–10s 分档；`enrich.go:4496` `lyricSearchDeadline = 20 * time.Second`）
双层比单层好，但 **DeskBar 的总截止建议 ≤6 秒（沿用现有超时），不要照搬 20 秒**——DeskBar 是 100ms 歌词刷新 + 3s 轮询的交互式 UI，20 秒无意义。

**A-5｜熔断要「连 2 次失败才开闸」，且取消不计入熔断**（`sourcebreaker.go:21-23`、`enrich.go:6043`）
理由经得起推敲：一次网络抖动不该让下一首歌少一个源；而「同一首歌自己就能触发熔断」正是 1 次开闸的失败模式。退避表 `sourcebreaker.go:58` = `{15s, 30s, 1m, 2m, 5m}` 封顶 5 分钟，使「误熔断的代价有界」。**DeskBar 若做熔断，这两条判据是核心**（但 DeskBar 只有 2 个源，优先级低于 R1–R3）。

**A-6｜`players.json` 的「播放器怪癖布尔字段」形状**（`shared/players.json:2-13` 注释 + `:15-40` 字段）
每条记录含 `republishesZeroAnchor` / `playingFromRate` / `artistArrivesLate` / `artistlessNotMusic` / `dropsSessionBetweenTracks` / `dropsSessionWhilePlaying` / `ignoresSeekCommand` 等字段——**把每个播放器的 MediaSession 怪癖显式化成命名布尔量**。DeskBar 的对应面是 `MediaWhitelist` + `MediaSourceRouter`。
**可照搬的是「清单形状」，不是「代码生成器」**（它的生成器 + CI `--check` 解的是 Go/Swift 双语言同步，DeskBar 单语言单侧无此问题——见 `players.json:2-13` 注释自述「此前信息散在 Swift 3 个文件 + Go 4 个文件，每处写『必须同步维护』，注释拦不住漏改」）。

**A-7｜封面判据是「是不是同一张」而不是「谁更大」**（`coverquality.go:1-40`）
原案例：同专辑 8 首都是 800×800，唯标题曲是 120×120，**而设备那张 120×120 是对的，QQ 那张高清图是挂错的**（该专辑 25 首曲目共用同一个错 URL）。这条**直接支持 DeskBar 现有的「宁缺勿错」哲学**，可作为「不要盲目升级封面清晰度」的论据。

**A-8｜借封面必须 ISRC 相同**（`borrowcover.go:7,28-30`）
注解写明：歌名/歌手/专辑/时长**全对得上也不算**，不借。与 DeskBar「候选唯一」同一哲学。**但 DeskBar 当前拿不到 ISRC**（GSMTC `props` 是否提供未核实）→ 记为**待评估**。

**A-9｜只读图片头部量尺寸**（`coveredge.go:20,31,49,105`）
只读前 512KB 交给 `image.DecodeConfig`，**解得即止不下载整图**。.NET 对应 `BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None)` 只读头部拿 `PixelWidth/PixelHeight`，零依赖。**注意**：DeskBar 的封面已经经 `SafeArtwork.DownloadAsync` 限制 ≤1MB（`AGENTS.md` §4.6），**头部读取的收益有限**，记为低优先。

**A-10｜逐字填色**：**不要**改成「预算进度 + 定时补间」（`LyricSyncEngine.swift:3-17`）
原文告诫：补间比 tick 长时，上一段还没放完就被重新触发。
**对 DeskBar 的裁定：这不是 bug，而是一条论据。** DeskBar 现状是**按当前时间重算宽度**（`LyricService.cs:75 GetProgress`、`:108 GetPlayedWidth`，由 16ms 定时器驱动），走的正是「定期重算」这条路。**结论：保留现状，不要为了「更平滑」改成 WPF Storyboard 补间。**
同文件另记录两个 DeskBar **尚未实现**的能力，可作路线图参考：`LyricSyncEngine.swift:27-41` 的罗马音**分组填色**（分词器片段边界与逐字切分不一定对齐，须按片段并组）、`:43-55` 的 `side` 对唱左右分栏（`side == nil` 表示**这首歌没有演唱者标记**，不是「靠左」）。

### 4.2 B 级：需改造后才能用

**B-1｜文本规范化**（`lyriclineendings.go:19-28`、`lyrictextencoding.go:24-27,29-63,122-138`）
- **可照搬**：剥 BOM、CRLF/CR → LF、去控制字符（U+0000–U+001F 保留 NL/CR/TAB，另去 U+007F）。
- 现场数据：酷狗 3509 条中 3494 条 CRLF、1198 条带 BOM（`lyriclineendings.go:8-17`）。
- **必须改**：`lyrictextencoding.go:114-120` 的 GB18030/Big5 转码依赖系统 `iconv` **子进程** → .NET 要用 `Encoding.GetEncoding(54936)` / `950`，而 .NET Core **默认不带** `CodePagesEncodingProvider`。**不得引入子进程**（违反零依赖）。
- 一条反直觉判据：**按行判编码，不整份转**（`:29-63`）——整份转会把 UTF-8 的中文歌手名转乱；`looksLikeBig5` 的判据在 `:72-106`。
- 另一条：**别用 `strings.ToValidUTF8` 处理非法字节**（`:140-158`）——它把连续非法字节只换成一个，与落盘结果对不上，会导致校验值永远不匹配、每次启动都判损坏。**DeskBar 无落盘校验，此条不适用，但教训值得记。**

**B-2｜匹配归一化补强 —— 一条被核实的冲突，以实测为准**
`lyrimuse-engine/fold.go:23-53`（变音符号表）、`:104-120`（全角字母数字 → 半角，**全角标点不动**）；`hanvariants.go:8-30,38-60`（异体字 `妳/祂/牠`）。

DeskBar 现状实读（`LyricService.cs:889-894`）：

```csharp
string folded = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
return NonAlphaNumericRegex.Replace(folded, string.Empty);   // :896 保留 \p{L} 与 \p{N}
```

**裁定（本次核实的成员冲突）**：
- 「DeskBar 没有全角折叠」的说法**不成立**——`FormKC`（NFKC）做兼容分解，全角字母数字**已被折叠**，`:884` 的注释也写着「NFKC 折叠全半角」。
- **变音折叠是真实缺口**：NFKC **不**分解预组合字符，`Beyoncé` → `beyoncé`，而 `Beyonce` → `beyonce`，两者**不相等**。修法：`string.Normalize(FormD)` 去掉组合符号 + 一张 `ß/ø/æ/đ` 小表。**代价低**。
- **异体字是中文曲库的真实缺口**（`妳听得到` 类歌名三家全查不到，A/B 实测见 `hanvariants.go:14-18`），但需要自带数据表，**表体积未测量**。
- **落地建议：保守**。若要做，只做变音折叠，且**绝不放宽 `PickUniqueCandidate`**（`LyricService.cs:837-878`）。异体字表等有实测样本再评估。

**B-3｜在途并发上限「按源不按主机」**（`lyricsourceinflight.go:13,17,24-53`）
判据：令牌桶管「每秒发多少」，**管不住「同一瞬间有多少在飞」**；而同一源的不同备用主机背后是同一套服务，所以按**源**计数。DeskBar 目前是单源顺序取词，**只有在引入并发后才有意义** → 与 R3/P2 绑定。

### 4.3 参考：lyrimuse 失败处理的 8 层（全部已读，带位置）

| 层 | 位置 | 判据/常量 |
| --- | --- | --- |
| 单请求超时 | `lyricsourcetimeouts.go` | 各源 4s–10s 分档 |
| 一轮总截止 | `enrich.go:4496` | `lyricSearchDeadline = 20s` |
| 在途并发名额 | `lyricsourceinflight.go:13,17` | `{"qq":4,"lrclib":3,"musixmatch":3}`，默认 6；**按源不按主机** |
| 同 URL single-flight | `httpcoalesce.go:24,46-66,69,90-93,112-124` | 只合并**同时在途**（不缓存）；`leaderCanceled` → 等待者各自重发；body > 8 MB 不共享 |
| 按源熔断 + 有界退避 | `sourcebreaker.go:21-23,58` | 连 2 次才开闸；退避封顶 5 分钟 |
| 失败记忆 + 自动略过 | `lyricsretryskip.go:14` | `lyricsNoAnchorGiveUpCount = 3`；**手动重试不受影响** |
| 取消 | `enrichcancel.go` | 1s ticker 读取消文件，真中断在飞请求 |
| 目标身份校验 | `earlylyrics.go:146-154` | 落地前校验「这次解析的那一首此刻是否仍在播」 |

**最值得单独记的一段**（`enrich.go:6036-6040`）：

> 「全部歌词源（含网易云）一起并发发出去……网易云必须跟其它源同批起跑，**别为了封面/跳转链接单独同步先查一遍：那等于把网易云自己最坏能到小三十秒的串行耗时原样叠加在整体等待时间最前面**。」

`:6043` 还补了反面：不取消的话「残留请求会跟下一首的叠在一起抢出站配额」——**正是 DeskBar R3 的同一现象**。

---

## 五、Must-fix：四条代码级确证的问题

**性质声明：R1–R3 是逐行读码得到的调用链结论，逻辑成立，但本次未实机复现。KRC 缺陷已离线实测复现，但未在真实 KRC 数据上触发。**

### R1｜歌词失败无记忆 → 每 3 秒重跑一次完整回退链

**调用链**（全部逐字读原文）：
`MediaSessionService.cs:117-128`（`_pollTimer.Interval = 3 秒`）→ `:337` → `:432` → `:554 RefreshMediaPropertiesAsync()` → `:696` → `:701 _ = Task.Run(...)` → `:705 LyricService.GetLyricsAsync(...)` → `:706 _currentTrackLyrics = lyrics;`

**决定性一行**（`MediaSessionService.cs:696` 原文）：

```csharp
if (!string.IsNullOrWhiteSpace(title) && title != "未知曲目")
```

该条件**与歌词是否已缓存、上一轮是否失败完全无关**。
而 `LyricService.cs:213-217` 只在 `result != null && result.Lines.Count > 0` 时写 `_cache` → **失败不缓存**。

⇒ **成功路径**：第 2 轮起命中 `_cache`，仅一次字典查找。
⇒ **失败路径**：每 3 秒完整重跑 `FallbackChainAsync`（`LyricService.cs:222-238`，串行：本地 QRC → 本地 KRC → 本地 LRC → 网易云 → QQ；本地三路是同步目录扫描，在线两路各 6s）。
`:738 catch { }` 静默吞掉全部异常。

**影响**：播放一个歌词源查不到的曲子（冷门/纯音乐/网络不可达）每分钟约 20 次 × 完整回退链；离线或限流时雪崩。
**修复（约 20 行）**：失败也写缓存，TTL **5 分钟**，与既有 `LyricService.cs:746` `CoverMissTtlMs` 完全对齐。

### R2｜歌词赋值无曲目校验 → 串词

`MediaSessionService.cs:705-706` 拿到结果直接赋值，**无任何曲目一致性校验**。
同一段代码里，**封面路径有校验**——`:714`：

```csharp
if (netBitmap != null && CurrentInfo != null && CurrentInfo.Title == title)
```

`:745-749` 的 `trackId == _lastTrackId` 短路在 `Task.Run` **之后**，拦不住歌词任务；`:711` 封面还有「已有本地缩略图就不再补全」的短路，**歌词路径没有等价短路**。

**影响**：切歌时上一首请求未返回（最坏 12s）→ 显示上一首歌词，≤3 秒后自愈；若新歌请求也慢则窗口更长。
**修复（约 10 行）**：赋值前比对 title/artist 是否仍为当前曲目（照抄 `:714` 的既有写法）。

### R3｜无在途去重 → 重叠请求

`:701` 的 `Task.Run` 无去重，而 `LyricService.cs:166` 的单次超时 **6s > 3s 轮询** → 单次请求超过 3 秒时，同曲目会有 2+ 个在途请求，与 R1 叠加放大。
**修复（约 25 行）**：按 `cacheKey`（`LyricService.cs:189` = `{kind}|{title}|{artist}`）做在途合并，形状对应 `httpcoalesce.go:69`。

### R4｜KRC 负词始偏移 → 丢词 / 标记被当歌词显示

**缺陷位置**：`src/DeskBar/Services/KrcDecoder.cs:159`

```csharp
private static readonly Regex WordRegex = new(@"<(\d+)\s*,\s*(\d+)(?:\s*,[^>]*)?>([^<]*)", RegexOptions.Compiled);
```

首字段 `(\d+)` **缺 `-?`**，认不出酷狗 KRC 的负词始偏移。

**离线实测复现**（探针 `.scratch/realitycheck/krc_regex_probe.ps1`，纯 ASCII、只读、逐字复刻 `KrcDecoder.cs:153/159/165` 三条正则）：

| 组 | 输入 | 实测结果 |
| --- | --- | --- |
| A | `[60864,3000]<-11,116,0>alpha<0,873,0>beta` | `wordMatches = 1`，重建只剩 `beta` —— **首词整段丢失** |
| B | 全非负控制组 | 正常 |
| C | `[60864,3000]<-11,116,0>alpha(60980,873,0)beta` | `wordMatches = 0`，兜底文本**原样保留 `<-11,116,0>`** → 标记显示成歌词 |
| D | `<-1250` | 同样丢词 |

**致因**：A 组走出 `:234-236` 的 `words.Count > 0` 分支，直接 `text.ToString()`，跳过兜底；C 组走兜底，而 `WordRegex.Replace(body, "")` 擦不掉它**没匹配到**的标记。
**未在报告中主张的一点**：侦察员另指出 `:197-203` 用 `wordMatches[0]` 做「相对/绝对毫秒」自适应判断，**首词被吞后会拿第二个词判**，可能把整行时间轴算错。此路径本次未构造用例，列为**待验证**。

**这条缺陷是「活 bug」还是「防御性修复」？—— 两个独立第三方的佐证**

1. **上游 lyrimuse 有真实样本 + 单测固定**：`lyrimuse-engine/krcnegativeoffsets.go:13-18`（坑的描述）、`:19 krcNegativeOffsetTokenRe`、`:22 repairKRCNegativeOffsets`、`:29-39`（只认计时行行首、`max(start+off,0)` 夹非负）；样本与期望值在 `krcnegativeoffsets_test.go:10,23-24,32-37`（`<-11,116,0>` → `(60853,116,0)`；`<-30` 下溢钳到 0）；另见 `kugou.go:138`、`lyricswordformat_test.go:94`（`<-50,550,0>`）。
2. **本次由协调者独立取回的第三方证据（C# 同领域库）**：[WXRIW/Lyricify-Lyrics-Helper](https://github.com/WXRIW/Lyricify-Lyrics-Helper)（C#、**Apache-2.0**、237★、仓库仅 516KB）的 `Lyricify.Lyrics.Helper/Parsers/KrcParser.cs` 里的 `ParseLyricsLine` **没有用正则匹配词标记**，而是把行正文按 `",0>"` 切分后对首字段直接 `int.Parse`——**`int.Parse` 天然接受前导负号**，因此它对同一输入的结算结果是 `lineStart + start` = `60864 + (-11)` = **`60853`**，**与 lyrimuse 单测的期望值逐位一致**。

**这说明负偏移是 KRC 的合法格式形态**（两个互不相关的实现都按「可以是负数」处理），而 DeskBar 是三方里唯一处理不了的。

**一处诚实的不确定**：`int.Parse` 支持符号也可能是「顺带」而不是「刻意」，单凭这点不能证明 Lyricify 见过真实负偏移样本。**且本机 `%APPDATA%` / `%LOCALAPPDATA%` 下无酷狗目录、0 个 `.krc` 文件（已执行 `find`，maxdepth 5）** → **无法在本机证实真实数据中会出现**。

**三方在下溢处理上并不一致**：lyrimuse 夹到 0；DeskBar `KrcDecoder.cs:219` 已有 `if (absoluteStart < 0) absoluteStart = 0;`；而 Lyricify 的 `SyllableInfo.StartTime = lineStart + start` **没有钳位**。**DeskBar 应保留自己的钳位。**

**修复建议：最小改动是 `KrcDecoder.cs:159` 的首字段加 `-?`**（`(\d+)` → `(-?\d+)`）。现有代码已具备其余条件：
- `:216 long.Parse(wordMatch.Groups[1].Value)` —— **已能解析带符号值**；
- `:219 if (absoluteStart < 0) absoluteStart = 0;` —— **已实现下溢钳位**；
- `:198-203` 的 `firstWordStart < rawStart` 判据，在 `-11 < 60864` 时**会正确判为相对**。

**不要照搬 lyrimuse 的迁移水位机制**（`krcnegativeoffsets.go:55 migrateKRCNegativeOffsets` 带水位 + `enrichDirty`）：DeskBar 是零磁盘写，水位无处可存（写注册表也算写盘）。**在解析期修即可，不引入任何状态。**

**回归用例**（探针已在 `.scratch/realitycheck/krc_regex_probe.ps1`，可直接扩充为 `src/` 无关的独立断言）：

| 输入 | 期望 |
| --- | --- |
| `[60864,3000]<-11,116,0>alpha<0,873,0>beta` | 词数 2；`alpha` Start = **60853**；文本不含 `<` `>` |
| `[21713,1023]<-1719,0,0>踏上` | 词数 1；Start = **19994**（`21713-1719`） |
| `[10,500]<-30,30,0>早` | Start = **0**（下溢钳位） |
| 全非负输入（B 组） | 与修复前**完全一致**（无回归） |

---

## 六、比 lyrimuse 更贴合 DeskBar 的两个对象

这两个仓库是**从 TaskbarLyrics 的 README 致谢栏反查**得到的，不是先验知识。

| 仓库 | 定位 | 语言/许可 | star | 最近 push | 为什么更贴合 |
| --- | --- | --- | --- | --- | --- |
| [WXRIW/Lyricify-Lyrics-Helper](https://github.com/WXRIW/Lyricify-Lyrics-Helper) | 歌词解密/解析/搜索**库** | **C# / Apache-2.0** | 237 | 2026-10-21 | **同语言 + 宽松许可 + 领域完全重合**，仓库仅 516KB |
| [ANYNC/TaskbarLyrics](https://github.com/ANYNC/TaskbarLyrics) | Windows 任务栏歌词 | **C# / MIT** | 723 | 2026-11-05 | **同平台同语言同 UI 目标**，且把规则写成了文档 |

### 6.1 Lyricify-Lyrics-Helper：模块切分是最直接的收获

库内目录（GitHub contents API 实取）：`Decrypter/`（下分 `Krc/`、`Qrc/`）+ `Parsers/` + `Providers/` + `Searchers/` + `Generators/` + `Helpers/` + `Models/` + `Resources/`。
`Parsers/` 文件与字节数：`KrcParser.cs` 9084、`QrcParser.cs` 2778、`YrcParser.cs` 14326、`LrcParser.cs` 21645、`TtmlParser.cs` 41459、`TtmlParser.Pronunciation.cs` 30157、`AttributesHelper.cs` 5079。

**可借鉴点是结构层面的**：
1. **解密与解析分层**（`Decrypter/Krc/` 与 `Parsers/KrcParser.cs` 分离）。DeskBar 现在把两者揉在 `KrcDecoder.cs`（335 行）/ `QrcDecoder.cs`（339 行）里；分层后「格式常量」与「文本语义」各自可测。
2. **`Providers`（取词源）+ `Searchers`（搜索）+ `Parsers`（格式）+ `Decrypter`（加密格式）四段切开**，与 DeskBar 的 `MediaSourceRouter`（选源）+ `LyricService`（调度）+ `KrcDecoder`/`QrcDecoder`（格式）**几乎一一对应**，可作命名空间的对照蓝本。
3. `AttributesHelper.cs` 单独承载 LRC 标签解析，对应 `LyricService.cs:912 ParseLrc`。

**许可判定**：Apache-2.0 属宽松许可，**不受「不得移植 Go/Swift」那条约束的限制**。但 `AGENTS.md` §1.2 的**零 NuGet 红线**意味着**不能以包引用方式引入**——只能作设计对照，或经用户明确同意后内联并保留声明。
**未取回**：`README.md`、`docs/`、各 `.cs` 正文、`.csproj` 依赖列表（**是否零依赖未知**）→ 目前只有**结构级证据**。
**已取回**：`Parsers/KrcParser.cs` 全文（本文第五章 R4 用它做了负偏移的第三方佐证）。

### 6.2 TaskbarLyrics：它的评分文档与 DeskBar 的「候选唯一」正好互为对照

它把录用规则写成了 `docs/歌曲歌词匹配与录用评分标准.md`（6323 B，协调者已取回全文）。要点：

- **两层用途**：候选是否允许抓歌词（**门槛 80 分**）、多源完成后的最终来源选择。
- **硬性拒绝（直接 0 分，不进加权）**：标题为空/`Unknown Title`；**版本标记不一致**——逐个比较 `live`/`remix`/`acoustic`/`demo`/`instrumental`/`karaoke`/`simlish`/`from`/`vma`/`award`/`现场`/`演唱会`/`颁奖`/`典礼` 在两侧的出现情况，**一方有另一方没有即判冲突**。
- **基础加权**：艺术家+时长可用 `0.50T + 0.30A + 0.20D`；专辑可用时再混 5%（`0.95 × 基础 + 0.05 × L`）。
- **90 分规则**：默认来源顺序 QQ → 酷狗 → 网易云 → LRCLIB，各源**并行**；QQ 先完成且 ≥90 分即**立即录用并取消其它任务**；否则等全部完成，再在 ≥90 分结果中选，仍按来源顺序、再按分数降序。
- **分段变体检索**：`exact` → `normalized` → `primary-artist` → `relaxed-title` → `version-relaxed-title`；**每阶段只要发现任一 ≥80 分候选就停止该源后续变体**。
- 它把实现位置列成表（`LyricMatcher.cs` / `LyricIdentityEvaluator.cs` / `LyricMatchingPolicy.cs` / `LyricSearchStageExecutor.cs` / `LyricResolutionCoordinator.cs`）。

**与 DeskBar 的对照裁定**：

| 维度 | TaskbarLyrics | DeskBar | 判定 |
| --- | --- | --- | --- |
| 歧义处理 | 打分排序（80/90 门槛）**选最优** | `PickUniqueCandidate`（`LyricService.cs:837-878`）**分组 >1 就放弃**并记日志 `refused to guess an edition` | **方向相反，不移植**（与 §2.2 同一裁定） |
| 版本混淆防护 | **显式关键词黑名单**（14 个词，含中文「现场/演唱会/颁奖/典礼」） | 归一化后按**歌手分组数**判定 | **同向**，但手段不同：它更**精确**，DeskBar 更**保守** |
| 门槛可解释性 | 80/90 双门槛 + 完整公式，**写在文档里** | 布尔判定，规则散在代码注释 | **可借鉴的是「把判据写成文档」这件事本身** |

**最有价值的可借鉴点（低风险）**：把 TaskbarLyrics 的**版本关键词表**用作 DeskBar 的**诊断日志增强**——即当 `groups.Count > 1` 触发拒绝时，日志里额外写明「冲突发生在哪一类版本标记上」。这**不放宽唯一性**，只提升可观测性。

**不适用**：它把设置/歌词缓存/歌曲映射/单曲偏移**落盘到 `%APPDATA%\TaskbarLyrics`**（与 DeskBar 零磁盘写**直接冲突**）；设置页与歌词窗口用 **WebView2**（DeskBar 不会引）。
**它的自认短板也值得记**：README 自述网易云/酷狗**时间轴信息不完整、歌词无法滚动**，需第三方 `inflink-rs` 补全 → 其源侧时间轴能力**弱于** lyrimuse。这反过来说明：**格式解析能力与源侧时间轴能力是两件事**，借鉴时要分开评估。

---

## 七、落地方案

### P0（建议尽快做，共约 35 行，零新依赖、零磁盘写）

| # | 动作 | 位置 | 验收 |
| --- | --- | --- | --- |
| P0-1 | **失败也写缓存**，TTL 5 分钟，对齐既有 `CoverMissTtlMs` | `LyricService.cs:213-217`、`:746` | 无歌词曲目连播 60 秒，`GetLyricsAsync` 实际请求次数 = **1** |
| P0-2 | **赋值前校验曲目身份** | `MediaSessionService.cs:706`（照 `:714` 写法） | 快速连切 3 首风格差异明显的曲子，歌词文本与当前歌名一致（截图比对） |
| P0-3 | **KRC 词级正则首字段加 `-?`** | `KrcDecoder.cs:159` | 第五章 R4 的 4 组回归用例全过；全非负输入无回归 |

### P1（约 25 行）

| # | 动作 | 位置 | 验收 |
| --- | --- | --- | --- |
| P1-1 | 按 `cacheKey` 做**在途合并** | `MediaSessionService.cs:701` + `LyricService.cs:189` | 限速/断网下，同曲目在途请求 ≤1 |
| P1-2 | `groups.Count > 1` 拒绝时，日志补写**冲突的版本标记类别** | `LyricService.cs:855-860` | 构造《X (Live)》类样本，日志能指出是 `live` 冲突 |

### P2（先测量，再决定要不要做）

| # | 动作 | 前置测量 |
| --- | --- | --- |
| P2-1 | `FallbackChainAsync`（`LyricService.cs:222-238`）末尾两个在线源**并发** + 整体截止 | **必须先测 `kind == Unknown` 的实际占比**（`:210`）；占比极低则收益接近零，**不做**。截止值 **≤6 秒**，不照搬 20 秒 |
| P2-2 | 变音符号折叠（`string.Normalize(FormD)` + `ß/ø/æ/đ` 小表） | 需实测样本证明有收益；**绝不放宽 `PickUniqueCandidate`** |
| P2-3 | 统一诊断入口 `scripts/doctor.ps1`（本地项优先、网络项用真实路径验证而非 ping 域名） | 见第八章 C-3 |

### 验收（DeskBar 无单元测试框架，只能用探针 + 既有流程）

```powershell
# 1) 编译 —— 判定成败读 build_diag.log 末尾（两个 .ps1 都不传播退出码）
powershell -NoProfile -ExecutionPolicy Bypass -File D:/ai/deskbar/build.ps1
# 2) 发布
powershell -NoProfile -ExecutionPolicy Bypass -File D:/ai/deskbar/publish.ps1
# 3) 部署并启动（必须走 WMI，避免 Job Object 连带杀进程）
cd /d/ai/deskbar && bash deploy.sh
```

**回归判据（逐条对应上面的验收）**：
- `build_diag.log` 末尾为「已成功生成。」；
- 无歌词曲目连播 60 秒：请求数 **1**；
- 连切 3 首：歌词不串（`scripts/` 截图比对）；
- `deploy.sh` 打印的常驻工作集仍在 **15–30 MB**；
- **零磁盘写入不破**（负缓存与在途表都是内存字典）。

**排查前提**：日志默认静默（`AGENTS.md` §4.8），且 `deploy.sh` 会**清空** `D:\app\DeskBar\logs\*`。**排查崩溃请先取日志、再部署**；计数请求必须先在诊断页打开调试日志。

---

## 八、反面结论汇总（明确不做的）

| # | 不做什么 | 依据 |
| --- | --- | --- |
| C-1 | **不引入 lyrimuse 的打分/择优（14 个 `scoreTerm`）** | 与 `AGENTS.md:186` 直接冲突；`match.go:673-674` 是作者自承的相反取舍 |
| C-2 | **不引入时长打平 / 声部聚类**（`lyricdurationtie.go` 289 行 + 4 个阈值 + LCS） | 解的是「多源内容相同只差 1 秒」的冠军归属，DeskBar 不用分数挑冠军 |
| C-3 | **不引入按天统计 + peer group + 落盘**（`lyricsourcestats.go` 1,020 行） | **必须落盘**，违反零磁盘写；且只有 2 个在线源，样本撑不起统计 |
| C-4 | **不引入熔断/在途名额/令牌桶的完整实现**（`sourcebreaker.go` 935 行） | 单例 HttpClient、2 个源、无并发编排 → 表空转。**只吸收「连 2 次才开闸」「取消不计熔断」两条判据** |
| C-5 | **不引入 DoH / 代理回退**（`lyricsourcedial.go` 212 行） | DeskBar 无此需求 |
| C-6 | **不引入目录监听**（`dirwatch.go:29-42` 是 macOS 专属 kqueue） | Windows 对应 `ReadDirectoryChangesW`，但会引入常驻句柄/轮询，侵蚀 15–30 MB 与 0.0–0.1% CPU 预算。DeskBar 现状是有限次扫描（`LyricService.cs:460-472`，为避免拖慢切歌） |
| C-7 | **不引入一次性格式迁移的水位机制**（`krcnegativeoffsets.go:55`、`lyriclineendings.go:40`） | 零磁盘写，水位无处存 |
| C-8 | **不引入 `players.json` 的代码生成 + CI `--check`** | 它解 Go/Swift 双语言同步；DeskBar 单语言单仓库无此问题。**只借「怪癖布尔字段」的清单形状** |
| C-9 | **不照搬 NDJSON 流式 stdout 契约**（`searchcli.go:14-36`） | 解的是 Go 引擎 ↔ Swift UI 跨进程推送；DeskBar 歌词链路是进程内 `LyricService`，无此边界 |
| C-10 | **不照搬全机重活闸**（`scripts/heavy-gate.sh`） | 为「多会话共用一台机器」设计；DeskBar 单机单会话下收益递减。**只在真的出现两个会话同时跑 `build.ps1` 时再引入** |
| C-11 | **不引入 lyrimuse 的命令行框架**（它**自己没有** `-h`/`--help` 与子命令总览，16 个子命令只能读源码发现） | 这是它的 DX 短板，**不要照搬** |
| C-12 | **不要把 lyrimuse 的 `gctune.go` GC 调参当作可直接迁移的经验** | Go 专属。**但其「明确反对固定内存上限」的推理对 DeskBar 有参考价值**（DeskBar 走的是 `EmptyWorkingSet` + `GC.Collect` 周期维护，是另一条路） |
| C-13 | **不要把「它有 627 个测试文件」当作质量证据** | 3803 个测试中 181 个引用真实网络 → 离线不可能全绿；本次**未运行任何测试** |

---

## 九、未覆盖范围与数据缺口（诚实声明）

**本次未做的事**：未 clone 任何仓库；未运行 lyrimuse 的任何代码或测试（本机无 Go/Swift 工具链）；未修改 DeskBar 任何源码或 `AGENTS.md`；未执行 `build` / `publish` / `deploy`；**R1–R3 未实机复现**。

**明确未读的材料**：
- lyrimuse `match.go` 全文（241,917 B；仅读入口、打分函数签名与常量）→ **本次最大覆盖缺口**；
- `songentityshare.go`(1,336 行)、`amllttml.go`、`lyricsourcestats.go`、`lyricswordformat.go`、`albumhint.go`、`durationvariant.go`、`t2s.go`、`doh.go`、`provisionallyrics.go` 等**仅读头注或函数注释**；
- **lyrimuse Swift 侧 793 个文件完全未读**（仅统计目录规模），`LyricsSyncEngine.swift`（248,634 B）只读前 55 行；
- DeskBar `MusicCapsuleWindow.xaml.cs`（1,196 行）未逐个读，仅采用 `AGENTS.md` §4.6 的既有结论；`LyricService.cs` 的网易云解析体（`:240-374`）与本地解码细节（`:418-716`）未逐行读；
- Lyricify 的 `.cs` 正文（除 `KrcParser.cs`）、`docs/`、`.csproj` 依赖列表 → **是否零依赖未知**；
- TaskbarLyrics 的 `TaskbarLyrics.Core/` 内部结构、测试工程内容、`docs/*.md`（除评分标准）。

**证据不足、不得当作结论使用的**：
- 「01–18 章决策 NNN」共 267 个编号在 lyrimuse 源码注释中被引用，但**仓库内无实体文档**（无 `AGENTS.md`/`CLAUDE.md`；`decision.go`/`decisionstore.go` 是**运行时**决策存储，不是设计文档）→ **阈值可从代码核实，阈值的推导依据不可核实**。凡引用其阈值的条目，依据均属不可核实。
- `pushed_at 2026-11-09` 与默认分支 HEAD `2026-11-01` 的差异**未归因**。
- DeskBar `kind == Unknown` 的实际占比**未测量**（直接决定 P2-1 是否值得做）。
- DeskBar 机器上是否真的存在带负偏移的 KRC 样本**未验证**（本机 0 个 `.krc`）。
- `ReadOffset`（`KrcDecoder.cs:177`）处理 `[offset:N]` 时是否也需支持负值（`[offset:-500]` 在 LRC 中常见）——**未核实**，不在本次修复建议范围内。

**检索留痕**：GitHub API 查询 9 次（`search/repositories` 2 次：`total_count` 710 / 81；`/repos/*` 元数据与 `contents` 7 次），全部未认证且成功，未遇 403。注意 `/search/code` 需认证，未认证下不可用 → 代码级检索的替代路径是 `raw.githubusercontent.com` 直取。

---

## 附：本次调研的原始材料

**团队分工（GitHub 开源先遣专家团，5 名成员并行）**：

| 角色 | 分工 | 主要产出 |
| --- | --- | --- |
| GitHub 开源项目侦察员 | 检索、候选仓库 API 元数据、lyrimuse/TaskbarLyrics/Lyricify 结构解剖 | 第十一章的候选项目表与 lyrimuse 可借鉴清单 |
| Software Architect | 架构对照、规模量化、失败处理维度差异 | 第三、五章（R1–R3 的调用链与行号） |
| Developer Tooling Engineer | 工程化门（契约测试 / doctor / 退出码传播） | 见「可另立的工程化议题」 |
| Research Synthesist | 证据核验、冲突裁定、快照锚定 | 第一章证据基线、§2.2 决定性冲突 |
| Reality Checker | 环境实测、许可链路核查、KRC 缺陷离线复现 | §1.3、§1.4、§1.5、第五章 R4 |

**协调者（lead）亲自执行的核验**：
- `git`/`curl` 被沙箱拦截后的 tarball 获取路径，以及用 GitHub commits API 独立确认真实 HEAD sha；
- 三条静态断言复核：`.github` 不存在、`build.ps1`/`publish.ps1` 无 `exit`、`deploy.sh` 无 `set -`；
- 逐条自查报告骨架上的承重引用（`KrcDecoder.cs:153/159/165`、`LyricService.cs:213-217/745-747/837-878/889-894`、`MediaSessionService.cs:696/701/705-706/714`、`match.go:660-691`、`AGENTS.md:186`）；
- 独立取回 [Lyricify `KrcParser.cs`](https://raw.githubusercontent.com/WXRIW/Lyricify-Lyrics-Helper/master/Lyricify.Lyrics.Helper/Parsers/KrcParser.cs) 全文（R4 的第三方佐证）与 [TaskbarLyrics 评分标准](https://github.com/ANYNC/TaskbarLyrics/blob/main/docs/歌曲歌词匹配与录用评分标准.md) 全文（§6.2）。

**本机遗留的探针（只读、纯 ASCII、未改 `src/`）**：
- `.scratch/realitycheck/krc_regex_probe.ps1` —— 复刻 `KrcDecoder.cs:153/159/165` 三条正则，输入取自 `lyrimuse-engine/krcnegativeoffsets.go:13-15` 的真实坏串，可直接作为 P0-3 的回归用例起点。
- `.scratch/refs/lyrimuse/` —— 快照（无 `.git`，**不要**当作可 `git` 操作的目录）。

**可另立的工程化议题（本次未落地，与歌词主题无交集）**：DeskBar 有 87 个根目录 `.ps1` + `scripts/` 227 个 `.py`，但**没有一条会自动运行、会在违规时变红的门**——`AGENTS.md` 里的「必须三处同改」「禁止 `Start-Process`」等铁律目前只靠人记。可借鉴的判据有三条：① 源码扫描式契约测试（`lyrimuse/Sources/lyrimuse-selftest/SourceContractTests.swift:4052-4059` 读 `main.go` 文本断言 `log.Fatal` 出现 0 次）；② doctor 命令（`lyrimuse-engine/healthcheckcli.go:15-67,223-253`，**先跑本地不联网的检查、网络项用真实业务路径探测而非 ping 域名**）；③ 退出码是 API（`exitreason.go:21-51`，全仓一致 0=成功 / 1=功能失败 / 2=用法错）。**注意其自曝的坑**：`OpsDiagnosticsTests.swift:1057` 记录「整份 contains 会被自己的注释打红」——不过滤注释行的契约门会一上线就恒红，**比没有门更糟**。

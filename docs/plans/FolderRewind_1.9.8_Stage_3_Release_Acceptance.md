# FolderRewind 1.9.8 阶段三：联动、性能、升级与正式发布验收

状态：实施规格已确定；本文所有功能、性能、安装及发布验收项均为 **待执行**。

依赖：[阶段一：历史恢复](FolderRewind_1.9.8_Stage_1_History_Recovery.md)、[阶段二：还原简化](FolderRewind_1.9.8_Stage_2_Restore_Simplification.md)。初始 Host 基线为 `53975eb4d18d3b936bf13a15577ebcd755d94546`，正式版本目标为 `1.9.8.0`。版本冻结发生在修复实施后，不能把规划基线当作最终发行提交。

## 1. 发布门槛与版本来源

| Gate | 必须取得的结果 | 当前状态 |
|---|---|---|
| G1 历史与恢复 | 阶段一退出条件、R1-01～R1-12、真实强杀及 UI 结果齐全 | 待执行 |
| G2 兼容与简化 | 阶段二退出条件、R2-01～R2-15、保留的执行保护齐全 | 待执行 |
| G3 最终组合 | 本文联动、旧数据、正式/实验两条路径及公开契约兼容验证 | 待执行 |
| G4 性能 | 两个基线、五次样本、结构性开销门槛与显著回归判定 | 待执行 |
| G5 渠道候选 | 同源 Setup/MSIX、x64/ARM64 的适用构建、安装及真实运行证据 | 待执行 |
| G6 正式发布 | 依赖顺序完成；晋级同一批已验收产物；线上版本与摘要核对 | 待执行 |

所有关键用例应有“原反馈复现”或“等价故障注入”的证据。缺少某位用户原始事务文件时记录证据边界，不声称已经复原其全部事故；也不以个人回复作为自动等待任务。未复现的问题保留待确认项，不能仅因测试总数增加就关闭。

### 1.1 初始候选来源，不是最终冻结值

现有 `docs/plans/Native_Artifact_Delivery_1.9.json` 记录内置 MineRewind 候选 `1.9.9`，来源提交 `a50104b921c51469e3b8362b865f0370c73d8840`，契约候选 `3.10.0-native.d4c4f9579b00f388`，API `3.10`，程序集身份 `3.0.0.0`。

旁边 `FolderRewind-Plugin-Minecraft` 工作目录当前检出的开发分支不等于该产物来源。跨仓库修改前各自检查 Git 状态及 AGENTS；从准确来源继续，不直接以相邻工作区重建并覆盖冻结包。

2026-10-19 的只读检查显示：NuGet 索引列出 `3.0.0`、`3.6.0`；MineRewind 最新公开 Release 为 `v1.9.5`，MineBackup-Mod 为 `v3.4.0`。这些只说明当时的公开状态，不能当作未来测试中的固定断言。正式冻结前重新读取：[NuGet 索引](https://api.nuget.org/v3-flatcontainer/folderrewind.plugin.abstractions/index.json)、[MineRewind Releases](https://github.com/Leafuke/FolderRewind-Plugin-Minecraft/releases)、[MineBackup Releases](https://github.com/Leafuke/MineBackup-Mod/releases)。

### 1.2 冻结记录

每组候选记录 Host/插件/相关独立仓库的源码提交、工作区干净状态、应用版本、SDK 包版本及摘要、API/程序集身份、插件 manifest/包摘要、MineBackup 的实际版本及 loader/MC 组合、7-Zip/rclone 版本及摘要、架构/渠道、构建配置、测试工具和产物路径。

- API 候选公开为对应正式包后，使用空包缓存从公开源独立还原插件；验证契约内容与 Host 一致，而非仅核对版本字符串。
- 不覆盖已公开 NuGet 版本，不把实验包名伪装成已发布正式依赖；发现同版本内容冲突时回到版本冻结步骤。
- 插件只能引用 Abstractions 包，不依赖 Host 项目或源码，包内不捆绑 Host Abstractions DLL。
- Native 实验功能默认关闭，保留已经合入的读写兼容能力；不通过打开实验功能替代 Core 回归。
- 7-Zip 等工具在本轮对比和冻结后不能再次随“latest”漂移；不顺手升级 .NET、Windows App SDK 或其他依赖。

## 2. 联动实施与最终结果语义

主要落点：`FolderRewind/Services/NativeHistoryRestoreOrchestrator.cs`、`NativeHistoryApplicationService.RestoreTarget.cs`、`KnotLinkService.Commands.cs`、现有 Operation/通知入口，以及准确来源中的 MineRewind `MinecraftSavesPlugin.cs` / `MinecraftSavesPlugin.KnotLink.cs`。

当前 Host 已区分提交、回滚、需要恢复和提交后警告；插件已有重进失败/超时及恢复待处理保护。先对照实际发布包和模组验证这些路径，再修改缺陷，不另造一套通信协议、任务队列或版本检测平台。

| 文件结果 | 对用户的含义 | 后续联动 |
|---|---|---|
| 请求已接收 | 任务准备启动 | 不是备份/还原成功；等待最终生命周期结果 |
| 修改前拒绝或取消 | 目标未改动 | 若尚未退出则不要求退出；已退出且原世界稳定时可恢复原世界会话 |
| NoChanges | 已证明无需修改 | 不创建快照、不重做文件；仍独立报告重进结果，TargetCommitted 不得伪造为 true |
| 已提交 | 文件已还原 | 可以请求重新进入；重进失败只报告后续警告，不再执行文件还原 |
| 修改失败且已回滚 | 原文件已恢复 | 报告还原失败及已恢复原状；原世界稳定时可重新进入 |
| 文件结果需要恢复 | 无法保证目标已处于可使用状态 | 不自动重进；给出恢复入口和明确的最终失败/待恢复通知 |
| 文件结果已确定、仅清理待处理 | 还原或回滚结果有效 | 保留结果，报告维护警告；清理不阻断正常后续历史推进 |

实施步骤：

1. 在请求保存退出前完成能够低成本确定的选择、路径/卷、载荷、凭据及必需协调条件检查。显示真正的缺项，不重复全量能力扫描。
2. 保持现有异步接受语义；让游戏侧和 Host 的最终成功/失败来自实际生命周期事件，不能依据初始 `OK:... started` 播放完成反馈。
3. 保留 Host 文件结果和插件后续结果两个维度，包含 NoChanges + 重进失败的组合。通知、声音、日志及命令终态必须一致。
4. 超时、取消、延迟到达的确认、插件停用和重复请求不能把别的操作标为成功。复用现有请求/会话归属；不能单靠增加固定等待时间掩盖状态竞争。
5. 恢复待处理也要有用户可理解的终态及下一步，不能使游戏侧永久停留在“正在还原”；兼容已有失败反馈，禁止在未确认文件稳定时发出重进请求。
6. 联动检查以实际握手和已知所需能力为准；不添加没有用途的模组扫描。既有支持版本的兼容声明由真实测试支撑，缺少/不兼容的组件在退出世界前说明。

## 3. 功能、升级与 UI 验收

### 3.1 重点用例

| 用例 | 输入与操作 | 必须观察到的结果 | 状态 |
|---|---|---|---|
| R3-01 | 最终 Host/插件/模组组合：普通、Quick、Checkout；Core Full/Smart，实验开关关闭 | 最终文件内容、模式、快照及事件一致；不把接受当完成 | 待执行 |
| R3-02 | NoChanges、已提交、已回滚分别组合重进成功/失败/超时 | 文件结果保留；NoChanges 不假装发生提交；无重复文件执行 | 待执行 |
| R3-03 | 需要恢复、修改前取消、保存退出失败、文件未释放 | 结果准确；不进入不稳定世界；有可执行的恢复/重试入口 | 待执行 |
| R3-04 | 旧/缺失模组、必需插件停用、插件升级/卸载竞争、迟到确认、重复请求 | 合理拒绝或维持当前操作租约；结果不串到其他操作 | 待执行 |
| R3-05 | 1.8.2、1.9.6、1.9.7 的隔离配置/归档副本升级；Full/Smart、加密、Packs 与本机状态组合 | 保留旧数据，增量依赖与凭据正确；不把 Runtime 故障误写为迁移成功/归档损坏 | 待执行 |
| R3-06 | 升级时带有效/失败的 v1/v2 事务；阶段一修复及下一次完整备份 | 原世界及快照保全；重复启动可恢复；不错误续接原分支 | 待执行 |
| R3-07 | Cloud 上传/下载、迁移、Retention、Merge、删除、导出及后续捕获 | 延用既有提交和保护根约束；不会回收仍需的回滚、快照或闭包 | 待执行 |
| R3-08 | Native 默认关闭及主动开启后的关键路径；公开旧插件与最终新插件分别加载 | 正式 Core 无回归；实验状态不被默认打开；旧契约兼容与准确包来源可验证 | 待执行 |
| R3-09 | 中英文、普通/高级历史、加载/空/局部失败/恢复提示、修复取消/确认及重启 | 页面保持可用，文案与动作一致，错误不扩大到整页；无缺失资源键 | 待执行 |
| R3-10 | Setup 与 MSIX，x64/ARM64，干净安装、原位升级、首次启动、基本备份还原和退出 | 真实渠道、架构及内置插件一致；安装和运行证据准确，不能以构建替代安装 | 待执行 |

真实账号 Cloud、多设备和 USB/ARM64 设备验收单独记录，rclone local 不替代真实后端。缺少设备/账号时记录未验证，不能写 Passed；适用关键项未关闭前不将整版标为发布就绪。

### 3.2 数据与 UI 隔离

默认使用合成夹具。真实样本仅用本任务明确提供或另行授权的副本；旧文档中的授权描述不能继承为操作用户现有配置/世界的许可。世界、配置、凭据、原始现场和大载荷留在本地忽略的 artifacts/Reference，不进 Git。

UI 自动验收默认使用 WinApp CLI 0.7+ 的批量脚本，结果同时包含 JSON 和关键状态截图；自动断言后检查截图的布局、焦点、主题和中英文文案。沿用 MVVM、现有 InfoBar/ContentDialog 和 AutomationId。

- 能使用 Sandbox 时优先在其中执行；否则使用隔离的本地 MSI 测试进程。`FOLDERREWIND_TEST_DATA_ROOT` 仅在现有 MSI 分发路径有效，不能据此认为 Store 进程已隔离。
- 启动后先确认实际 ConfigService 根属于测试目录，并确认目标路径均为本轮夹具，再进行有写入的 UI 操作。不能复用用户唯一的生产世界或猜测 PID/窗口。
- MSIX/Store 安装测试使用干净测试账户、虚拟机或适用的隔离环境；不把生产 LocalState 当测试目录。
- 脚本固定目标作用域和本次 PID，逐项检查 winapp 退出码。重启后重新发现 PID/窗口，不复用上次句柄。
- 页面/验证失败、修复前后、还原确认、提交后警告及重启持久化分别截图，不只留初始窗口截图。
- 若用户在实施阶段明确要求人工 UI 验收，则交付步骤并保留实际执行结果；未执行仍是待验证。

### 3.3 构建和测试命令

从仓库根目录串行执行，不并行争用项目 bin/obj。使用新的证据目录，不把旧 TRX 混入本次报告。

```powershell
$ErrorActionPreference = 'Stop'
$fr198Evidence = Join-Path $PWD ('artifacts/1.9.8/validation-' + [guid]::NewGuid().ToString('N'))

dotnet restore .\FolderRewind.slnx -p:Platform=x64
if ($LASTEXITCODE) { throw 'Restore failed.' }
& .\.github\scripts\Initialize-CoreTestEnvironment.ps1

dotnet build .\FolderRewind.slnx -c Debug -p:Platform=x64 `
  -p:GenerateAppxPackageOnBuild=false --no-restore
if ($LASTEXITCODE) { throw 'WinUI build failed.' }

dotnet test .\FolderRewind.Plugin.Abstractions.Tests\FolderRewind.Plugin.Abstractions.Tests.csproj `
  -c Release --no-restore --logger trx --results-directory "$fr198Evidence/abstractions"
if ($LASTEXITCODE) { throw 'Abstractions tests failed.' }
dotnet test .\FolderRewind.Plugin.Runtime.Tests\FolderRewind.Plugin.Runtime.Tests.csproj `
  -c Release --no-restore --logger trx --results-directory "$fr198Evidence/runtime"
if ($LASTEXITCODE) { throw 'Runtime tests failed.' }
dotnet test .\FolderRewind.Tests\FolderRewind.Tests.csproj `
  -c Release --no-restore --filter 'TestCategory!=PerformanceBenchmark' `
  --logger trx --results-directory "$fr198Evidence/host"
if ($LASTEXITCODE) { throw 'Host tests failed.' }

& .\.github\scripts\Get-SetupTestResults.ps1 -Directory $fr198Evidence
```

检查脚本拒绝失败、未执行和缺失证据；任何 Inconclusive/Skipped 的关键集成项不得被测试总数掩盖。插件独立工程与现有 World 测试在准确来源上另行运行，采用同一已冻结 SDK 包；不得让插件直接编译 Host 源码以凑通过。

WinApp 先通过 `winapp --help`、`winapp ui --help` 及子命令帮助确认本机语法。编写一个批量脚本，用真实 AutomationId 验证 R3-09 及相关恢复/还原流程，并输出非零失败状态；脚本不静默换环境或跳过失败步骤。

## 4. 性能比较协议

### 4.1 对照与样本

三个 cohort：已发布 `v1.9.7`、规划起点 `53975eb4d18d3b936bf13a15577ebcd755d94546`、最终候选。各自独立源码及输出目录；旧基线只加隔离/测量入口，不移植候选的恢复、验证或数据路径。

现有 `BackupRestorePerformanceBenchmarkTests`、`RealWorldPerformanceBenchmarkTests` 和 `tools/BackupPerformanceHost/Program.cs` 固定三次重复。实施时先给这些入口及汇总脚本增加五次采样支持，沿用同一测量协议。采用测试/工具环境参数 `FOLDERREWIND_BENCHMARK_REPEATS=5`，缺省值也为五；参数是待实施能力，不是基线当前已支持的开关。旧版本测量适配仅修改重复次数与测量入口。

固定同一机器、输入、卷布局、工具、压缩/验证设置和恢复点开关；记录电源方案及后台负载。数据构造和夹具正确性比较不混入操作耗时，但产品本身的验证、快照、捕获基线同步和清理开销全部计入相应阶段。游戏保存退出、文件操作和重进耗时分别报告，不将无窗口 Host 工具当成 UI 验收。

每个有效场景五个样本全部保留，报告中位数、最小/最大、首轮、调用计数及开销。缓存未控制就标记 uncontrolled，不宣称冷缓存或 P95。旧版本没有的能力记 N/A，不伪造等价结果。

### 4.2 必测场景与结构性门槛

| 场景 | 测量内容/硬门槛 |
|---|---|
| 打开普通/高级历史、切换分支 | 同一历史配小/大世界；无待恢复事务时零世界内容读取、零物化、零 restore journal |
| 普通 Full/Smart 捕获 | 包含必要扫描、归档和所选验证等级；同配置、同稳定输入的内容结果一致 |
| 小事务及 2 GiB 合成内容 Clean/Overwrite | 原文件、过滤范围和目录结果正确；DirectorySwap/FileDelta 及日志成本单列 |
| 真实样本副本及默认 Minecraft 过滤 | 边界外大目录/文件不整树复制或扫描；玩家/路径保留不误标 Exact |
| 多层 Smart 物化 | 整次逻辑物化一次，必要的多个归档提取如实计数；不能把“物化一次”误写成全部链只允许一次 7z x |
| Quick 有变化/无变化 × 普通/Quick 快照开关 | 单次准备、每个 Source 单次补丁；无变化不建快照；验证等级和调用次数符合选择 |
| 清理失败及后续操作 | 清理不重复进行发布，不依赖旧 revision，不使正常浏览或后续备份锁死 |
| 根重定向、USB NTFS | 报告额外路径绑定成本和实际设备信息，不把同一物理 SSD 的两个盘符称为跨设备结果 |

使用 `HistoryOperationDiagnostics` 和已有计时/计数入口记录 7z a/l/t/x、存储/世界读取、写入/复制、移动、日志字节/刷新、物化/补丁和清理。缺少必要计数时扩展既有诊断，不另建持久遥测服务。Strict 对每个所需输入的完整测试最多一次，Balanced 不因安全快照名字强制增加完整测试；必要的摘要、加密和 Native 内容校验不能删掉。

临时空间同时报告逻辑载荷和实际测量口径；生命周期进程峰值不能冒称纯操作峰值，测试夹具内存不能冒称真实应用峰值。

### 4.3 回归判定

对两个基线分别计算 `deltaMs = candidateMedian - baselineMedian`。有效 baselineMedian 必须大于零。**deltaMs > 20 且 deltaMs / baselineMedian > 0.10** 判为本轮显著回归；门槛相等不算“超过”。

第一次命中后，排除明显环境异常，再对相关基线和候选按相同协议各跑一轮五次；两轮均命中则 G4 不通过。不能挑最快一轮、删首轮或调整验证等级消除回归。结构性硬门槛即使耗时更快也必须满足。

优先用既有服务基准和 `tools/BackupPerformanceHost`，参照 `tools/performance/BackupRestore-README.md`。只在五次能力已补齐后运行本版本报告；原有三次结果作为历史证据保留，不冒充五次。至少保留场景、cohort 提交、配置/工具摘要、五个样本、正确性结果及原始诊断/汇总路径。

## 5. 候选构建与正式晋级

### S3-01：补足安全候选入口

基线 `.github/workflows/release-setup.yml` 的 `publish` job 会直接以 `-Finalize` 调用发布脚本。旧 `Reference/RELEASE-WORKFLOW.md` 中“只构建/默认草稿”的描述不能用于解释这个工作流。

在既有工作流增加 `candidate_only` 布尔输入，默认 true：仍执行原有 tests/build 并保留候选、附件和报告，跳过 publish。该输入为本阶段待实施内容，添加之前不能用 workflow dispatch 生成待验收候选。工具版本必须取本轮冻结值，而不是在测试后重新选择 latest。

本轮正式晋级使用现有 `Publish-ReleaseAssets.ps1` 对下载并验收的同一批候选附件执行，不重新触发 build 来“发布已经测过的版本”。不另造发布平台；补充现有报告/校验脚本对三阶段验收证据及其源码、架构、渠道、包摘要的关联校验，保持原有附件完整性和版本检查。

新增开关、候选不发布、冻结后晋级和摘要不匹配拒绝，加入现有 `Test-SetupReleaseFlow.ps1` / `Test-SetupReleaseAssets.ps1` 等回归。

### S3-02：先对齐公开依赖

顺序为：**API NuGet → MineRewind → Host 内置包/官方目录 → FolderRewind**。

- 从修复后的冻结源码生成 API 候选，执行既有 Plugin v3 Hardening 的独立插件兼容验证。
- 按实际公开状态发布对应正式 SDK；等公开索引可读取，以全新缓存验证，不覆盖现有版本。
- 从准确插件来源、对应公开 SDK 独立构建最终 `.frplugin`，检查 manifest、权限、能力、架构和实际 SHA-256。
- 同一份最终文件用于插件发行附件、Host 内置包和官方目录；不在通过验收后重建一个“同版本”文件替换。
- 旧公开契约及旧正式插件继续纳入兼容验证；程序集版本相同不是二进制兼容已证明。

### S3-03：生成并冻结渠道候选

只有版本冻结步骤完成后，把应用 csproj 与 manifest 对齐 `1.9.8.0`，准备中英文 Release notes 和恢复指引。通过既有 `Build-SetupCandidate.ps1` 分别生成 x64/ARM64 Setup 候选；公开附件沿用两个 Setup EXE 及各自 SHA-256，MSI 为内部打包材料。

候选构建入口示例（变量须来自本轮真实冻结记录，不填写假的 CI run）：

```powershell
# 前置：源码版本已为 1.9.8.0；以下变量均来自候选运行/冻结记录。
# $fr198Version、$fr198SevenZipRelease、$fr198RunId、$fr198CandidateRoot
& .\.github\scripts\Build-SetupCandidate.ps1 -Platform x64 `
  -Version $fr198Version -SevenZipRelease $fr198SevenZipRelease `
  -RunId $fr198RunId -ArtifactsDirectory $fr198CandidateRoot
& .\.github\scripts\Build-SetupCandidate.ps1 -Platform ARM64 `
  -Version $fr198Version -SevenZipRelease $fr198SevenZipRelease `
  -RunId $fr198RunId -ArtifactsDirectory $fr198CandidateRoot
```

本地试构建与正式 CI 证据区分记录，不伪造 CI URL/运行号；执行原生命令时检查退出码。构建按现有项目发布配置进行，不为本轮引入 AOT/工具链迁移。

Microsoft Store 使用现有 Packaged/MSIX 打包路径，保留商店身份与渠道设置；不能把 Msi 的 `WindowsPackageType=None` 用于商店打包。MSIX 和 Setup 分别记录最终文件、安装结果和运行数据位置，包含 LocalState 重定向验收。

冻结后修复导致任何包变化，都形成新候选，更新摘要并重跑受影响验收；旧候选证据不能转写成新包通过。

### S3-04：最终晋级与线上核验

阶段验收报告绑定同一源码、实际 SDK/插件、工具及安装包；保留原始 TRX、JSON、截图、性能原始样本、失败/未执行记录和冻结清单。当前 `New-SetupReleaseReport.ps1` 生成的 `installerExperience='not-run'` 不能当成 R3-10 已通过；需补充真实渠道结果和其他 Gate 证据，再进行最终校验。

通过所有适用 Gate 后，先核对草稿/附件集合，再由既有发布脚本晋级冻结附件；不覆盖已发包或用版本文本替代文件摘要。Store 递交、审核通过和用户可更新是独立状态，GitHub 已发布不能代替 Store 已可用。

发布后核对 NuGet 空缓存还原、插件与 Host 标签对应提交、附件/内置包/目录摘要、Setup 更新选择的架构，以及 Store 的实际可用版本。记录发布链接与实测结果。新增严重问题停止后续晋级并保留候选/现场，按新的修复候选推进，不通过降级源码或删事务回滚用户状态。

## 6. 交接与最终完成定义

- [ ] G1～G5 的适用关键项全部有实际证据，待验证/失败不伪装为通过。
- [ ] FR198-01～FR198-10 均有根因或证据边界、实现提交和对应验收结果。
- [ ] R3-01～R3-10、五次性能协议和两基线判定完成。
- [ ] Setup/MSIX 与 x64/ARM64 的规定验证完成；默认实验开关、语言、内置插件正确。
- [ ] 公开契约与最终插件可独立还原；没有 Host 编译依赖或捆绑契约 DLL。
- [ ] 候选入口不会自动正式发布，晋级使用同一组已验收字节。
- [ ] G6 的线上版本、摘要、更新和渠道结果记录完整。

最终报告说明具体改动、修复原因、实际运行的验证、失败/未验证项及发布状态。技术通过、可发布、已递交 Store、已正式可用分别记录，不合并成一个“完成”。

第一、二阶段的代码与服务验证已有独立记录，第二阶段见[实施与验证报告](FolderRewind_1.9.8_Stage_2_Validation_Report.md)。其中 `3bfc761` 仅用于阶段二工程性能对照，不能替代本阶段的 `v1.9.7` 及原规划起点正式基线。阶段二无窗口 Host 的测量二进制也不是渠道候选。

前两阶段人工 UI 本轮跳过，不计为通过。真实游戏联动、USB 等硬件、UI、中英文、Setup/Store 及 x64/ARM64 的正式验收仍须按本文件执行。未执行 G1～G6 的发布步骤，没有生成 Setup 候选、推送或发布。最终发行组合、候选字节及线上记录仍 **待填写**。

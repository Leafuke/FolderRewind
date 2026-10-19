# FolderRewind 1.9.8 全阶段自动验收交接（x64 MSIX）

执行对象：用户之后分派的 agent。**编写本文的 agent 未安装、启动产品应用或执行 UI 自动化。本文所有安装/UI 项初始为待执行。** 既有服务证据见[阶段三记录](FolderRewind_1.9.8_Stage_3_Release_Acceptance.md)，不得替代 MSIX 结果。

## 1. 输入与执行边界

- 使用本轮冻结 **1.9.8.0 x64 MSIX**、公钥 CER、必需依赖及清单；逐个核对 SHA-256。交接输入未填或摘要不符先停止，不现场重建替代。
- 不新增测试用例代码、工程、探针或 UI 脚本；复用既有测试/探针及本文操作。可以直接发出分组 WinApp CLI 命令并保存记录，不编译开发验收版本补覆盖。
- KnotLink/真实游戏互联、Setup/ARM64、SDK/插件发布、Store/Release 和线上更新延期；不连接生产云账户。
- 默认隔离 Windows Sandbox，先只读 `winapp target snapshot sandbox --json`。不可用则 BLOCKED，等待用户指定独立 VM/测试账户，不退回当前生产用户。MSIX 不使用 MSI 的 `FOLDERREWIND_TEST_DATA_ROOT` 隔离。
- guest 为共享持久环境；确认任务归属，干净安装与各旧版升级使用独立干净状态。取回证据后才重置本任务环境，不关闭其他任务的 guest。
- 所有配置、Source、归档、损坏/占用/链接/空间夹具均为本轮合成材料。讨论文件的历史授权不等于当前真实用户数据授权。

## 2. 安装、激活、定位与取证

### 2.1 前置与传输

宿主保存 CLI 版本/帮助及 snapshot；要求 CLI 0.7+，guest 已连接、未最小化、可输入且可截图，能读树不等于能输入：

```powershell
winapp --version
winapp target --help
winapp target snapshot sandbox --json
winapp target push --help
winapp target exec --help
winapp ui --help
winapp ui inspect --help
winapp ui wait-for --help
winapp ui screenshot --help
```

用 `winapp target push sandbox <宿主冻结目录> Candidate` 传输。`Candidate` 相对于 snapshot 的 guest `workRoot`；通过 `target exec` 在 guest 重算摘要。宿主 SDK 不等于 guest 运行时，按清单准备依赖。

### 2.2 guest 内安装与已安装包激活

下列 PowerShell 操作通过 `winapp target exec sandbox -- powershell.exe -NoProfile -NonInteractive -Command ...` 在 guest 执行，变量来自实际 workRoot/清单：

1. `Get-AppxPackage -Name Leafuke.FolderRewind`：干净安装应为空；存在不属于本任务的包则停止，不自行卸载。
2. 校验 CER 的 Subject/Thumbprint/有效期及与 Publisher 一致；只在指定隔离 guest 的 `LocalMachine\TrustedPeople` 信任核对过的公钥证书，不改宿主、不导出私钥。
3. 安装清单要求的 x64/neutral 包依赖和运行时，再 `Add-AppxPackage -Path <冻结MSIX> -DependencyPath <实际包依赖>`；没有包依赖时省略该参数，不传空字符串，不擅自改成自包含包。
4. 保存安装返回码、失败 ActivityId/部署日志及实际 Name/Version/Architecture/PackageFullName/PackageFamilyName/InstallLocation/SignatureKind；应为 `1.9.8.0 / X64`。
5. 从已安装 manifest 的 Application Id（当前 `App`）组成 `<实际PFN>!App`，通过 guest `shell:AppsFolder` 激活。**不使用 `winapp run <csproj/folder>` 重新建立开发身份/布局。**
6. `winapp ui list-windows --on sandbox --json` 发现 AppPid/HWND，核对可执行文件属于刚安装的包；每次重启/升级/重建 guest 重新发现，不复用旧 PID。

guest 实际配置为 `%LOCALAPPDATA%\Packages\<PFN>\LocalState\FolderRewind\config.json`，历史在该目录 `history\<ConfigId无连字符>\`；非 GUID ID 按 `HistoryRepositoryPaths` 编码，不猜路径。先核对实际配置与 Source 全部在本轮环境。

每条 UI 命令同时保留 `--on sandbox` 与 `-a <AppPid>` 或 `-w <HWND>`。文件选择器是同目标独立窗口，重新发现其 owner/句柄；不得把 guest PID 发到默认宿主目标。

### 2.3 单步命令与证据

每条 CLI 后立即核对退出码；失败不能被后续成功覆盖。示例是分组操作说明，不是新增 UI 脚本：

```powershell
winapp ui inspect --on sandbox -a $AppPid --interactive --json
winapp ui invoke HistoryBranchMoreActions --on sandbox -a $AppPid
winapp ui wait-for HistoryCompareWorkingState --on sandbox -a $AppPid -t 5000
winapp ui invoke HistoryCompareWorkingState --on sandbox -a $AppPid
winapp ui screenshot --on sandbox -w $MainHwnd --output <新的宿主PNG路径> --json
```

截图输出是宿主路径，确认非空并逐张用图像工具检查：裁切、重叠、错误主题、缺失文字、焦点与禁用状态。菜单/对话框立即截图，含独立弹窗时按实际帮助使用 `--capture-screen`。

紧密连续命令使用相同 `WINAPP_UI_WORKFLOW_ID`；每个新 shell 显式传值，暂停后重查 UI，结束 `winapp ui yield --on sandbox`。不另启独立录屏工作流阻塞输入。

每项结果记录 `caseId`、candidateSha256/sourceCommit、target/environment、PackageFullName/AppPid、前置、期望/实际、命令及退出码、文件断言、日志/树/截图路径、限制。状态为 `PASS / FAIL / BLOCKED / NOT_RUN / DEFERRED`；初始 NOT_RUN。服务、MSIX、UI、硬件分开汇总。

## 3. 基础数据与选择器

在 guest 任务 workRoot 的 `Fixtures` 内放模板和实际 Source。通过 UI 创建普通配置 `FR198-A`（Source A/B、Backup-A）和独立 `FR198-B`（故障隔离）。先做 B0→Main→Feature 三次备份并从指定批次创建分支，记录实际 ConfigId/SourceId/VersionId/RunId、分支和快照数，归档干净起点。

复用 `scripts/tests/Set-HistoryAcceptanceFixture.ps1` 的 B0/Main/Feature/Tiny；保持 guest checkout 相对结构并确认其 `.tmp/FolderRewindEval/SourceA` 在隔离根内。它仅准备明确文件，不创建备份/配置；不能在宿主运行后误认为 guest 已准备。每个破坏性组用独立配置或恢复任务环境。

文件断言包含相对路径集合、SHA-256、空目录集合；ADS/硬链接/只读另外取流、对象和属性证据。Archive/Packs 操作前后留摘要，不能只检查一个文本文件；验证成本不混入产品性能时间。

| 功能 | 已有 AutomationId（须复核实际树） |
|---|---|
| 选择/视图 | `HistoryConfigFilter`、`HistoryFolderFilter`、`HistoryPresentationSelector`、`HistoryViewSelector`、`HistoryBranchFilter` |
| 分支 | `HistoryAdvancedTools`、`HistoryBranchCheckout`、`HistoryBranchMoreActions`、`HistoryBranchMerge` |
| 比较 | `HistoryCompareWorkingState`、`HistoryWorkingComparison`、`HistoryCancelComparison`、`HistoryComparisonDetails` |
| 修复/恢复 | `HistoryRecoveryStatus`、`HistoryRetryRecovery`、`HistoryRepairWorkspace`、`HistoryOpenPreservation`、`HistoryRecoveryDetails` |
| 整页故障/取消 | `HistoryLoadError`、`HistoryRetry`、`HistoryLoadDetailsText`、`HistoryCancelOperation` |
| 列表 | `HistoryVersionList`、`HistoryRunList`、`HistoryVersionItem_<identity>`、`HistoryRunItem_<RunId>` |
| 还原/导出 | `HistoryVersionRestore_<VersionId>`、`HistoryRunRestore_<RunId>`、`HistoryExportVersion_<VersionId>`（先打开该条目菜单） |
| 分支对话框 | `HistoryRunCreateBranch_<RunId>`、`AppDialogTextInput`；实际对话框按钮 `PrimaryButton`/`CloseButton` |
| 维护/快照 | `HistoryMaintenanceDropDown`、`HistorySafetySnapshots`、`HistoryScanRecover`、`HistoryClearMissing` |
| 配置/等级 | `ConfigSettingsName`、`DestPathBox`、`ConfigSettingsTabBackup/Restore/Filter`、`ProjectSettingsSave`、`BackupVerificationBalanced/Strict`、`ConfigSettingsDialog_BackupMode` |
| 呈现 | `SettingsTheme`、`LogLiveRefresh`、`LogAutoScroll`、`LogEntries`；导航按实际树 |

动态后缀来自当前列表/持久化记录，禁止猜 GUID；重复 Source 按钮先限定 Run/Source 祖先，不取首个匹配。虚拟化条目先滚动生成。文本 TwoWay 设置后保存/切焦点，再核对持久化；没有精确 ID 时先源码/inspect 定位，不能坐标盲点。

## 4. 阶段一：历史、比较与修复

服务入口：HistoryRuntimeTests、HistoryPreparationRecoveryTests、HistoryWorkspaceRepairTests、HistoryCheckoutServiceTests、HistoryRestoreProtocolCrashTests、UiFailurePresentationTests。服务精确计数/强杀不冒充包内 UI；不把含旧物理绑定的探针 journal 复制到包容器。

| ID | 前置与操作 | 必须断言/证据 |
|---|---|---|
| R1-01 | A 有有效历史及 Workspace 故障；浏览 A、导出到不存在的新目录；切 B 备份 | A 列表可读、局部诊断、危险写受限；导出内容正确，B 可用；截图及文件摘要 |
| R1-02 | 相同历史配小/大 Source，进入普通/高级并切分支，不请求比较 | 不建 restore journal；零物化/内容读取精确证明引用既有服务测试，无包内计数不得声称独立测得 |
| R1-03 | 显式比较一致→改字节→占用→取消；期间切分支/离开 | Equal/Different/Unknown/取消区分，列表保留；旧结果不覆盖新选择；文件可再次打开、无目标事务；逐态截图 |
| R1-04 | 既有旧 v2 Preparing→快照提交→Rebind 前强杀，恢复两次 | 新 Workspace/快照/世界保留；无合法包内夹具时呈现部分 NOT_RUN，不改日志阶段 |
| R1-05 | 既有合法提交/Source 变化推进 Workspace 后恢复 | 不回滚新状态、不丢 Packs；可继续操作，前后摘要 |
| R1-06 | 既有准备中断及已登记/未知归属容器 | 准备残留不永久阻断；未知对象保留为维护，不错误删除 |
| R1-07 | 既有未知协议/损坏进度/执行矛盾恢复 | 保留材料并阻止危险写，完好历史可读；不得删 journal 绕过 |
| R1-08 | 解除故障后 `HistoryRetryRecovery` 再备份 | 已解除原因消失，尚存原因继续显示；不靠清健康位或重启掩盖 |
| R1-09 | 退出应用，A 的真实 Workspace 分别保全后移走、写坏、排他持有；未知格式/ACL 用既有测试或可靠隔离夹具 | 缺失/损坏可预览修复，占用/权限重试，未知格式禁止重建；保存原始字节/摘要，不造假基线 |
| R1-10 | 既有 Catalog/迁移/Workspace 独立与组合故障；检查对应提示 | 修一种不掩盖其余，局部故障不叫所有归档损坏；详情原因正确 |
| R1-11 | 损坏 A：修复先取消再确认，打开保全位置，退出重启后备份 | 取消不替换；说明未知基线/下次完整捕获；保全字节等于损坏原件，历史与 ConfigId/SourceId 不变，完整捕获新分支身份 |
| R1-12 | 既有竞态/保全失败/保全后/发布后强杀；UI 复核重试 | 不丢原件，重复修复不重置有效状态；不绕过待恢复执行事务；随机 Kill 不算精确窗口 |

Workspace 文件名从 `HistoryLocalStateStores.cs` 和真实诊断核对，在该配置 `history/.../local-state` 下。保全目录由预览/诊断给出，不猜路径、不放入普通临时清理。

## 5. 阶段二：范围、保护与恢复

各组独立干净起点，普通 Clean/Overwrite 与 Checkout Exact 分别验证。插件补丁仅在当前冻结插件支持且无需游戏互联时适用。

| ID | 操作/既有场景 | 必须断言 |
|---|---|---|
| R2-01～02 | NTFS 合成目标添加命名流后还原，回滚用既有服务/探针 | 成功不普遍阻断 ADS，不拼旧流到新内容；回滚保留原对象/流 |
| R2-03～04 | Source 根/祖先 Junction；本轮 guest LocalState 历史重定向（先退出应用） | 入口链接保留、实际内容正确、修复可用；真实保护目录重叠仍拒绝，不操作宿主 LocalState |
| R2-05 | 既有循环/断链/入口改变 | 拒绝不确定绑定，不向重连后的其他位置写；真实未知 tag 条件不足待验证 |
| R2-06 | 目标内与范围外硬链接，含只读；回滚用既有测试 | 按路径替换；范围外字节/属性不变，清理不改共享 ACL/属性 |
| R2-07 | 排除链接子树、白名单祖先、空目录、多 Source、保留和结构冲突，普通/批次还原 | 管理内容符合模式，边界外不变，不吞范围外目录；保真度正确；实际游戏玩家联动延期 |
| R2-08～10 | 既有准备/快照后/执行/提交/交接强杀，恢复两次 | 不丢原材料；修改前失败不改目标/永久阻断；终态与二次恢复一致；合法包内夹具再验提示 |
| R2-11 | 普通/Quick 快照开关及 Balanced/Strict；UI 保存后实际还原 | 快照数量/内容/等级正确，关闭快照仍有回滚；单次物化/补丁/验证引用既有 Host，不把 Balanced 标 Deep |
| R2-12 | Quick 一致/不同/同大小 mtime 改字节；Checkout Equal/Different/Unknown/同版本；比较取消 | 可靠一致才能 NoChanges/省保护，其余完整捕获当前字节；最终内容等于选择，取消不误报成功 |
| R2-13 | 既有同配置租约竞争；UI 完成后再次操作 | 无死锁/重复恢复/错误基线；不安排游戏请求 |
| R2-14 | 既有容量/绑定故障；真实磁盘满、USB 断连/换盘符需独立硬件 | 原数据保留，不误写其他设备；模拟通过不算真实硬件通过 |
| R2-15 | 既有清理失败→推进 Workspace→重启恢复，UI 检查维护提示 | 清理不依赖旧 revision、不动新世界；后续操作不重复发布旧结果 |

同大小/mtime 场景记录时间戳、改一个管理文件字节再还原时间戳；不能以改长度代替。guest 内核对文件，摘要/JSON 经 `target pull` 带回宿主。

## 6. 阶段三与 x64 MSIX

| ID | 操作 | 成功及延期边界 |
|---|---|---|
| R3-01～03 | 冻结包 Core Full/Smart、普通/Quick/Checkout、取消/占用/恢复结果 | 文件状态/快照/持久化符合前两节；请求接收、退出/重进组合 DEFERRED |
| R3-04 | 内置加载/启停及准确来源旧公开插件 | API/版本/来源摘要清楚；缺旧包 BLOCKED；模组、迟到/重复请求 DEFERRED |
| R3-05 | 独立干净 guest 分别安装已核来源 1.8.2/1.9.6/1.9.7 x64 包，生成 Full/Smart/加密数据；退出后安装同 PFN 1.9.8.0 | 不卸载/清 LocalState/强制降级；配置、历史、凭据语义及还原/导出/后续捕获正确；来源不明本地产物仅算夹具 |
| R3-06 | 合法事务/修复场景重启，恢复两次与后续完整捕获 | R1/R2 证据与实际包呈现分开 |
| R3-07 | 本轮隔离本地 rclone 与既有 Cloud 回归；Merge/Retention/删除/导出后捕获 | UI 终态、闭包/载荷正确；无真实云账户不声称真实云/多设备通过 |
| R3-08 | 干净安装 Native 默认关闭，内置版本/API/摘要，既有 opt-in 回归单列 | 不扩大实验默认范围，开发构建不替代候选 |
| R3-09 | 中英文；普通/高级；正常/空/加载/局部失败/修复/取消/重启；浅/深色、Tab/Enter/Escape | 无资源键、裁切/重叠、只有颜色的状态；焦点与保存正确；高对比不足 NOT_RUN，不改宿主主题 |
| R3-10 | 干净安装/首次启动/退出重启/同身份升级/备份还原导出/插件加载 | 实际 X64 1.9.8.0 关联冻结摘要，依赖完整，持久化在容器；Setup/ARM64/Store/线上更新 DEFERRED |

同身份升级参见 [Add-AppxPackage](https://learn.microsoft.com/en-us/powershell/module/appx/add-appxpackage?view=windowsserver2025-ps)。保存旧包 manifest/摘要/签名/来源；本机注册的开发包不等于正式 Store 版本。

### 6.1 既有脚本适用性

| 入口 | 复用及限制 |
|---|---|
| `scripts/tests/Set-HistoryAcceptanceFixture.ps1` | guest 隔离 checkout 内准备明确文件，不创建备份 |
| `scripts/tests/Test-HistoryBranchDialog.ps1` | 操作参考；没有 sandbox 路由，不能从宿主给 guest PID 直接调用；本轮按单步命令执行，不改 UI 脚本 |
| `scripts/tests/Test-WinUiReview.ps1` | 明确 Target/AppScope 的现有日志/主题检查；不代表全阶段，高对比选项仅 local，不能操作宿主生产桌面 |
| `tools/Test-NativeArtifact*Ui.ps1` | local/MSI acceptance-owner/专用构建前提，不能直接套冻结 MSIX |
| `tools/RestoreProtocolProbe` | 精确窗口/双次恢复是服务证据，不复制物理绑定 journal 到另一处 |
| `tools/BackupPerformanceHost` | 无窗口真实服务及性能；MSI 测试根不是 MSIX 安装/启动证据 |
| `scripts/tests/Test-Legacy182ReleasePayload.ps1` | 插件载荷/摘要参考；Setup 报告不替代 MSIX 升级及旧包来源 |

## 7. 非 UI/性能复核及失败处理

已有本轮原始结果优先引用，仅候选变化、结果缺失或失败时复验；串行执行，每次单独保留退出码/TRX：

```powershell
dotnet restore .\FolderRewind.slnx -p:Platform=x64
. .\.github\scripts\Initialize-CoreTestEnvironment.ps1
dotnet test .\FolderRewind.Tests\FolderRewind.Tests.csproj -c Release --filter 'TestCategory!=PerformanceBenchmark' --logger trx --results-directory <新目录>
dotnet test .\FolderRewind.Plugin.Runtime.Tests\FolderRewind.Plugin.Runtime.Tests.csproj -c Release --logger trx --results-directory <新目录>
dotnet test .\FolderRewind.Plugin.Abstractions.Tests\FolderRewind.Plugin.Abstractions.Tests.csproj -c Release --logger trx --results-directory <新目录>
```

缺真实工具不能将 Inconclusive 当通过。性能复用 `tools/performance/BackupRestore-README.md` 与冻结工具，两个正式基线、每组五次、正确性/API/总成本及计数分别保留。服务性能不能被 UI 成功替代，UI 自动化命令开销不混入产品计时。

失败立即保存包身份、候选 SHA、命令错误码、树、截图、日志和文件摘要，保留损坏/回滚材料。禁止改阶段、删 journal、清健康位或关验证“继续跑”。前置条件不足 BLOCKED，行为不符 FAIL。

执行 agent 不修改冻结包或产品来修复失败；提交复现/证据给实现任务。修复生成新候选后重跑受影响组及必要回归；旧证据不移植。取证前保留 guest，只清理确认本任务归属的合成材料。

完成报告独立列：既有服务/协议、实际 MSIX 安装/升级/UI、NOT_RUN/BLOCKED 的条件，以及 DEFERRED 的互联/渠道/发布。只有结构化结果和关键截图目视均完成才可 UI PASS；不宣称正式发行就绪。

## 8. 本轮冻结交接输入（构建后填写）

待 MSIX 构建、签名与冻结。缺少本节实际输入不得开始安装。

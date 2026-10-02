# 阶段 A：当前历史收尾验收记录

日期：2026-10-02，Asia/Shanghai。分支：`codex/history-stage-a`；起点：`bd0e0e9fc4d243da789b3a0baa7078f0e249bb39`。参考 `Reference/历史记录能力-初始建议.txt` 和 `Reference/2026-10-02-semantic-merge-assessment-建议核对.md`。

## 契约与范围

继续使用 ADR 0007 的 Source 独立分支。检查点父关系描述历史延续，版本父关系描述内容来源。阶段 A 没有新增插件公开 API、元数据格式或 Minecraft 语义合并能力。

- 请求来源引用的基线必须存在并属于该来源；预检及提交分别校验，包括 Unknown 状态中的非空引用。未请求来源不阻断独立捕获，pending journal 仍阻断配置写入。
- 首次失败仅写 BackupRun 事实，允许创建无版本、无分支的 Unknown 本机状态；已有和未参与来源的状态不变。
- Legacy 迁移事实 ID/内容确定，Pack/Transaction 随机。重复构建、不同顺序、重复导入、切换前取消及切换后绑定失败分别验证；已有仓库不因 Legacy 输入改变而被覆盖。

## 原 15 项失败的处置

| 原测试或分组 | 解释与当前保护测试 |
| --- | --- |
| `HistoricalBranchCreationRejectsIncompleteSourceCheckpoint` | 构造边界拒绝 null Version；分支服务另用合法单 Source 检查点验证缺 Exact representation 时不能建分支 |
| `MergeCheckpoint_RequiresTwoDistinctParents` | 合法单 Source 夹具同时验证单父、重复父拒绝及两条不同父通过 |
| `Checkpoint_AllowsUnavailableSourceWithoutVersion` | 替换为 `Checkpoint_RejectsUnavailableSourceWithoutVersion`，失败尝试由 BackupRun 测试验证 |
| `KnownEmptyRoster_IsAnExactCheckpoint`、`NullVersion_IsNotDeletionOrExactState` | 替换为合法空文件树版本的 Exact 准入及空 roster/null Version 构造拒绝 |
| `MultiSourceCheckoutFailureRestoresEverySourceAndLeavesWorkspaceUnchanged(false/true)` | 替换为 `SourceCheckoutFailurePreservesEverySourceAndWorkspace`，验证请求来源失败回滚和绑定变化拒绝；批次原子性由 `SourceHistoryTests.BatchRestoreIsAtomicAndPreservesIndependentBranchPointers` 保留 |
| `NonEquivalentReconciliationRequiresWinnerAndRejectsStaleTipSet` | 分支、检查点和版本使用同一 Source，再验证胜者和旧 tip 集合拒绝 |
| `JournalRecovery_CompletesWorkspaceAndCatalogAfterPackCommit` | 显式写入来源基线、活动分支、锚点及副本；验证恢复与重复恢复 |
| 三项 `HistoryPresentationQueryTests` | 补 Exact 表示和检查点，区分来源查询与项目聚合；同版本多个检查点独立展示；副本生命周期和 release 按版本隔离 |
| `FailedRunWithoutReliableVersionsCommitsOnlyActivityFact` | 除只写 BackupRun 外验证 Unknown 初始化；新增已有/未请求状态保持测试 |
| `PreflightThrowsWhenBaselineVersionIsMissing` | 明确请求 Source；验证未请求缺失基线不阻断；补跨来源引用及预检后提交复核、产物清理 |
| `ShuffledLegacyFactsProduceIdenticalObjectsAndBootstrapVector` | 保留事实与本机基线确定性，容器 ID 随机；补四项迁移/导入流程用例 |

原 CI 第 16 项上传取消超时不能由本地未复现宣称已修复。`CancellationDuringUploadDoesNotLockOutTheNextTask(1/2/3)` 使用异步屏障分别控制上传中、远端校验前、事实持久化后元数据同步。逐项验证取消结果、独占文件打开、操作门释放、重试成功，以及持久化副本复用；超时路径先取消并等待任务退出，再释放 Runtime 和目录。没有扩大五秒时限，没有以 sleep 同步。

## 新归档与恢复证据

`HistoryMergeArchiveFixture` 使用真实 7-Zip、CommitCoordinator、BranchService、CheckoutService、MergeService、CommitBuilder、ApplyService 和文件系统还原事务。Base 有两个文件，Ours/Theirs 分别修改一个；结果不同于三方，必须建新 Version 和归档。另一个来源真实建档并保持文件、基线和分支不变。

`HistoryMergeArchiveTests` 共 4 项：验证内容/历史父关系、Exact 和存储摘要、持久化准备结果跨 Runtime 重开复用、提交前重新物化、正式版本还原后的路径/字节/树摘要；验证深度校验失败、归档往返差异、准备后篡改均在写入前阻断。

`HistoryMergeArchiveRecoveryTests` 共 5 项，日志由真实 Apply 产生，未手工拼装：

| 故障位置 | 当次结果 | 两次重开后的结果 |
| --- | --- | --- |
| 文件已替换、Pack 发布前异常且回滚延期 | MutationFailedRecoveryRequired | 旧文件、旧 workspace/catalog；会话 Ready；可重试提交 |
| Pack durable 后阻断 catalog 写入 | CommittedRecoveryRequired | 向前恢复新文件、workspace/catalog；会话 Committed |
| durable 后清理异常 | CommittedWithPostActionWarning | 保留已提交事实并完成延期清理 |
| durable 后清理取消 | CommittedWithPostActionWarning | 保留已提交事实并完成延期清理 |
| 实际恢复暂停在屏障 | 同配置等待，其他配置独立 | 放行后恢复旧文件；锁正常释放 |

每个 Pack 边界场景验证 pending journal 拒绝预检/MutationGate，重复 Apply 不增加 Pack/合并 Version/Checkpoint/BranchUpdate，恢复后的新归档仍可正式还原。本证据为可控 I/O 故障和 Runtime 重启，不宣称做过进程强杀或真实 WinUI 人工操作。

## 旧保护意图映射

“现存/替代”只证明下表注明的行为；原测试类已删除时，不能沿用其名称作为执行证据。

| 旧保护意图/旧测试类 | 现存或替代测试 | 尚未覆盖的旧证据 |
| --- | --- | --- |
| R1 Exact 准入；`HistoryExactCheckpointAdmissionTests`、`HistoryMergeTests` | 现存 Exact 逻辑父/表示闭包；新 ArchiveTests 验证真实往返、篡改阻断和正式还原 | 待补：准备后首选副本丢失/损坏时替代副本选择的整链 |
| R2 durable 结果；`HistoryMergeApplyTests`、`NativeHistoryApplicationServiceMergeTests` | 替代 ArchiveRecoveryTests 验证 durable 前后和清理取消；现存 `HistoryMergeTests.CachePostActionFailurePreservesCommittedFacts` | 待补：durable 后 Session SQLite 写入/读取故障及应用外层结果映射 |
| R3 配置操作门；`HistoryRestoreServiceTests`、`BackupServiceTests` | 替代 ArchiveRecoveryTests 的真实恢复屏障；现存 JointTransactionRecovery、ConfigurationOperationGate、CommitCoordinator 测试 | 待补：WinUI Backup/SafetySnapshot 外层嵌套入口无死锁验收 |
| R4 清理；`MergeSessionStoreTests`、`HistoryMergeLifecycleTests` | 替代 ArchiveTests 重开准备与正式还原、ArchiveRecoveryTests 延期清理；现存 `DurableSessionRoundTripsResolutionAndKeepsRootsUntilAbandoned` | 待补：Session DB/schema 损坏时的清理阻断和大规模生命周期回归 |
| R5 最小协调；`HistoryMergeApplyTests`、`MineRewind.Tests.RestoreCoordinationTests` | 现存 SourceHistoryTests 的另一来源隔离；新 ArchiveFixture 限定真实写入来源 | 待补：真实游戏零写入协调、保护扩大范围及外层 scope 漂移；保留人工发布门槛 |
| R6 Provider 身份；`HistoryMergeProviderTests`、`HistoryMergeServiceTests` | 现存 `ProviderCannotEscapeOrDoubleClaimInputPaths`、持久化会话签名在 provider/policy 改变时失效 | 待补：非默认 Provider 实际 Apply；显式删除、新产物、逻辑单元属于阶段 B，不提前宣称支持 |

以上待补项明确保留，不由阶段 A 的新增用例冒充旧全面验收；阶段 A 门槛限定为原失败收尾、新归档整链和 Pack 持久化边界恢复。真实 UI、游戏加载与稳定 SDK 发布仍阻塞。

## 执行与结果

本地 .NET SDK `10.0.401`，配置 Release。三个项目均使用 `dotnet test <project> -c Release --no-restore --logger trx`。

| 工程 | 通过 | 失败 | 跳过 |
| --- | ---: | ---: | ---: |
| Host | 681 | 0 | 0 |
| Plugin Runtime | 138 | 0 | 0 |
| Plugin Abstractions | 15 | 0 | 0 |
| 合计 | 834 | 0 | 0 |

另运行 `./scripts/tests/Test-HistoryUploadCancellation.ps1`，20 轮 × 3 个时点全部通过。TRX 本地目录：`%TEMP%/folderrewind-stage-a-release/{host,runtime,abstractions,cloud-cancellation}`；临时目录不作为长期证据。

本地 Host Release x64、x86、ARM64 构建全部通过，各为 0 警告、0 错误；参数与工作流一致：`-p:Platform=<架构> -p:CETCompat=false -p:GenerateAppxPackageOnBuild=false`。本地构建包含既有、未纳入提交的中文资源工作区修改；远端 CI 使用干净提交。

长期证据由 [Plugin v3 Hardening](https://github.com/Leafuke/FolderRewind/actions/workflows/plugin-v3-hardening.yml) 保存：要求真实 `FOLDERREWIND_TEST_7Z`、三套完整 TRX、20 轮取消 TRX、零跳过检查，以及 x86/x64/ARM64 Release 构建且警告视为错误。`stage-a-test-results` artifact 内的 `stage-a-acceptance.md` 自动写入最终提交 SHA、SDK、配置、测试计数和运行 URL，避免手工记录与被验收提交错位。失败时也上传已经产生的 TRX。

本文件记录本地已执行结果；最终阶段 A 通过须以对应提交的全部 CI 作业成功为准，不能引用旧 `c3fdd488` 的红灯或旧模型全绿数字。没有执行真实 WinUI、Minecraft 插件或游戏加载验收。

首次远端验收的 Host 测试持续多分钟未返回。CI 因此增加每个测试两分钟的 hang 诊断，挂起时保留执行顺序证据而不生成大型 dump；这不改变上传重试的五秒保护时限。最终 CI 若暴露真实挂起，必须定位修复后重跑，不能以本地通过关闭。

## 提交边界

1. `78df5ba` — `fix(history-model): 对齐单来源检查点与分支保护测试`
2. `d617d3c` — `fix(history-projection): 修正历史投影与恢复夹具的来源状态`
3. `c6773a5` — `fix(history-preflight): 在捕获前阻断请求来源的缺失基线`
4. `6ac7472` — `fix(history-commit): 明确失败备份的本机状态初始化契约`
5. `13cf0e3` — `fix(history-migration): 对齐迁移事实身份与重试幂等契约`
6. `e385f61` — `fix(history-cloud): 补齐上传取消与重试的资源释放保护`
7. `0cbf5d2` — `fix(history-merge): 补齐新归档合并与正式还原整链验证`
8. `0bc14dd` — `fix(history-recovery): 补齐合并持久化边界的恢复验证`
9. 当前文档及 CI 提交 — `fix(history-ci): 固化阶段A回归门槛与当前验收证据`

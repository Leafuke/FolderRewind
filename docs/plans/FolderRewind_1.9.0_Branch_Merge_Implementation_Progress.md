# Branch Merge 整改历史记录与当前证据入口

当前 Source 模型验收见 [阶段 A 验收记录](History_Stage_A_Acceptance_2026-10-02.md)。本文件第 2–8 节保留 2026-09-23 与 2026-09-26 的历史记录；旧测试类名及 815/776 项统计不能作为当前模型的通过证据。旧保护意图的现存、替代及待补测试映射见新记录。

以下历史内容记录依据 [完整整改计划](FolderRewind_1.9.0_Branch_Merge_Hardening_Plan.md) 与 [实现质量评估报告](../reviews/FolderRewind_1.9.0_Branch_Merge_Implementation_Review_2026-09-22.md) 当时完成的 R1–R6 修复及验证。

## 1. 当前状态与发布 Gate

**发布 Gate 状态：当前关闭（CLOSED）**

2026-10-02 阶段 A 本地 Release 回归：Host 681、Plugin Runtime 138、Abstractions 15，共 834 项通过，零失败、零跳过。上传取消的三个时点另连续执行 20 轮，共 60 项通过。最终提交及 CI 完成状态以新记录所述、绑定具体 SHA 的工作流结果为准。

由于尚未在复制目录与测试 Minecraft 世界中完成真实破坏性人工验收，本阶段**明确保持发布阻塞，不声称具备发布条件**。待人工验收全部通过后，再单独提交验收记录并关闭发布阻塞项。

## 2. 实施进度对照（四列表）

| 阶段 / 任务项 | 代码接通 | 自动测试 | 故障注入 | 人工验收 |
| --- | :---: | :---: | :---: | :---: |
| **C00：计划与验收基线固化**（R1–R6 边界、提交顺序、门槛） | 已完成 | 已完成 | 不适用 | 不适用 |
| **R1 · C03/补丁：最终 Exact 准入**（实际可恢复性、FF/ThreeWay 闭包、有效替代副本） | 已完成 | 已完成 | 已完成 | 待执行 |
| **R2 · C02/补丁：提交事实持久化**（durable commit 隔离后续异常、5 态区分、缓存同步） | 已完成 | 已完成 | 已完成 | 待执行 |
| **R3 · C01/补丁：统一配置操作门**（Config→Runtime→Store 锁序、lease 恢复、Capture 阻断） | 已完成 | 已完成 | 已完成 | 待执行 |
| **R4 · C06：产物生命周期与清理**（持久 payload 独立封存、终态自动清理、审计保留） | 已完成 | 已完成 | 已完成 | 待执行 |
| **R5 · C07：准备与协调解耦**（提前准备、零写入不退出、按需扩大快照、scope 漂移阻断） | 已完成 | 已完成 | 已完成 | 待执行 |
| **R6 · C04：Provider / Policy 规范身份**（`id@version;schema=n`、Host 校验、多 provider） | 已完成 | 已完成 | 已完成 | 待执行 |
| **C08：MineRewind 恢复边界与范围**（多世界活跃阻断、非涉及世界不干扰、Recovery 不 rejoin） | 已完成 | 已完成 | 已完成 | 待执行 |
| **C09：按规范签名保留重算成果**（输入/边界/摘要签名匹配迁移、manual 校验、新旧 roots 交接） | 已完成 | 已完成 | 已完成 | 待执行 |
| **C10：显式按需准备云端副本**（依赖优先下载 helper、去重、gate 外网络、tip 漂移重算提示） | 已完成 | 已完成 | 已完成 | 待执行 |
| **C11：算法与交互性能优化**（线性 BCA、按 Source/500 keyset 分页、单 CAS 批量 resolution） | 已完成 | 已完成 | 已完成 | 待执行 |
| **C12：UI 状态与操作反馈完善**（Typed Diagnostic、本地化文案、creation kind 区分 Merge） | 已完成 | 已完成 | 已完成 | 待执行 |
| **C13：端到端生命周期与故障回归**（真实 ZIP/catalog、重启恢复单条 Update、合并后 Exact 还原） | 已完成 | 已完成 | 已完成 | 待执行 |
| **C14：实现结果与自动化证据归档**（更新 ADR、插件说明、进度表、构建与测试结果） | 已完成 | 已完成 | 不适用 | 待执行 |

## 3. Git 提交列表与哈希对照

| 编号 | 仓库 | Commit Hash | 提交标题与摘要 |
| --- | --- | --- | --- |
| C00 | Host | `9f831c1` | `docs(merge): 记录完整整改计划与发布验收门槛` |
| C01 | Host | `99cc10b` | `fix(history): 在配置操作门内协调恢复与备份准入` |
| C02 | Host | `65ad13e` | `fix(merge): 保留持久提交结果并隔离提交后失败` |
| C03 | Host | `9b21ea0` | `fix(merge): 在提交前验证实际 Exact 表示闭包` |
| C04 | Host | `b18a465` | `fix(merge): 固定 provider 与 policy 身份` |
| C05 | Host | `568158c` | `refactor(merge): 持久化准备结果并独立封存合并产物` |
| C06 | Host | `bb719b3` | `fix(merge): 自动清理终态会话并保护已发布产物` |
| C07 | Host | `ef8095f` | `fix(merge): 在环境协调前完成准备并缩小协调范围` |
| C08 | MineRewind | `6708d6d` | `test(restore): 覆盖合并协调范围与恢复结果边界` |
| C09 | Host | `4d264d7` | `feat(merge): 按完整输入签名保留重算成果` |
| C10 | Host | `dfd471a` | `feat(merge): 支持显式准备所需云端副本` |
| C11 | Host | `34b7c35` | `perf(merge): 优化共同祖先查询与冲突读取` |
| C12 | Host | `fd2e6cc` | `fix(ui): 完善合并状态与操作反馈` |
| 补丁 | Host | `457ab50` | `fix(history): Deep 校验跳过损坏副本并选择有效 Exact 副本`（R1 补充） |
| 补丁 | Host | `c151d96` | `fix(history): 刷新缓存 Runtime 的运行期恢复状态`（R3 补充） |
| 补丁 | Host | `0863bfa` | `fix(merge): 完成恢复索引刷新并验证提交后故障边界`（R2 补充） |
| C13 | Host | `acca7b5` | `test(merge): 补齐端到端故障与生命周期回归` |
| C14 | Host | *当前 HEAD* | `docs(merge): 更新实现结果与自动化验收证据` |

## 4. R1–R6 关闭证据对照

### R1：最终 Exact 准入（验证实际可恢复性）
- **实现机制**：通过 `RepresentationRuntime.AssessRepresentationExactAsync` 建立结果级 Exact assessment 入口。在 `HistoryMergeCommitBuilder` 中对 FF 复用 Version、ThreeWay 复用 Version、新 Merge Version 做深度评估；存在 storage hash 时比对 digest 与大小，否则执行物理解包与 manifest round-trip 校验。最终受控本机 payload 在提交期间保持只读文件句柄保护。
- **降级与替代**：当首选副本缺失或损坏时，自动探测并选择同 Version 的其他有效 Exact 副本；只有当无可恢复副本时才阻断提交并返回结构化诊断，不推进 Branch，不生成不完整 Merge Update。
- **回归与故障注入**：`HistoryExactCheckpointAdmissionTests`、`HistoryMergeTests`（准备后删除副本、首选副本哈希损坏跳至替代副本、所有副本失效时阻断提交）。

### R2：提交结果持久化（durable 事实不可被后续异常覆盖）
- **实现机制**：分离 pack durable 事实与后续动作结果。在 filesystem executor 返回成功后，立即固定 `TargetCommitted = true`、`WorkspaceUpdated = true` 和 `AppliedSources`。后续 Session DB 保存失败、读异常或取消 token 无法撤销提交事实。明确划分 5 种状态：`BlockedBeforeMutation`、`MutationFailedRolledBack`、`MutationFailedRecoveryRequired`、`CommittedRecoveryRequired`、`CommittedWithPostActionWarning`。
- **恢复与重试**：Session 更新失败时返回 `CommittedRecoveryRequired`；重启恢复使用原 transaction/pack identity，不生成第二条 Merge Update。capture baseline 同步成为独立 post-action，失败安全失效缓存并不影响提交。
- **回归与故障注入**：`HistoryMergeApplyTests`、`NativeHistoryApplicationServiceMergeTests`（pack durable 后 Session SQLite 写入抛异常、Session DB 无法读取、提交后取消 token、缓存失效故障注入）。

### R3：统一配置操作门与恢复准入
- **实现机制**：锁顺序固定为 `Config operation gate → Runtime mutation/recovery gate → 本机状态存储锁`。公开恢复入口获取配置操作门；内部恢复入口接收 `ConfigurationOperationLease`，避免重复获取非重入锁造成死锁。
- **准入阻断**：未完成联合事务 journal 纳入 readiness 计算与 `EnsureReadyAsync` 检查（即使返回缓存的 Runtime 也会发现未完成 journal）。Backup 在读取 live tree 及获取 capture consistency lease 前检查并恢复 pending journal。
- **回归与故障注入**：`HistoryCommitCoordinatorTests`、`HistoryRestoreServiceTests`、`BackupServiceTests`（pending journal 阻断 Backup、恢复与 Capture barrier 互斥、跨配置并发独立、Safety Snapshot 内部入口无死锁）。

### R4：产物生命周期与自动清理
- **实现机制**：引入 `PreparedMergeDescriptor`，记录 session/plan/resolution/provider 身份、候选 facts、manifest 摘要和产物 ownership。最终候选归档直接封存到 repository `payloads/<RepresentationId>/`，不再向 Session 临时目录写入持久产物。
- **清理与保留**：Session 提交或放弃后，自动清理解包输入、manual 导入和旧 revision 临时文件；catalog 引用文件与 pending intent 保护的 payload 严禁误删；SQLite 数据库或 schema 损坏时 fail closed，不删除 immutable facts。
- **回归与故障注入**：`MergeSessionStoreTests`、`HistoryMergeLifecycleTests`（成功清理、放弃清理、重启清理、catalog 引用文件保留、损坏 schema 安全 fail closed）。

### R5：准备与协调解耦及协调范围最小化
- **实现机制**：将合并拆分为 `PrepareMergeAsync`（输入解包、冲突解决、结果复制、压缩、hash、解包 round-trip 和元数据计算全部在 coordinator 外完成）与 Apply 执行两阶段。
- **协调范围**：根据实际变更 Source 计算最小协调范围。零写入变更跳过游戏退出协调；仅协调实际写入的 Source；仅当配置级 Safety Snapshot 明确需要时才扩大 scope；进入 continuation 后若探测到范围变化，阻断并提示重试。
- **回归与故障注入**：`HistoryMergeApplyTests`、`MineRewind.Tests.RestoreCoordinationTests`（零写入不退出游戏、PreserveCurrent Source 过滤、Snapshot 扩大 scope 协调、scope 漂移阻断）。

### R6：Provider 与 Policy 规范身份
- **实现机制**：引入 Host 内部 `MergeProviderDescriptor`，规范化 `id@version;schema=n` 与 policy `id@version`；由 Host 选择后传入 planner，写入 plan、PreparedMergeDescriptor 和 Merge provenance。版本或 schema 变更使旧处理结果失效；Host 严格校验 provider 输出路径、输入签名与冲突归属。
- **回归与故障注入**：`HistoryMergeProviderTests`、`HistoryMergeServiceTests`（非默认假 provider Prepare 与执行、provider 版本/schema 变更失效旧决策、越界路径与重复认领拒绝）。

## 5. 性能与算法优化记录

- **线性 Best Common Ancestor (BCA)**：
  - 算法：获取全部共同祖先集合后，单次拓扑剔除共同祖先集合中各节点的直接 parents，获得最佳共同祖先 candidates，消除平方级回溯。
  - 规模验证：50,000 节点长链/分叉/criss-cross 图，访问节点与边共 300,015 次，算法具备严格线性复杂度；小图与朴素图遍历 oracle 比对结果一致。
- **流式与分页冲突读取**：
  - 构建器按 Source 流式处理，移除全会话 `ToLookup` 内存全量加载。
  - 数据库采用固定 500 条 keyset 分页，UI 采用 100 条分页。
  - 100,000 条冲突测试中，数据库仅执行 204 次 keyset 读取，批量 resolution 仅消耗单个 CAS 事务，实测耗时约 6.3s，进程峰值 Working Set 约 120MiB（机器观测记录，不设依赖机器性能的时间硬断言）。

## 6. 全量自动化验证与多架构构建结果

### 自动化测试工程矩阵

| 测试工程 | 测试数量 | 失败 | 通过 | 跳过 | 耗时 |
| --- | :---: | :---: | :---: | :---: | :---: |
| `FolderRewind.Tests` (Host) | 609 | 0 | 609 | 0 | 19s |
| `FolderRewind.Plugin.Runtime.Tests` (Plugin Runtime) | 144 | 0 | 144 | 0 | 4s |
| `FolderRewind.Plugin.Abstractions.Tests` (Abstractions) | 15 | 0 | 15 | 0 | 39ms |
| `MineRewind.Tests` (Minecraft 插件) | 47 | 0 | 47 | 0 | 9s |
| **总计** | **815** | **0** | **815** | **0** | **32s** |

### 跨架构编译与构建矩阵

| 架构 / 配置 | 目标工程 / 解决方案 | 警告 | 错误 | 状态 |
| --- | --- | :---: | :---: | :---: |
| Debug x64 | `FolderRewind.slnx` | 0 | 0 | **PASS** |
| Release x64 | `FolderRewind.slnx` | 0 | 0 | **PASS** |
| Debug ARM64 | `FolderRewind.slnx` | 0 | 0 | **PASS** |
| Debug x86 | `FolderRewind.slnx` | 0 | 0 | **PASS** |
| Debug x64 | `FolderRewind-Plugin-Minecraft.slnx` | 0 | 0 | **PASS** |

## 7. 待执行人工验收清单与尚未完成事项

人工验收必须在复制的测试目录与测试 Minecraft 世界上执行，不得在正在运行的真实生产环境做故障注入。

### 人工验收清单（8 项）

1. **双 Source 自动合并与 Manual 导入**：两个 Source 自动合并、文件冲突选择、manual 导入文件，确认提交后 source tip 保持原位。
2. **重启恢复与 Stale 拒绝**：Session 中断后关闭应用，重启后继续该 Session；若推进 source 分支，确认旧 Session 明确标记为 Stale 并拒绝直接 Apply。
3. **Dirty 工作安全快照**：未保存工作目录触发独立 Safety Snapshot，合并后实际从该 Snapshot 还原，验证未提交内容完好。
4. **Binding 修复与成果保留**：缺失 binding 导致阻断；修复 binding 后重算，确认完整签名一致的 resolution 被完整复用，current-only Source 不丢失。
5. **.mca 双改与多世界协调**：`.mca` 双改正确显示为普通文件冲突；双世界配置下，第二个世界活跃正确触发退出协调；两个世界均活跃无法唯一退出时整次阻断。
6. **零写入跳过退出**：仅元数据/无变更的合并操作不调用游戏退出握手；需要快照时按需扩大协调范围。
7. **普通 Restore 玩家数据与 Merge/Checkout 隔离**：普通 Restore 保留玩家数据产生 Derived baseline；Merge 与 Checkout 严禁触发任何 post-commit NBT 写回。
8. **清理后 Exact 还原**：合并成功且自动清理 Session 后，重启应用，从正式 merged tip 执行 Exact Restore，验证内容字节级一致。

### 尚未完成事项
- 执行上述 8 项破坏性人工验收并收集实机操作截图与日志证据。
- 人工验收通过后，提交 `docs(merge): 记录人工验收结果并关闭发布阻塞项` 并解除发布 Gate。


## 8. 2026-09-26 实际验收修复补充

本次修复纠正了实际 UI 验收暴露的线程、真实归档集成、诊断与布局问题。完整根因、环境状态和人工操作步骤见 [历史验收修复与人工复测](History_Evaluation_Fixes_2026-09-26.md)。

- 分支输入在 UI 线程创建控件、等待并读取结果，共用交互入口同步修复。
- 真实 7-Zip + 深层会话路径复现：CreateProcess 的 WorkingDirectory 过长导致“目录名称无效”，发生在写来源前；改用短工作目录和绝对输入通配路径后通过，归档条目仍为相对路径。
- 合并失败记录阶段、完整异常和脱敏进程输出，界面提供可复制详情，取消及提交后故障保持准确语义。
- 合并布局采用尺寸约束、溢出菜单、原生底部操作和可折叠详情；实际 DPI/语言矩阵交由用户人工复测，不以构建成功替代 UI 验收。
- 核心校验复用运行时健康判断；空历史允许 Missing，损坏、不可访问或未完成事务仍阻断。旧测试历史已可恢复归档，重新初始化后的应用自动校验通过。
- 本轮 Host 617、Plugin Runtime 144、Abstractions 15，共 776 项全部通过、零跳过；此数字不包含本轮未重跑的 MineRewind.Tests，不沿用上一轮的总计。末次诊断微调后 59 项合并定向测试再次通过。
- x64 Release 及 WinUI 开发包装脚本构建通过，零警告零错误。保留用户要求的人工验收步骤，不宣称完整 UI 闭环已验证。

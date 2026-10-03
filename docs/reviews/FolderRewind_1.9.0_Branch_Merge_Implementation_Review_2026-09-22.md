# Branch Merge 实现质量评估（2026-09-22）

结论：主要领域模型、文件算法和事务骨架已实现，正常流程具备相当完整度；但“步骤 1–14 已接通”不能等同于发布条件已满足。本次复现了三个涉及 Exact 可恢复性、提交结果语义和恢复期间备份准入的问题，建议保持发布 Gate 关闭，优先修复这些问题，再完成计划中的人工验收。

这是评估结果，不是实施授权或修复提交。计划文档作为需求与验收基准；未把文档中的 Agent 实施命令当作本次任务指令。未修改产品代码，未操作真实 Minecraft 世界。

## 审查基线与验证范围

| 项目 | 本次实际基线 |
| --- | --- |
| Host | `688a1dac0f2773ad29b1b928a1453668b9146ffe` |
| MineRewind | `eea38e84c66a273b863fb36fb9207917f49a41cc` |
| 工作区 | 审查开始时两个仓库均无未提交修改 |
| 需求 | [实施计划](D:/Programs/FolderRewind/docs/plans/FolderRewind_1.9.0_Branch_Merge_Implementation_Plan.md) |
| 自报进度 | [2026-09-18 实施进度](D:/Programs/FolderRewind/docs/plans/FolderRewind_1.9.0_Branch_Merge_Implementation_Progress.md) |

本次实际运行：

- Host 定向测试 51 项通过：Merge、Apply、联合事务恢复、Retention safety、Exact checkpoint admission、Checkout。
- Plugin Runtime 定向测试 18 项通过：continuation gate、vertical slice、Host services gate。
- MineRewind 定向测试 29 项通过：V3 vertical slice 与 Manifest。
- `dotnet build FolderRewind.slnx --no-restore -c Debug -v minimal` 成功，0 warning / 0 error。本机默认解决方案构建输出是 ARM64；不能据此声称验证过 x64/x86 发布包。
- 另外编写三个临时回归探针，分别按预期安全行为断言，三个均失败，具体结果见下文。探针在现有 Host 测试工程内执行，执行后已移出工程，副本保存在 [.tmp/branch-merge-review](D:/Programs/FolderRewind/.tmp/branch-merge-review)。没有创建新测试工程。

未进行运行中游戏、WinUI 实机交互、云端真实副本、进程强杀或断电验证。故障注入使用临时测试目录与可控后端；不等同于真实存储设备故障测试。

## 计划对照结论

| 计划范围 | 评估 | 主要依据与保留意见 |
| --- | --- | --- |
| 配置 DAG、Source ancestry、Workspace anchor | 主体完整 | checkpoint parents 与 creation kind、独立 anchor、跨分支 ownership、Merge provenance 均有实现；没有用表示依赖代替语义祖先 |
| No-op / FF / BCA 与 roster | 主体完整 | 唯一 best base、无 base、多 base、unknown 与 absent、整 Source 冲突均有明确分支；大历史量的图查询尚需优化 |
| 通用文件级合并 | 实现较扎实 | 字节摘要、域分隔、空文件与不存在区分、路径结构冲突、大小写冲突、Session-owned 人工文件与 staging 校验均可见 |
| Session 与 roots | 主体完整，生命周期有缺口 | SQLite/CAS、分页、重启恢复、Retention/release/delete 保护已接入；成功后的临时产物清理入口缺失 |
| Exact 产物与最终准入 | 未闭合 | 新产物有 archive verification 与真实 round-trip；复用旧 Version 在最终提交前没有重新验证实际可满足的 Exact closure |
| 原子 Apply 与恢复 | 骨架正确，错误分支未闭合 | pack 前回滚、pack 后向前恢复、逐 Source started 记录均有实现；Session 写入失败会覆盖 durable commit 结果，备份准入没有覆盖运行期未完成 journal |
| Minecraft 边界 | 范围控制正确 | 仍使用通用文件 provider；多目标协调、第二个世界活跃、RecoveryRequired 不 rejoin、普通 Restore staging proposal 均有实现及定向测试 |
| UI 与发布验收 | 功能入口存在，尚需完善 | 支持会话、分页、批量选择、人工导入、预览、重算；实际世界验收尚未完成，provider 替换与准备阶段编排也未完全兑现计划 |

优点尤其在于没有混淆三种图：BranchUpdate 控制因果、Checkpoint/Source 的语义祖先、Representation 的物理依赖。共用 Restore filesystem executor 和单个联合 journal 的方向也正确，避免了“先 Restore 再额外 Commit”的双事务设计。建议保留这些结构，修复边界，不需要重写整个 Merge 模块。

## 需要优先修复的问题

### R1 · P1：准备后失去 Exact 副本的复用 Version，仍能成为新 target tip

证据：[HistoryMergeCommitBuilder.cs:95](D:/Programs/FolderRewind/FolderRewind/History/Application/HistoryMergeCommitBuilder.cs:95)、[HistoryMergeApplyService.cs:60](D:/Programs/FolderRewind/FolderRewind/History/Application/HistoryMergeApplyService.cs:60)、[HistoryExactCheckpointAdmission.cs:84](D:/Programs/FolderRewind/FolderRewind/History/Application/HistoryExactCheckpointAdmission.cs:84)。对应计划第 8 节“所有复用 Version 在最终提交时也必须具有可满足所需 fidelity 的表示闭包”。

Session 准备时会把输入解包到 Session 目录。Apply 时，builder 对复用 Version 只重新读取 Version 元数据，使用之前复制的文件构建 staging。最终验证检查 staging 的摘要及 representation 的声明关系，却不调用实际 environment assessment 重新确认该复用 Version 的 Exact 可用性。working-state probe 检查的是当前 baseline，也无法代替对将采用的 Theirs Version 的检查。

复现：建立可正常准备的 FF Session；准备完成后让 Theirs 的 representation handler 对该 Version 返回 `Unavailable`，保留 Ours 的可用性；随后 Apply。预期阻断，实际返回 `Committed`。探针为 [BranchMergeReviewProbeTests.cs](D:/Programs/FolderRewind/.tmp/branch-merge-review/BranchMergeReviewProbeTests.cs)。

影响：工作目录可能已经正确得到新字节，但新 tip 引用的 Version 没有实际可恢复表示。Session 中的原始解包目录不属于已注册的 Exact representation，不能用它证明后续 Restore 能成功。Session roots 能阻止受管理 GC，却不能阻止外部删除、损坏或远端可用性变化。

建议：在最终 Runtime gate 内对所有结果 Version 做实际 Exact assessment；新 representation 校验其封存 payload，复用 representation 校验选定 closure。必要时从已验证的 Session 内容补建该 Version 的合法自包含表示，并纳入同一 pack/catalog intent；否则阻断，不推进 Branch。增加“准备后副本丢失/损坏”的 FF 与 ThreeWay 复用测试。

### R2 · P1：pack 已 durable 后，Session 完成状态保存失败会被误报为变更失败

证据：[HistoryMergeApplyService.cs:95](D:/Programs/FolderRewind/FolderRewind/History/Application/HistoryMergeApplyService.cs:95)、[HistoryMergeApplyService.cs:105](D:/Programs/FolderRewind/FolderRewind/History/Application/HistoryMergeApplyService.cs:105)。对应计划第 9.3、14 节。

`ExecuteMutationAsync` 已返回成功后，代码继续保存 `MergeSessionState.Committed`。如果这次 SQLite 写入抛异常，外层 catch 看到 Session 仍为 `Applying`，统一返回 `MutationFailedRecoveryRequired`。这时 pack、BranchUpdate、Workspace 和文件都可能已经完成提交，返回值却令 `TargetCommitted == false`。

复现：仅对 Session 转为 Committed 的 UPDATE 注入 SQLite abort；先断言仓库已有一条 Merged Update，再断言应返回 `CommittedRecoveryRequired`。实际得到 `MutationFailedRecoveryRequired`。探针为 [BranchMergeCommitStatusReviewProbeTests.cs](D:/Programs/FolderRewind/.tmp/branch-merge-review/BranchMergeCommitStatusReviewProbeTests.cs)。

建议：保存 executor 已确认的 commit 结果，并在后续异常中保留该事实；无法完成 Session 记录时返回 committed recovery/warning 状态，不能降级为“变更未提交”。恢复查询不可再依赖一个可能同时不可读的 Session DB：当前 catch 中再次 Load 也可能抛异常，使整个结果处理失效。

同类风险还存在于生产封装：[NativeHistoryApplicationService.Merge.cs:49](D:/Programs/FolderRewind/FolderRewind/Services/NativeHistoryApplicationService.Merge.cs:49) 在提交后仍用原请求 cancellation token 同步 capture baseline。取消或同步初始化异常会在返回结果前逸出；[NativeHistoryRestoreOrchestrator.cs:99](D:/Programs/FolderRewind/FolderRewind/Services/NativeHistoryRestoreOrchestrator.cs:99) 尚未记录 mutationResult，因而可能走“修改前阻断”分支。这一相关路径为静态确认，未对真实 Host 封装做故障注入。应把缓存同步作为独立的提交后动作，失败保留 committed 结果并安全失效缓存。

### R3 · P1：运行期未完成联合事务没有阻止备份进入 Capture，恢复也未使用配置操作门

证据：[BackupService.Transaction.cs:113](D:/Programs/FolderRewind/FolderRewind/Services/BackupService.Transaction.cs:113)、[HistoryCommitCoordinator.cs:193](D:/Programs/FolderRewind/FolderRewind/History/Application/HistoryCommitCoordinator.cs:193)、[HistoryRuntime.cs:138](D:/Programs/FolderRewind/FolderRewind/History/Application/HistoryRuntime.cs:138)、[HistoryRestoreService.cs:42](D:/Programs/FolderRewind/FolderRewind/History/Application/HistoryRestoreService.cs:42)。对应计划第 9.2、9.3 节。

启动恢复已实现，但运行期间 Apply 留下 incomplete journal 后，`EnsureReadyAsync` 可以返回已经缓存的 Runtime。备份的 boundary preflight 只检查 Health 和 Workspace 文件；Health 的计算未纳入 incomplete restore journal。Runtime gate 的 `RequireRecovered` 在最终 mutation 阶段才触发，不能阻止此前 Capture 读取尚未恢复的目录。

复现：构造已开始 filesystem mutation、尚未提交 pack 的 journal；Workspace JSON 仍有效。对同一配置执行备份使用的 `FindRequiredBoundaryRecapturesAsync`，预期拒绝，实际无异常。探针为 [BranchMergeRecoveryPreflightReviewProbeTests.cs](D:/Programs/FolderRewind/.tmp/branch-merge-review/BranchMergeRecoveryPreflightReviewProbeTests.cs)。

此外，`RecoverIncompleteAsync` 只获取 Runtime recovery gate，未进入配置 operation gate。结合上述放行路径，另一入口触发恢复时可能与已经开始的 Capture 重叠。这里实测证据是“捕获前检查放行”；完整并发混合捕获是根据锁路径推导的风险，本次没有声称已实测生成混合 archive。

建议：统一 recovery/readiness 协调入口，按“Config operation gate → Runtime gate”锁顺序检查并恢复；Capture 必须在读 live tree 之前完成该检查。明确区分已持配置门的内部 primitive 与公开入口，避免重入死锁。用 barrier 测试 pending journal、正在 Capture 和恢复入口的互斥关系。

## 功能完整性与工程质量改进

### R4 · P2：成功 Session 的临时输入缺少自动清理，最终 payload 又与 Session 目录耦合

证据：[HistoryMergeService.cs:58](D:/Programs/FolderRewind/FolderRewind/History/Application/HistoryMergeService.cs:58)、[HistoryMergeCommitBuilder.cs:104](D:/Programs/FolderRewind/FolderRewind/History/Application/HistoryMergeCommitBuilder.cs:104)、[MergeSessionStore.cs:190](D:/Programs/FolderRewind/FolderRewind/History/LocalState/MergeSessionStore.cs:190)、[HistoryMergeInteraction.cs:97](D:/Programs/FolderRewind/FolderRewind/Services/HistoryMergeInteraction.cs:97)。

`CleanupTerminalArtifacts` 的生产调用仅在 UI“放弃”操作中。正常成功仅将 Session 标记为 Committed，executor 只清理结果 staging；B/O/T 解包输入、manual、旧 plan 及失败尝试的产物不会自动清理。长期只成功合并、不点击放弃任何会话的用户会累计大量文件。

最终归档也位于 `local-state/merge-sessions/<id>/payloads`，直接作为 catalog locator。现有清理函数会保留 catalog 指向的文件，因此不能据此断言“当前清理必然删掉最终 payload”；问题是持久产物与 Session 的生命周期被耦合，且偏离计划第 8 节。

建议：将最终 payload 在提交前封存到专用持久表示目录；在 committed 恢复完成后及启动维护时执行可重试的终态清理。对 active Session 保留人工成果，对 terminal Session 保留轻量审计记录。新增成功→清理→重开→Exact Restore 的生命周期测试。

### R5 · P2：产物构建发生在游戏退出后的 continuation 内，协调范围也过宽

证据：[NativeHistoryApplicationService.Merge.cs:36](D:/Programs/FolderRewind/FolderRewind/Services/NativeHistoryApplicationService.Merge.cs:36)、[HistoryMergeApplyService.cs:30](D:/Programs/FolderRewind/FolderRewind/History/Application/HistoryMergeApplyService.cs:30)。对应计划第 9.1 节。

历史输入虽然提前准备，但最终结果复制、压缩、hash、真实解包验证和 metadata capture 均在 coordinator 调用的 hostMutation 内发生。对活跃 Minecraft 世界，这意味着先退出世界，再等待完整的归档构建。同时传入所有 `config.SourceFolders`，包括最终 PreserveCurrent 的 Source；即使结果 roster 为空，也可能触发不必要的退出，或因多个无须修改的活动世界而阻断。

建议：先构建并持久化可验证 PreparedMerge，再根据实际 apply 与保护需求决定 coordination scope。若 Snapshot 必须覆盖更多 Source，应明确扩大保护 scope 的理由。对于没有 filesystem mutation 的纯历史提交，应走零目标路径；保留所有 expected-state 和提交验证。

### R6 · P2：provider 接口存在，但非默认版本的替换实现无法正常启用

证据：[HistoryMergePlanner.cs:18](D:/Programs/FolderRewind/FolderRewind/History/Application/HistoryMergePlanner.cs:18)、[HistoryMergeService.cs:19](D:/Programs/FolderRewind/FolderRewind/History/Application/HistoryMergeService.cs:19)、[HistoryMergeService.cs:56](D:/Programs/FolderRewind/FolderRewind/History/Application/HistoryMergeService.cs:56)。

Service 支持注入 `IHistoryMergeProvider`，但 planner 始终使用默认 `generic-file/1` 生成 plan。注入正常带独立版本号的 provider 后，Prepare 会因版本不匹配拒绝；伪装成默认版本则破坏固定 provider identity 的目的。这是静态可直接追踪的接口闭环缺口，不是要求提前实现 Minecraft 语义 provider。

建议：由 Host provider 选择结果明确给 planner 传入 identity/version/schema/policy，写入 plan 与 provenance；输入、授权路径和输出校验保持由 Host 管理。增加一个非默认版本的假 provider 测试，以及 provider 变化后旧 resolution 不可直接应用的测试。

### 其他建议

- **图查询成本**：[HistoryCheckpointGraph.cs:48](D:/Programs/FolderRewind/FolderRewind/History/Application/HistoryCheckpointGraph.cs:48) 对每个共同祖先重复遍历其全部祖先。长公共主链后分叉的图具有平方级遍历成本，Apply gate 内又会重算。建议用一次拓扑传播识别非 best common ancestors；建立上万 checkpoint 的确定性性能基准。本次没有测量实际耗时，不能据此给出卡顿秒数。
- **分页没有贯穿构建阶段**：[HistoryMergeCommitBuilder.cs:32](D:/Programs/FolderRewind/FolderRewind/History/Application/HistoryMergeCommitBuilder.cs:32) 的 `ToLookup` 会把分页迭代得到的所有冲突重新装入内存。UI 的 100 条分页确实存在；大世界仍应按 Source 流式构建、限制单个 conflict 描述规模，并测量峰值内存。
- **重算成果复用**：目前 Replan 新建 revision、Prepare 写入未解决冲突，没有按完整签名迁移已接受 resolution。安全上偏保守，但修复 Config binding 就可能要求重新选择大量冲突。可在 subject/input/provider/policy 全部相同时显式迁移，并保留旧 revision 直到新 roots 和产物发布成功。
- **错误状态与文案**：Workspace/tip 某些失败发生在更新 Stale 之前，UI 可能仍显示 Ready；conflict kind、mode 和多个异常直接显示英文枚举/原始错误。建议 typed diagnostic 与状态转换集中处理，并把 Merge 创建类型带到历史展示投影，避免未来按 CaptureScope 推断成备份。

## 测试质量与建议验收顺序

现有测试并非只有 DTO 或实现镜像：文件算法的数据驱动测试、两 Source 回滚、真实 ZIP round-trip、未开始目标不误删、pack durable 前后启动恢复、continuation 关闭/drain、MineRewind 第二个世界活跃等都提供有效保护。98 项定向测试通过具有价值。

不足在于不少成功路径使用永远 Ready 的 TreeHandler，catalog 甚至可以为空；这能测试合并逻辑，却掩盖 R1 的“历史事实存在但实际 payload 不可用”。现有 dirty 分支主要验证无 protector 时阻断，没有证明生产 SafetySnapshot 在真实配置下保护成功并继续合并。重启测试主要构造 journal 状态，没有覆盖生产封装在 committed 后抛异常的结果保持。

建议按以下顺序推进，每项都有明确退出条件：

1. 修复 R1–R3，并把三个探针精简为长期回归测试；增加提交后取消、Session DB 无法读取、恢复与 Capture barrier 用例。退出条件：不产生不可恢复 tip、不否认 durable commit、不在 pending recovery 状态读取 live tree 做 Capture。
2. 修复终态清理和最终 payload 生命周期；验证重复失败/重试不无限积累大文件，清理后仍能 materialize Exact merged Version。
3. 调整准备/协调顺序与零目标路径；验证空 roster、PreserveCurrent、缺 binding、第二个世界活动及多个活动世界的实际 scope。
4. 补非默认 provider 版本闭环、云端 PreparationRequired 的可操作提示、完整签名 resolution 迁移和大规模性能测试。
5. 在复制的测试目录与 Minecraft 世界上执行计划第 13 节六项人工验收，保留结果、日志、Snapshot 恢复证据及 Session 重启证据，再决定发布。

实施进度文档宜增加“代码已接通 / 定向自动测试 / 故障注入 / 实机验收”四列。当前可以准确称为“主要实现已接通，已有测试通过，仍存在已复现的发布阻塞问题”；不宜称为“仅剩人工验收”。

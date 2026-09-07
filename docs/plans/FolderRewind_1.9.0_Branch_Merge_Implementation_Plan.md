# FolderRewind 1.9.0 Branch Merge：AI Agent 实施计划

状态：设计已冻结，实施中。实际完成范围和未解决 blocker 见 [实施进度](FolderRewind_1.9.0_Branch_Merge_Implementation_Progress.md)。

本计划汇总维护者在本轮讨论中采纳的决策，以及授权 Agent 直接确定的工程默认项。实施时以仓库实际代码为准；本文件中的“新增”均为待实施能力，不代表当前代码已经存在。

## 1. 审查基线与首发范围

审查日期：2026-09-05。

| 仓库 | 分支 | 审查 HEAD |
| --- | --- | --- |
| Leafuke/FolderRewind | `1.9.x` | `c4c1da0a3d4668eb8ddac9e1399d144cab3721f3` |
| Leafuke/FolderRewind-Plugin-Minecraft | `1.9.x` | `baf5571324aee0e08f110e54bd8c89fe67693958` |

两个工作区在审查时干净，HEAD 与对应远端分支一致。这里只进行了静态代码及现有测试代码审查，没有执行测试。后续 Agent 开始编码前必须核对新 HEAD 和工作区修改，不得覆盖维护者的后续工作。

本文路径约定：`Host:` 相对于 FolderRewind 仓库；`MC:` 相对于 FolderRewind-Plugin-Minecraft 仓库。当前后者位于前者目录内，但是独立 Git 仓库。

### 1.9.0 必须交付

- ConfigurationCheckpoint 级 Branch Merge，target 为当前活动 Branch。
- No-op、fast-forward-like、唯一 best checkpoint base 的三方合并。
- Source roster 的三方处理，与当前 Config binding reconciliation 分离。
- 通用文件级保守合并、持久化本机冲突会话、人工选择及人工产物导入。
- staging 完成后一次 Host 配置级 Apply、Safety Snapshot、stale 检查、失败回滚和崩溃恢复。
- Minecraft 使用同一个 Host 文件级 provider；仍执行所需的环境协调。
- 可替换 provider 的 Host 契约，以及可关联多个路径的 conflict subject，为以后扩展保留空间。

### 明确不在首发范围

- Minecraft region/chunk/section/block、NBT、stats/advancements 的语义合并。
- `.mca` 内部解析、区块地图、2D/3D 浏览和预览。
- 自动文本 diff3、rename detection、recursive/virtual merge base、无共同祖先自动合并。
- 非活动 target 的“只提交 History，之后再 Checkout”入口。
- MergeSession 云同步、immutable MergeAttempt 历史、分布式锁。
- 面向尚未发布的 1.9.x 中间模型/API 的兼容层或迁移负担。
- 新测试工程。只使用已有 Host、Plugin Runtime/Abstractions 和 MineRewind 测试工程。

未来路线：先引入 2D 存档浏览，再接入 MineRewind 的区块级分析和 2D 冲突处理。1.9.0 不发布假实现或空的 Minecraft merge capability。

## 2. 当前代码事实与发布前 blocker

`docs/adr/0006-history-graph-foundation.md` 有部分阶段性描述已落后于代码，例如 Reconciliation 已实际存在。实施时更新该 ADR 的相关约束，不用旧文档推翻已审查的代码事实。

| 事项 | 当前事实 | 必须完成的工作 / blocker 范围 |
| --- | --- | --- |
| Branch ownership | `HistoryBranchProjection.FindLocalTips`、SQLite BranchTips 都只由同 Branch child 消费 tip；全局 ancestry 独立 | 保持该 invariant；新增跨 Branch Merge 后必须仍成立 |
| Source ancestry | `SourceVersion.ParentVersionIds` 已存在；writer 从可靠 Source baseline 写入；repository 验证同 Config、同 Source、无环 | 扩展多 parent 创建规则；不要借用 representation graph |
| 配置 ancestry | Checkpoint 没有 parents；Workspace 没有 checkpoint ancestry anchor | 新增配置级 semantic DAG 和本机 anchor，是 Merge Base blocker |
| Branch 创建 | 几条创建路径的 `ParentUpdateIds` 都为空 | 已知 BranchUpdate 来源时记录来源；独立 checkpoint 不制造虚假 BranchUpdate。Merge Base 不依赖修补后的控制 DAG |
| Reconciliation | 引用全部旧 tips，但只选 winner checkpoint | 保留当前语义；不能由 loser 的可达性推出其内容已合入 |
| Dirty protection | Checkout 主要凭持久化 baseline 的 Exact 标记判断保护需求 | 重新探测磁盘；不能漏掉完整备份之后的外部修改。worktree Merge blocker |
| Exact tip 准入 | Backup 可以在部分 Source 无 Version 时推进 Branch；从 checkpoint 建 Branch 只检查结构完整 | 统一完整性与 Exact closure 准入。Merge 输入/输出 blocker |
| 多 Source coordinator | Host 接收 affected folders，但只把第一个 Folder 交给插件 | 协调全部相关 Source；不能代表性地只协调第一个世界。配置级 Minecraft Merge blocker |
| 插件写回 | MineRewind continuation 返回后仍可能通过 NbtHelper 改写玩家 NBT | 移到 Host 管理的普通 Restore staging preparation；Merge/Checkout 禁止此后写回。Host mutation invariant blocker |
| boundary | Backup 解析插件 FilePolicy/选区后计算 boundary；Checkout binding 用原始 Config 计算 | 统一权威 boundary resolution，避免假 mismatch。插件 Merge/Checkout blocker |
| final revalidation | 当前 Checkout 使用传入 bindings 重新 plan，不等于重新读取权威 Config | gate 内重新校验 Config revision、绑定路径、resolved boundary 和 Workspace |
| 联合事务 | Backup writer 的 pack journal 与 Restore 的 filesystem/Workspace journal 分离 | Merge 必须有同一恢复协调者，不能简单 Restore 后再 Commit |
| 部分捕获 | 底层有 scope-aware patch；生产入口使用 `Determine(effectiveBoundary, effectiveBoundary)`，实际为该 boundary 内 FullSource | 不宣称 operation-scoped partial commit 已接通；保留底层 Exact 规则，不为本次 Merge 新增 partial capture UI |
| Overlay recovery | `HistoryArchiveRecoveryService.RecoverAsync(overlay: true)` 可创建 parentless PartialSource 和 Partial representation | 这不能证明完整 logical state，必须拒绝进入 Exact Merge。原生 recovery 若要创建完整 Version，需基于明确 Exact parent 和明确 overlay/patch 语义完成状态；否则只保留待处理 artifact，不制造 Exact Version。真实 Legacy 导入不得被自动升级为 Exact |
| Retention | 已有 Exact roots 和实际 representation closure 保护 | 接入持久 Session roots，保持 semantic ancestry 不保留祖先 payload |

相关现有文件：

- `Host:FolderRewind/History/Application/HistoryBranchProjection.cs`
- `Host:FolderRewind/History/Application/HistoryBranchService.cs`
- `Host:FolderRewind/History/Application/HistoryBranchReconciliationService.cs`
- `Host:FolderRewind/History/Application/HistoryCommitCoordinator.cs`
- `Host:FolderRewind/History/Application/HistoryCheckoutPlanner.cs`
- `Host:FolderRewind/Services/NativeHistoryRestoreOrchestrator.cs`
- `Host:FolderRewind/Services/NativeHistoryApplicationService.cs`
- `Host:FolderRewind/Services/Plugins/V3/PluginV3BackupSourceResolver.cs`
- `MC:MineRewind/V3/MinecraftSavesPlugin.cs`

## 3. 领域模型与不可混淆的关系

| 概念 | 定义与权威 |
| --- | --- |
| Config binding | 本机 SourceId 到路径、管理边界及配置的绑定；Host 管理，不由 Merge 自动修改 |
| SourceVersion | 一个 Source 在其 EffectiveSourceBoundary 内的完整 logical state |
| SourceVersion parents | 具体 Source 状态的 semantic 来源；同 Config、同 Source |
| ConfigurationCheckpoint | Source roster 与各 Source logical state 的不可变 vector |
| Checkpoint parents | 配置 semantic history 的延续/合并关系；Merge Base 在此图上计算 |
| BranchUpdate parents | 分支更新的因果与收敛关系；可以跨 Branch，但不改变节点 ownership |
| Workspace ancestry anchor | 下一次配置状态提交延续的 checkpoint；不承诺 working tree 等于该 checkpoint |
| Workspace Source baseline | 各 Source 的 BaseVersion 与 Exact/Derived/Unknown 关系；独立于配置 anchor |
| Representation dependency | 如何 materialize 状态，唯一可用于计算物理依赖闭包的关系之一 |
| MergePlan | 固定输入和 policy 的不可变分析结果；重算产生新 revision |
| MergeSession | 本机持久化的处理会话，保存计划 revisions、冲突、resolution、产物引用及 Apply 关联 |
| Conflict | 需要显式决定的配置、Source、文件或 provider 单元差异，不一定对应单个文件 |
| Resolution | 针对特定输入及 plan revision 的决定；不是脱离上下文的“这个路径选 ours” |
| SafetySnapshot | 独立 Exact 恢复 root，不创建隐藏 Branch、不推进 Branch |
| Merge provenance | Host 记录的角色、输入、base、provider/policy、结果摘要；不同于 descriptive plugin metadata |

### 3.1 Checkpoint 与 SourceVersion 的调整

在现有 `ConfigurationCheckpoint` 增加 `ParentCheckpointIds`，并用明确的创建类型区分 Capture、Merge、SafetySnapshot、Aggregate、Import 等来源。名称可以遵循仓库风格调整，含义不可合并成任意字符串。

- 普通配置提交最多一个 semantic parent；真实 Merge 有两个不同的 checkpoint parents。
- parent 必须存在、属于同一 Config、无重复且无环。
- Source roster 不能重复 SourceId；有 Version 的条目必须指向同 Config、同 Source 的版本。
- checkpoint boundary 必须与所引用 SourceVersion 的 boundary 一致；不能省略后默认成 All。
- roster 中没有 Source，与存在 Source 但 VersionId 为 null，是不同状态。
- 已知空 roster 是完整的空 checkpoint；不等于 unborn Branch。

复用 `SourceVersion.ParentVersionIds`，扩展验证器：

- Capture 仍为零或一个可靠 parent；PartialSource 必须有可靠的 Exact logical parent，即使产物已经重新打包成自包含表示也不能省略 semantic parent。
- 新生成的 Merge Version 使用 Ours/Theirs 中实际存在的 distinct SourceVersion parents，最多两个；同 Config、同 Source、无环。
- 新状态仅依赖一侧时允许一个 parent；首版不提供从双方都不存在的 Source 凭空创建手工 Merge Version 的入口。
- Source 状态可证明等于已有一侧时复用 Version，不改写 immutable parents。
- 需要区分 Merge 与 Capture 的创建类型；可将现有 `CaptureOutcome` 改为 `SourceVersionCreationKind`。CaptureScope 只描述捕获操作，Merge 不应在 UI 被误报为一次全量备份。
- Merge 不伪造 BackupRun；相关 `CreatedByRunId` 可以为 null。

新增明确的 Host Merge provenance，例如 `BranchMergeProvenance`，附着本次 target BranchUpdate：

- Merge mode：FastForwardLike / ThreeWay。
- target/source BranchId，Ours/Theirs UpdateId 与 checkpoint IDs。
- ThreeWay 的 BaseCheckpointId；fast-forward-like 不需要伪造三方 base。
- 所使用 provider/policy/schema 版本和 resolution 摘要、必要的产物逻辑摘要。
- 角色显式记录；不从规范化 parent 顺序推断 Ours/Theirs。
- 不包含本机路径、Session staging 路径、大型冲突集合或预览二进制。

### 3.2 Workspace anchor 规则

在 `HistoryWorkspace` 增加可空的 `CheckpointAncestryAnchorId`；同时更新序列化、相等性、CAS、journal、migration/import 初始化和 UI projection。

| 操作 | 规则 |
| --- | --- |
| 普通 Backup | parent 为当前 anchor；成功提交后 anchor 更新为新 checkpoint |
| Source Restore / subset restore / 普通 checkpoint restore | 保留配置 anchor；Source baselines 按实际 Restore 更新 |
| Branch Checkout | anchor 切换到目标 checkpoint；current-only Source 保留，不要求整个 Workspace vector 与 checkpoint 完全相同 |
| 从历史 checkpoint 创建 Branch | 复用用户明确选定的 checkpoint；激活时采用其 anchor |
| 从 current state 创建 Branch | 以可靠 anchor 延续真实状态；不能按 CreatedAt 从全库挑一个等内容 checkpoint 改变历史 |
| SafetySnapshot S | S 延续当前 anchor，更新可靠本机基线/anchor；Branch anchor 不前进 |
| ThreeWay Merge M | checkpoint parents 为 Session 固定的 O/T；Workspace anchor 更新为 M |
| FastForwardLike | 新 target Update 指向 T；Workspace anchor 更新为 T |
| Rename / Reconciliation | 不制造 checkpoint semantic ancestry；只有明确采用的 checkpoint/Checkout 能改变内容历史位置 |

例如 P2 上 Restore 旧 A1 后 Backup 得到 P3：P3 的 checkpoint parent 是 P2，新 A Version 的 parent 可以是 A1。前者表达历史延续，后者表达 Source 状态来源。Restore 导入旧内容不等于对其 donor checkpoint 做完整 Branch Merge。

已知 BranchUpdate 来源可记录跨 Branch parent；独立 checkpoint/未知导入不制造不存在的控制节点。未知来源必须显式呈现，不能按时间或表示依赖猜 ancestry。保留已发布 1.8.x 的真实 migration 路径，不新增对开发期 1.9.x 中间数据的兼容路径。

## 4. Merge 输入、Base 与结果分类

输入：同 Config、不同 Branch；target 是当前活动 Branch；双方都是非删除、非 unborn、具有完整 checkpoint 的唯一 local tip。Name 仅用于展示，身份始终用 ID。

1. 固定 Ours/target tip 和 Theirs/source tip，校验 checkpoint semantic graph。
2. T 是 O 的 semantic ancestor（含自身）则 No-op，不新增 History facts，也不修改 Workspace。
3. O 是 T 的 semantic ancestor 则 FastForwardLike。
4. 否则求全部共同祖先，移除仍是其他共同祖先之祖先的节点。
5. 剩一个则为 B；零个为 NoCommonBase；多个为 MultipleMergeBases。
6. 缺 parent、图损坏或不完整，不能当作无共同祖先。
7. 先用 metadata/Version identity 进行可证明的短路；真正需要读取的内容才要求 Exact materialization。
8. 云端可准备则返回 PreparationRequired；所需 Exact 状态不可用则阻断。不改选更老 base，不从 representation graph 找替代 base。

FastForwardLike：复用 T checkpoint，新增 target-owned BranchUpdate，parents 为固定 target/source tips，Reason 为 Merged。source local tip 仍保持原值。

ThreeWay：结果即使字节恰好等于 O 或 T，只要需要建立新的双方 ancestry，也产生新的 merged checkpoint；不因内容相等自动降为 No-op。未变 Source 可以全部复用。

Merged Update 必须校验 target/source 角色、两条 BranchUpdate parent、结果 checkpoint 与 provenance 一致。`Reconciled` 继续限定同 Branch parents，不允许拿它提交真正 Merge。

## 5. Source roster 与 boundary

| Base | Ours | Theirs | 结果 |
| --- | --- | --- | --- |
| A | A | A | 对相同 Source 做内容三方分析 |
| — | A | — | 采用 Ours 新增 Source |
| — | — | A | 采用 Theirs 新增 Source |
| — | A | A | 同 identity 独立新增：可证明完整状态等价则接受，否则 SourceAddAdd |
| A | — | A | Theirs 未变则删除；已变则 SourceDeleteModify |
| A | A | — | Ours 未变则删除；已变则 SourceModifyDelete |
| A | — | — | 移出结果 roster |

- Unknown/Unavailable/null Version 不是删除。Merge 不把失败捕获当作空状态。
- 参与同 Source 内容三方处理的 boundary 不一致时升级 SourceBoundaryConflict；不自动 union/intersection。允许显式选一侧完整 Source 状态，再做 binding 校验。
- SourceAddAdd/SourceDeleteModify/SourceBoundaryConflict 首版提供完整 Source 层的 ours/theirs 选择；不伪造空 base 进入细粒度自动合并。
- 相同显示名/PathHint 不合并 Source identity。不同 SourceId 可以同时保留，但目标路径重复或相互包含必须阻断 mapping。
- merged checkpoint 的每个 Source 都需要本机 binding 和对应 resolved boundary；缺失则 ConfigurationMappingRequired，边界不符则显式 reconciliation。
- 结果不存在但当前 Config 仍绑定的 Source 一律 PreserveCurrent，不自动解绑或删除物理目录。
- 当前 Config 新增而 B/O/T 都不知道的 Source 不加入 merged checkpoint，也 PreserveCurrent。
- 用户希望 Source 在后续备份中继续缺席，需要独立解除 Config binding，否则下次 Backup 可以重新加入。
- 已知空 merged roster 允许 History/Workspace 提交；受影响路径为空时走同一个事务协调者的零 filesystem mutation 情形，不为满足 coordinator 参数制造假 Source。

Config repair 是独立明确操作。它使旧 Apply plan 失效；之后显式重算/重新绑定计划，只有内容输入签名仍一致的 resolution 可复用，不能在旧 plan 上偷偷更换路径。

## 6. 通用文件合并与首发 Minecraft 行为

对管理边界内的规范化路径，文件值为 Absent 或受控内容引用。Absent 不等于零字节文件。

| 判定 | 自动结果 |
| --- | --- |
| Ours 等于 Theirs | 采用该结果，包括双方删除 |
| Ours 等于 Base | 采用 Theirs |
| Theirs 等于 Base | 采用 Ours |
| 其余情况 | ModifyModify / ModifyDelete / AddAdd 等冲突 |

实施要求：

- 内容相等用读取后的字节摘要/字节证据证明，不能用 Size + LastWriteTime 或 nullable StateFingerprint 作为成功证明。
- 使用带算法版本及域分隔的文件/树摘要。logical state 摘要与压缩 archive 的 storage SHA256 分开。
- 保留原始内容，不自动换行转换、JSON 合并、文本解码或二进制格式修改。
- 先处理路径重复、文件/目录前缀冲突、目标平台大小写冲突和不安全路径；不把这些冲突绕过为普通文件覆盖。
- 不越过 Source boundary；拒绝路径穿越和超出受控输入/输出根的产物。对现有 representation fidelity 契约无法可靠表达的特殊文件/链接明确阻断，不悄悄转换成普通文件。
- 首版不扩大既有 Exact fidelity 对 filesystem 元数据的承诺；目录结构和空目录等需按现有 capture/materialization 契约一致处理，不能由 Merge 单方面声称提供额外保真。
- 输入 materialization 按 Version/representation 去重、按需读取，避免强制为每个 Source 制造三份重复解包。

Minecraft 首发行为：`.mca`、NBT、JSON 等均作为普通文件。双方对同一 `.mca` 改出了不同字节，即使实际改的是不同区块，也产生文件冲突。双方改不同路径则按通用规则处理；结果描述为文件级 Merge，不声称经过世界语义或跨文件一致性验证。

### 6.1 Conflict 与 resolution 的最小模型

建议新增 Host 模型：

- `MergeConflictId`：在特定 PlanRevision 内稳定，不作为跨输入永久身份。
- `MergeConflictSubject`：SourceId、一个或多个相对路径、可选 ProviderUnitId。
- `MergeConflictKind`：配置/Source/文件结构与内容冲突的明确种类。
- `MergeValueRef`：存在性、受控内容 handle、内容摘要；Base/Ours/Theirs 分别记录。
- `MergeResolution`：PlanRevision、ConflictId、完整输入签名、Ours/Theirs/Manual/ProviderSpecific 以及结果引用。
- `VisualizationMetadata`：可选的 namespace、schema version、有限大小的描述数据/预览引用，不内嵌大 NBT、纹理或任意脚本。

通用 renderer 永远可显示 Source、相关路径、冲突类型与两侧选择。未来一个区块冲突可以涉及多个关联文件，不需要推翻“一个 conflict subject 可关联多个路径”的模型。Core 不硬编码 region、dimension、chunk 等字段。

Manual 首版支持把用户提供的文件复制到 Session-owned staging 后验证、记录摘要。外部文件以后再变动不影响已接受的 resolution。不能直接持有一个可变外部路径直到 Apply。批量 ours/theirs 是明确的批量 resolution，不是默认自动解决全部冲突。

### 6.2 Provider 扩展边界

首发实现 Host 内部的可替换 `IHistoryMergeProvider` 与 `GenericFileMergeProvider`。契约输入是固定的只读 B/O/T view、授权 Source/路径集合和 policy；输出为 proposal、conflicts、受控结果引用与描述信息。

- 不把任意 Host gateway、History writer、Workspace service 或 Branch mutator 交给 provider。
- Host 决定 provider 和处理单元，验证输出完整性、输入签名、路径范围及最终 Exact 产物。
- 首发只注册通用 provider，MineRewind 不注册专用 merge provider。
- 外部插件 capability 的完整公开 API 可在实际接入 2D provider 时版本化发布；1.9.0 不为尚不存在的 chunk renderer 冻结庞大接口。
- 未来 provider 可以认领多个相关路径作为一个处理单元；同一输入不能被通用和语义 provider 重复独立裁决。认领有歧义时阻断或要求明确 policy。
- provider/schema 改变后重算；不把旧 PluginSpecific resolution 偷偷交给新算法解释。

## 7. 本机 MergeSession、stale 与临时 roots

### 持久化

建议使用已有 Microsoft.Data.Sqlite 依赖，在 `local-state/merge-sessions/` 保存独立的 durable Session store，产物放在该目录下的 Session 专属子目录。它不是可随时丢弃的 HistoryIndex，也不是同步到 cloud 的事实库。

最少保存：SessionId、Config/repository identity、state/revision、各 immutable plan revision、expected state、冲突与 resolution、受控 artifact 清单及摘要、input roots、Apply transaction/intended pack identity。

状态：Preparing、Resolving、Ready、Stale、Applying、Committed、Abandoned；IO/provider 问题作为可重试状态及 diagnostic，不把暂时失败当作自动放弃。Applying 在重启后必须先查询联合事务恢复结果。

- 冲突分页/虚拟化；避免把整个世界的冲突集合装入一个 ViewModel list 或每次重写一个巨大 JSON。
- 保存 resolution 使用 revision/CAS；人工产物先完整写入并验证，再在一次 Session store transaction 内发布引用。
- 重启只恢复会话和可验证的处理进度，不自动 Apply。
- 显式 Recompute 创建新 PlanRevision，保留旧产物直到新 revision/roots 成功持久化。
- 完成/放弃后释放 roots；关闭 UI 不释放，清理不能丢弃未完成的人工工作。
- Session store 损坏时保留 artifacts，并在无法确定 roots 时阻止相关 GC；不能把损坏当作“没有活动操作”。

### Expected state

每个 plan 固定 target/source BranchId 与唯一 tip 集合、O/T/B IDs、authoritative Config revision/绑定/边界摘要、Workspace revision/active anchor/Source baselines、provider/policy/schema 版本。representation 选择可变化，但必须仍证明同一个 logical state 和所需 Exact fidelity；不因存在一个无关新 replica 或 annotation 就全量判 stale。

- 两边任何 tip 变化，包括 source 单独前进、出现 multi-tip 或 Rename 生成新 tip，原 plan 不直接 Apply。
- Config mapping、boundary 或 Workspace 逻辑状态变化需要显式重算/重新绑定。
- filesystem 的未提交变化不作为第四个 Merge 输入。它在 Apply 阶段被探测和保护，不要求用户解决冲突期间停止使用工作目录。
- Host 自己的 Safety Snapshot 会造成预期内 Workspace revision/anchor 变化；后续 expected 只接受该次明确转换，不接受其他 mutation。
- resolution 复用必须校验完整 subject/input/provider/policy 签名，不能只比较路径。

### Retention 接入

Session 创建和 roots 注册需与本机 mutation/retention 协调，消除先判断输入可用、后被 GC 删除的窗口。未完成 Session 对仍需读取的 Version/representation closure 建立 durable roots，并独立保护 Session/manual artifacts。

把 roots 接入自动 Retention、显式 local payload deletion 和 MaterializationPolicy release 检查；不能只在 Merge 页打开时传入 roots。完成/放弃与 roots 移交需要先保存 durable state 再允许清理。

此保护只约束本机管理的删除行为，不假装拥有远端分布式锁。远端副本消失可导致 preparation 失败；不会导致部分 Merge commit。

## 8. Merged payload、metadata 与完整性

新生成的 merged SourceVersion 首版使用 self-contained Exact representation，优先复用 `CoreFull` archive 构建/验证能力，不创造 Minecraft 专用归档格式，也不让两个 semantic parents 自动成为两个物理 dependencies。

- 从固定 merged staging 构建 payload，不对 live worktree 再发一次 Backup 来“得到 Merge Version”。
- 使用明确的 merged boundary/manifest，不能再次套用当前 Config 的不同过滤规则而悄悄裁剪结果。
- 复用已有 archive verification、RepresentationRuntime 和 catalog writer；封存 payload 并校验 storage hash、size 和 logical manifest。
- 新 metadata provider 入口复用只读 view/候选验证逻辑，但 view 指向最终 merged state，不是旧 backup session 或 live folder。
- 普通 metadata 提取异常/格式错误降级 warning；用户主动取消整次未提交操作仍按取消处理。
- 若状态等于某一侧则复用 Version 及其已有 metadata；这与给一个新 merged Version 复制旧 metadata 不同。
- 所有复用 Version 在最终提交时也必须具有可满足所需 fidelity 的表示闭包。
- Session staging 不是最终 representation locator；完成 Session 清理不能删掉 merged tip 的 payload。
- Session/manual payload、最终 representation payload、filesystem rollback 和 Safety Snapshot 是四种不同生命周期。

## 9. 单次 Host Apply 与联合事务

### 9.1 正常流程

1. 在 coordinator 之外完成 history 输入准备、冲突处理、merged staging、archive verification 和 metadata 提取。
2. 用户 Apply；确认 Session Ready、target 仍为当前活动 Branch，并提前检查必需 coordinator 可用。
3. Host 解析全部受影响 Source。协调器停止/退出需要释放的活动世界；若不能完整协调全部目标，阻断整个操作。
4. supplied continuation 最多进入一次。进入后探测当前 working state；Dirty/Derived/Unknown 时复用 Exact Safety Snapshot，保护成功后才允许目标 mutation。
5. Snapshot 必须覆盖其声称保护的当前配置状态；不能只对本次选区做 PartialSource 捕获后宣称保存了全部当前工作。捕获无法证明 Exact 则阻断。
6. 获取配置操作门和 Runtime mutation gate 的最终必要保护，读取权威 Config/Workspace/tips，重新验证 plan、产物和 target bindings。
7. 保存 durable 联合事务 intent，计划并逐项记录 rollback 进度；调用现有 filesystem backend 应用所有 Source。
8. 所有 filesystem apply 成功后，一次性发布 Merge pack：新 SourceVersions、representations、metadata、一个新 merged checkpoint（FF 无新 checkpoint）、一个 target BranchUpdate、必要 provenance。
9. 完成 Workspace 与 LocalReplicaCatalog，持久化已完成状态，再清理 rollback/临时产物并释放 gate。
10. coordinator cleanup/rejoin；只恢复确实需要恢复的先前运行环境。相关失败不否认已完成的 Host commit。

无 filesystem 变化的操作不需要虚假的游戏退出，但仍需对应的 expected 检查与必要 History/Workspace 提交。No-op 不创建 Snapshot 或新的 History facts。

### 9.2 锁与 revalidation

- gate 保持非 reentrant。现有 Backup/Restore/Checkout 公共 mutation API 在 coordinator 回调内继续禁止嵌套调用。
- continuation 只在本次 coordinator 生命周期内有效，结束后关闭，拒绝保存 delegate 后延迟调用。若 callback 提前返回但已启动 continuation，Host 必须观察其完成及提交结果，不能先报告 Blocked 再让 mutation 在后台继续。现有 one-shot gate 只有 invocation 标记，需补充关闭/完成状态。
- Host continuation 间接回调插件时，要重新建立该插件回调的权限边界，不能把内部 Host mutation 的许可通过 AsyncLocal 传给 consistency、metadata 或 staging provider，造成 nested mutation 绕过。
- Snapshot 是 Host continuation 内部授权的独立保护提交，不是插件发起 nested Backup。
- 不在已经持有同一个 gate 时调用会重新获取它的 Snapshot/History writer 公共方法。复用 inside-gate primitive，或在 Snapshot 返回并释放 gate 后获取最终 gate 并严格 revalidate。
- 各层固定锁顺序；配置操作门用于阻止本机 capture 与 filesystem apply 重叠，Runtime gate 保护 tips、History 和 Workspace。不能仅因最终 commit CAS 失败就允许 Backup 在变化中的目标目录上捕获混合状态。
- Config writer/reconciliation 必须与最终 Config 检查及 Apply 协调；检查 revision 后仍可并发改变实际路径的窗口必须关闭。
- 本机 History import/Sync publication 同样参与 Runtime gate。网络下载和长时间分析在 gate 外完成。
- Host gate 不是外部进程锁。Minecraft 使用环境协调；通用目录需要检查保护时的文件状态及 apply 前相关变化，无法可靠保护时阻断，不能宣称保证了任意外部 writer 的原子视图。

### 9.3 Durable commit 与恢复

Merge 的不可逆提交判据是预定 pack 已验证并持久化，而不是“某个 async 方法没有抛异常”。Apply transaction/intended pack ID 在执行前持久化，重试使用同一次 intent；避免崩溃重试产生第二个 Merge Update。

| 失败点 | 恢复规则 |
| --- | --- |
| staging / protection / final validation | 无目标 mutation、无 Branch advance；已完成 Snapshot 保留 |
| filesystem apply 中、pack 尚未持久化 | 按 journal 回滚所有已接触 Source；rollback 失败则 RecoveryRequired，并阻止后续 mutation |
| pack 持久化后、Workspace/catalog 尚未完成 | CommittedRecoveryRequired；按 durable intent 完成本地状态，不撤销 immutable facts |
| Workspace/catalog 已完成，cleanup/rejoin 失败 | CommittedWithPostActionWarning；清理可重试 |

复用/抽取现有 Restore filesystem executor 与 journal，增加可选 History pack commit intent。普通 Restore/Checkout 仍可以是 Workspace-only 操作；Merge 是 History-and-Workspace 操作。恢复协调者依据明确的 transaction mode 判断提交，不能让通用 History journal 先应用 Workspace，另一份 Restore journal 随后又回滚 filesystem。

建议让 Merge 调用 pack 发布 primitive，而不是完整调用现有 Backup `CommitAsync` 后再重复保存 Workspace。现有 `FileHistoryRepository.CommitAsync`、pack validation、LocalState intents、filesystem backend 均可复用；联合事务由一个所有者负责。

启动时必须先解决未完成联合事务，再允许相关 Config 的 Backup/Restore/Merge/GC。恢复流程不能等到用户再次点击 Restore 才触发。Source rollback progress 必须可判定“已计划 / 已开始 / 已备份 / 已应用”，避免对未接触的路径执行破坏性清理。

取消只在 durable commit 前导致回滚；提交后应完成必要本地状态或记录 RecoveryRequired。环境尚未安全恢复时不能仅显示 warning 后直接进入游戏。

## 10. MineRewind 首发改动边界

### 仍然必须修改

- coordinator request 明确 operation kind（Restore / Checkout / Merge）、operation identity、全部相关 Source，以及可表达 Committed/RecoveryRequired 的 continuation result。
- Host 构造配置级 coordination scope。MineRewind 对所有需协调的目标执行可证明的准备；无法路由/释放多个活动世界时整次阻断，不逐 Source 调用 Host mutation。
- continuation 恰好最多一次；plugin Query/logging/notification 允许，公共 nested mutation 仍拒绝。
- Quick Restore 保持 Host semantic intent，不恢复插件自行排序版本的逻辑。
- post-action warning 与 Host commit 结果分离；RecoveryRequired 时不擅自 rejoin。

### 保留玩家数据的已有 Restore 功能

修复现有 continuation 后 `NbtHelper.ApplyPlayerData(livePath, ...)`，不是新增 Minecraft semantic Merge。

- 保留普通 Restore 的功能，将现有 NBT 读写逻辑迁移到 Host 调用的受控 Restore staging preparation 契约。
- 输入为稳定的当前工作只读 view 与目标只读 view；输出为受限的 staged 文件 proposal。插件不能改 live path。
- Host 将 proposal 纳入同一次 Restore filesystem apply；若结果不同于选定 Version，则 Workspace 为 Derived，不能声称 Exact，并正确失效/更新 capture cache。
- Branch Checkout / Merge 不调用这种改变已选 Exact 内容的 Restore preparation；已有 PreservePlayerData 设置限定为普通 Restore，UI 明确其作用范围。
- per-request preserve intent 使用 operation identity / 明确选项传递，不通过全局 VersionId 字典串扰同时进行的操作。
- 不增加 region parser、chunk policy、语义 JSON union 或世界内容修复。

未来 2D provider 需要考虑跨文件单元和格式版本，但其具体算法、原子单元、renderer 都留到后续设计，不进入 1.9.0 验收条件。

## 11. 文件与模块级任务地图

以下新增文件名是建议落点，可在同一职责范围内适度合并。不要把 Merge 塞进已有 Backup/Restore 的万能公开 API，也不要复制 filesystem writer。

| 模块 | 现有文件 | 新增/调整职责 |
| --- | --- | --- |
| Domain | `Host:FolderRewind/History/Domain/ConfigurationCheckpoint.cs`、`SourceVersion.cs`、`BranchUpdate.cs`、`HistoryDomainValidator.cs`、`HistoryIds.cs` | checkpoint parents/creation kind，Merged 规则，typed provenance，所需 IDs |
| Storage/index | `Host:FolderRewind/History/Storage/HistoryRepositoryValidator.cs`、`HistoryPackCodec.cs`、`HistoryCommitPack.cs`、`HistoryRepositoryDescriptor.cs`；`Index/HistoryIndex.cs` | 验证/编码新 facts，checkpoint parent edges，索引重建；格式直接调整，不做 1.9 开发期兼容 |
| Source/config ancestry | `Host:FolderRewind/History/LocalState/HistoryWorkspace.cs`、`HistoryLocalStateStores.cs`、`HistoryLocalStateJournalRecovery.cs`；`Application/HistoryCommitCoordinator.cs`、`HistoryBranchService.cs`、`HistoryRestoreService.cs`、`HistoryCheckoutService.cs`、`HistoryArchiveRecoveryService.cs` | anchor 在所有创建、提交、恢复路径中一致维护；修复按内容任意复用 checkpoint，拒绝把无 Exact logical parent 的 overlay artifact 当完整 Version |
| 图查询/准入 | `Host:FolderRewind/History/Application/HistoryQueryService.cs`、`HistoryBranchProjection.cs`、`HistoryBranchReconciliationService.cs` | 新增 `HistoryCheckpointGraph.cs`、`HistoryExactCheckpointAdmission.cs`；metadata BCA、完整/Exact 准入、保持 control DAG 语义 |
| boundary/protection | `Host:FolderRewind/Services/Plugins/V3/PluginV3BackupSourceResolver.cs`、`NativeHistoryApplicationService.cs`、`HistorySourceBindingRepairService.cs`、`BackupService.CaptureBaseline.cs`、`BackupService.Transaction.cs` | 抽取 `HistorySourceBoundaryResolver` / working-state probe；复用现有 Snapshot，补 gate 与磁盘保护 |
| Merge domain planning | 新增 `Host:FolderRewind/History/Application/HistoryMergeModels.cs`、`HistoryMergePlanner.cs`、`HistoryMergeService.cs` | intent、状态分类、roster、Source 策略、expected state；不操作 UI、不直接遍历 live tree 合并 |
| 文件算法 | 新增 `Host:FolderRewind/History/Merge/IHistoryMergeProvider.cs`、`GenericFileMergeProvider.cs`、`MergeTreeManifest.cs` | 纯三方文件逻辑、结构冲突、内容签名、受控 view/proposal |
| Session | 新增 `Host:FolderRewind/History/LocalState/MergeSessionStore.cs`、`MergeSessionModels.cs`；`Storage/HistoryRepositoryPaths.cs` | durable Session/CAS/分页/产物目录/plan revisions/Apply identity |
| staging/结果构建 | 新增 `Host:FolderRewind/History/Application/HistoryMergePreparationService.cs`、`HistoryMergeCommitBuilder.cs`；复用 `Representation/RepresentationRuntime.cs`、`Capture/VerifiedArchiveCaptureFactory.cs`、archive backend | 按需准备、固定结果树、全量 Exact archive、metadata、单 pack 候选；不提前发布 History |
| 联合事务 | `Host:FolderRewind/History/Application/HistoryRestoreService.cs`、`HistoryRestoreTransactionJournal.cs`、`FileSystemHistoryRestoreMutationBackend.cs`、`HistoryCommandCommitter.cs`；`Storage/HistoryTransactionJournal.cs` | 抽取 `HistoryWorkspaceMutationExecutor` / recovery coordinator；一个 FS path、可选 pack commit、统一恢复 |
| Runtime/startup | `Host:FolderRewind/History/Application/HistoryRuntime.cs`、`HistoryRuntimeManager.cs`、`NativeHistoryCoreGateway.cs`；`Services/NativeHistoryConfigurationOperationGate.cs`、`NativeHostMutationContext.cs` | 恢复前禁止 mutation/GC；固定锁顺序和内部 continuation 权限 |
| Host 编排 | `Host:FolderRewind/Services/NativeHistoryRestoreOrchestrator.cs`、`NativeHistoryApplicationService.cs`；新增 `NativeHistoryMergeApplicationService.cs` | 复用协调边界，保持 Restore/Checkout/Merge 三种 semantic intent；prepared state 进入一次 continuation |
| 插件契约/runtime | `Host:FolderRewind.Plugin.Abstractions/Capabilities.cs`、`PluginApi.cs`、`Manifest.cs`、`Identifiers.cs`；`FolderRewind.Plugin.Runtime/Operations/RestoreMutationContinuationGate.cs`、`Activation/CapabilityRegistry.cs`、`PluginManifestContractValidator.cs`；Host `Services/Plugins/V3/PluginV3HostServices.cs` | batch coordinator/result、普通 Restore staging preparation、版本/manifest 注册验证；首发无需公开 Minecraft merge capability |
| metadata | `Host:FolderRewind/Services/Plugins/V3/PluginV3BackupSession.cs`；`Domain/VersionMetadataSnapshot.cs` | 抽取可供最终 merged staged state 使用的 metadata capture/validation helper，避免复制实现 |
| retention/deletion | `Host:FolderRewind/History/Retention/HistoryRetentionModels.cs`、`HistoryRetentionPlanner.cs`、`HistoryRetentionExecutor.cs`；`Application/MaterializationPolicyService.cs`、`HistoryLocalReplicaMaintenanceService.cs` | durable Session roots 纳入所有相关本机释放/清理入口；生命周期转移 |
| UI | `Host:FolderRewind/ViewModels/HistoryPageViewModel.cs`、`HistoryPageViewModel.Commands.cs`；`Views/HistoryPage.xaml`；`Services/HistoryInteractionContracts.cs`、`HistoryInteractionService.cs` | 配置级 Merge 入口；新增小型 `HistoryMergeViewModel` / `HistoryMergePage` 或同职责控件，分页冲突、恢复Session、重算、手工导入、Apply 结果 |
| localization | `Host:FolderRewind/Strings/zh-CN/Resources.resw`、`Host:FolderRewind/Strings/en-US/Resources.resw` | 对应 semantic 状态、Source/文件冲突、快进/No-op、恢复与 warning 文案 |
| MineRewind | `MC:MineRewind/V3/MinecraftSavesPlugin.cs`、`MinecraftSavesPlugin.KnotLink.cs`、`MinecraftSavesPlugin.Capabilities.cs`、`NbtHelper.cs`、`manifest.json` | 批量协调、staging-only 的原有玩家保留、结果语义、API/manifest 对齐；不实现细粒度合并 |
| 文档 | `Host:docs/adr/0006-history-graph-foundation.md`、`docs/plugin-v3/README.md`、`docs/plugin-v3/MANUAL_TEST_M5.md` | 对齐真实新模型、操作边界与首发范围；必要时新增 Merge ADR 并引用本计划 |

新增 `History/Merge` 目录后需要更新已有 `FolderRewind.Tests.csproj` 的 source link；当前 Application 文件也是显式 Include，不能假设新文件会自动进入测试工程。不要借此全量重组 csproj。

## 12. 实施顺序与 Git commit 拆分

先修基础，再让 Merge 入口可用。各步保持可编译；跨仓库插件契约变化通过对应的 Host/MC commits 配对验证，不要求独立仓库形成一个 Git 原子提交。不在本计划阶段实际创建 commit。

| 顺序 | 仓库 / 建议 commit | 完成条件 |
| --- | --- | --- |
| 1 | Host `feat(history): 增加配置语义祖先与工作区历史锚点` | domain/index/validator/anchor 写入与 round-trip 完整；Restore/Checkout 区分成立 |
| 2 | Host `fix(history): 统一精确检查点准入与来源边界解析` | 普通 Branch 不再推进到不完整/不可满足 Exact 的 checkpoint；插件 boundary 同源解析 |
| 3 | Host `fix(history): 在破坏性切换前可靠保护当前工作` | Dirty/Derived/Unknown 正确保护；Snapshot 不推进 Branch；gate 与 Config/Workspace revalidation 闭合 |
| 4 | Host `refactor(plugins): 支持配置级还原协调与受控准备产物` | batch coordinator、one-shot/result、Restore staging preparation 契约及 runtime 注册完备 |
| 5 | MC `fix(restore): 将玩家数据保留移入宿主管理的准备阶段` | 全部目标协调；不再 continuation 后修改 live NBT；Merge/Checkout 不启用该 Restore 修改策略 |
| 6 | Host `feat(history): 增加三方分支合并计划` | No-op/FF/BCA、roster/boundary、expected state 和多 parent provenance 规则可测试 |
| 7 | Host `feat(merge): 实现保守文件级三方合并` | 文件/结构冲突与 provider 契约，首发无 Minecraft 特判 |
| 8 | Host `feat(merge): 持久化冲突会话与恢复处理进度` | Session/revision/Manual artifacts/重启恢复；分页查询；durable roots 同步接入后才允许长期保存会话 |
| 9 | Host `feat(history): 为合并会话保护精确输入闭包` | Session roots 贯穿 retention、release、显式删除；完成/放弃的安全清理。与步骤 8 作为紧邻依赖交付 |
| 10 | Host `feat(merge): 构建精确合并产物与版本元数据` | staging 转 self-contained Exact 表示；复用 Version；最终 state metadata；尚无提前 Branch advance |
| 11 | Host `refactor(history): 统一工作区变更与历史提交恢复` | 共用 filesystem executor；pack 前回滚、pack 后完成；启动恢复先于其他 mutation/GC |
| 12 | Host `feat(history): 原子应用分支合并结果` | 完整 coordinator → Snapshot → gate/revalidate → FS → pack → Workspace 路径；安全重试不重复 Merge |
| 13 | Host `feat(ui): 增加分支合并与文件冲突处理界面` | 当前 target、source 选择、Session 恢复/重算、冲突批量选择/人工导入、正确状态呈现 |
| 14 | Host + MC 各自 `docs(merge): 明确首发文件级合并范围与扩展边界` | API/ADR/手工验收文档一致；MineRewind 未注册精细 merge provider |

Exact tip 准入修复不得丢弃已验证的可用 Backup 产物来掩盖失败。需要保留部分 capture facts 时，明确报告配置 checkpoint 未满足 Branch advance 条件，并保持 Branch 不动；不要把该 Backup 的降级结果套用到 Merge，Merge 始终全配置一次提交。

## 13. 最小必要测试与验证

不新建测试工程。不对简单 DTO getter 或 UI 文案建立镜像测试。优先修改/扩展现有测试；只有纯 Merge 算法、Session 和联合事务缺少承载位置时，在已有 Host 测试工程新增测试文件。

| 测试组 | 最小有意义覆盖 | 建议位置 |
| --- | --- | --- |
| 图与身份 | checkpoint/Source 多 parent 校验；跨 Config/Source、缺 parent、cycle；Merge child 不消费 source local tip | 现有 HistoryDomainTests、HistoryIndexAndLocalStateTests、HistoryRepositoryTests |
| ancestry 写入 | Restore 旧 Source 后 Backup 延续旧配置 anchor；Checkout 切 anchor；Snapshot 不推进 Branch；等内容不跳换 ancestry | 现有 HistoryCommitCoordinatorTests、HistoryBranchAndAnnotationTests、HistoryCheckoutServiceTests |
| Base/分类 | No-op、FF、唯一 best、criss-cross 多 base、无 base、损坏引用；所需 base payload 缺失不降级 | 新增 HistoryMergePlannerTests（已有工程内） |
| roster/boundary | 用 data-driven cases 覆盖第 5 节矩阵；null 不等于 absent；current-only preserve；空结果；缺 binding；同源插件 boundary | MergePlannerTests + 现有 EffectiveSourceBoundaryTests/CheckoutServiceTests |
| 文件算法 | 数据驱动覆盖单侧增改删、双方同结果、不同 add/add、modify/delete、零字节与 absent；一个 prefix/type 和大小写冲突 | 新增 GenericFileMergeProviderTests |
| Session/roots | 保存人工 resolution 后重开；输入/provider 变化不得误复用；GC 保留依赖链；放弃后允许释放；Session 损坏不误删 | 新增 MergeSessionTests + 现有 HistoryRetentionSafetyTests |
| concurrency | 用 barrier 注入 source 或 target tip 变化、Config/binding 变化、Workspace 变化，验证 gate 内拒绝且零新 Merge facts；Snapshot 自身 revision 转换允许 | MergePlanner/Apply 测试，避免 sleep-based 时序测试 |
| 联合事务 | 故障注入：第二个 Source apply 失败、pack 发布前失败、pack 已落盘但返回异常、Workspace/catalog 完成失败、重启恢复、重试不产生第二个 Update | 扩展现有 Restore transaction 测试或新增 HistoryMergeApplyTests |
| plugin/结果 | 两个目标中第二个活动；continuation 两次/生命周期结束后调用拒绝；已开始 continuation 被 Host 观察；内部插件回调不能继承 Host mutation 许可；post rejoin warning 保留 commit；RecoveryRequired 不 rejoin | 现有 Runtime/Abstractions 测试和 MC:MineRewind.Tests/V3VerticalSliceTests |
| staging/metadata | 新产物至少一次真实 materialization 比对 expected tree；metadata 来自 merged staging、普通 provider 异常不阻断；玩家保留仅在普通 Restore 产生 Derived | 现有 VersionMetadataSnapshotTests、FileSystemHistoryRestoreMutationBackendTests、MC V3VerticalSliceTests |

代码变化相关的定向测试通过后，只运行一次必要的集成构建/受影响已有测试工程；无新失败或新改动不反复扩大测试范围。测试新增的注释仅放在 DAG、concurrency、commit/recovery 等非显然 invariant，使用简洁中文。

最小手工验收：

1. 通用配置两个 Source：双方修改不同文件自动合并；同文件冲突选择 ours/theirs；source tip 保持原位。
2. 关闭并重启应用继续处理冲突；在另一次操作推进 source 后，旧 Session Apply 被拒绝。
3. 当前目录 Dirty 时 Apply，核对 Safety Snapshot 可恢复该未提交工作；最终状态等于 merged checkpoint。
4. 历史 Source 缺 binding 时无目标写入；修复并显式重算后可 Apply；current-only Source 文件不丢失。
5. 使用复制的测试 Minecraft 世界：同 `.mca` 双改显示文件冲突，绝不出现“区块已自动合并”提示；活动世界走协调流程。
6. 普通 Restore 的玩家保留仍可用且 Workspace 为 Derived；Merge/Checkout 无 post-commit NBT 写回。

不要以维护者真实运行中的世界作为故障注入或破坏性手工测试数据。

## 14. 实施验收 invariant

- Branch ownership != global Branch ancestry；Branch causality != checkpoint semantic ancestry；两种 semantic ancestry != representation dependencies。
- Merge ≠ Restore ≠ Checkout ≠ Reconciliation；共用 primitive，不混合公开领域操作。
- 所有 Merge 输入角色和 resolutions 对应同一个明确 plan revision。
- 新 merged Version、checkpoint 和 target Update 一次发布；source Branch 不前进、不被消费。
- 冲突未解决、stale、保护失败、pack 前取消/失败都不产生 target Branch advance。
- pack 已 durable 的操作不得被报告为未发生；恢复不撤销 immutable facts。
- 插件永不在 Host continuation 返回后修改 managed worktree；只有 Host executor 应用文件。
- Snapshot 独立持久存在；Session 清理、target 失败和 rejoin 失败都不能删除它。
- 所有正常 target tips 满足结构完整与所需 Exact closure；current-only PreserveCurrent 不被伪装成 merged roster 的一部分。
- 所有本机 GC/release 入口尊重未完成 Session roots；semantic ancestor payload 不被无限保留。
- 首发没有 Minecraft 精细合并实现；Core 的 conflict/provider 模型能够在后续承载多路径的 2D 区块单元。

本文件是实施输入，不是实施结果。当前已发现 blocker 在相应代码与最小验证完成前，均保持未解决状态。

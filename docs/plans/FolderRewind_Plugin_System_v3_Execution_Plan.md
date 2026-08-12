# FolderRewind Plugin System v3 — 1.9.0 冻结与执行计划

> 状态：D0 / M3R 已冻结 / M3 实施中
>
> 计划版本：2026-08-12 / Revision 10
>
> 产品版本：FolderRewind 1.9.0、MineRewind 1.9.0
>
> Plugin API：3.0.0（Revision 9 candidate 已撤回，Revision 10 contract 正在 M3 重新实现与冻结）
>
> App Config Schema：1
>
> 目标仓库：`Leafuke/FolderRewind`、`Leafuke/FolderRewind-Plugin-Minecraft`、`Leafuke/FolderRewind-Site`、新建 `Leafuke/FolderRewind-Plugin-Catalog`
>
> 当前执行门：M3 实施；用户已授权完成后自动通过 M3 Gate、继续 M4 并自动通过 M4 Gate、继续 M5，最终停在 M5 Gate 等待人工测试与审阅。

本文件是 Plugin System v3 的唯一执行依据。它先作为受版本控制的 proposed specification 接受审阅；用户明确通过 D0 后，才可把状态改为“已冻结 / 实施中”并修改产品代码。实施中若发现本计划无法满足仓库事实，必须先修订本文件、说明影响并重新通过当前里程碑，禁止在代码中静默偏离。

---

## 0. 执行治理与授权边界

### 0.1 执行原则

1. **Host owns state and execution; plugins own semantics.** 插件不得获得 `ConfigService`、`BackupService`、ViewModel、WinUI 控件或其他 Host 内部可变对象的编译期访问。
2. **Discovery proposes; Reconciliation proposes; the user owns configuration.** Discovery 只返回新配置候选/草稿；Config Reconciliation 只返回针对现有 revision 的 Change Proposal。`AutoCreateConfigs`/受限 AutoApply 都是用户明确开启后由 Host 执行的校验与原子提交策略，不是插件直接持久化配置的权限。
3. **Clean break runtime, lossless known-data migration.** 1.9.0 只运行 v3 API，不提供 v2 runtime compatibility layer；MineRewind 的已知数据和 enabled intent 必须迁移，未知 v2 代码包只隔离和保留，不执行、不删除。
4. **MineRewind parity and the third-party Artifact slice are v2 removal gates.** Discovery、自动补全、文件策略、世界详情、区域备份、热备份、热还原、玩家数据、命令/热键和 KnotLink，以及独立 transformer/materializer plugin 的安全 graph/restore E2E 未全绿前，不得删除 v2。
5. **每个提交保持可构建、可测试、可审阅。** 两仓库短期可以在开发分支保留编译期过渡 adapter，但发布态不得存在 v2 compatibility path。
6. **不借 v3 重写完整备份引擎，但 Artifact 必须是一等扩展边界。** Core 继续拥有 capture、history、retention、Cloud、encryption、integrity 和 operation lifecycle；插件可以在 Host-controlled staging/transaction 中转换 Artifact，并为其格式提供 Restore Materializer，但不能通过自由 Hook 原地改写已提交归档或完全接管 Backup/Restore Engine。
7. **破坏性动作先建恢复点。** 配置迁移、设置切换、插件更新、Provider State migration、版本指针切换必须有明确 prepare/commit/recovery 语义。
8. **用户可见行为本地化。** 状态、诊断、降级、阻止、信任、包错误、Preset 和 Recovery Center 同步更新中英文资源。

### 0.2 审阅门与外部发布

| 门 | 通过条件 | 通过后允许进入 |
|---|---|---|
| D0 文档冻结 | 本文件、`CONTEXT.md`、ADR 0002–0004 经用户批准 | M1 产品代码实施 |
| M1 安全基础 | migration/recovery/test seam 全绿 | M2 runtime 与持久化 |
| M2 Runtime Core | Abstractions/runtime/config model 集成全绿 | M3 垂直切片 |
| M3R 文档重冻结 | Revision 10、`CONTEXT.md`、ADR 0003 补充与 proposed ADR 0005 经用户批准 | 恢复 M3 产品代码实施 |
| M3 API Freeze | 原四条 E2E + config proposal + fake reverse-delta Artifact/Restore E2E 全绿 | 重新冻结并准备 API 3.0.0 |
| M4 Parity | Host regression + MineRewind parity + third-party Artifact integration 全绿 | 包、更新、Catalog、Preset |
| M5 Distribution | 安装/更新/离线迁移/Catalog/站点全绿 | 删除 v2 |
| M6 Release Candidate | 四仓库 DoD 全部满足 | 单独申请发布授权 |

每个门完成后必须提交：变更摘要、测试证据、已知风险、下一阶段计划。用户于 2026-08-12 批准 M3R，并特别授权本轮在证据全绿后自动批准 M3 与 M4、连续实施至 M5 Gate；M5 Gate 必须暂停，交由用户进行人工测试和审阅。此连续授权只覆盖本地修改、测试、仓库初始化和里程碑内提交；push、PR、NuGet.org、GitHub Pages、远程 Catalog 创建/合并和正式 release 均需另行批准。

### 0.3 基线与工作区约束

- Host 与 MineRewind 当前分支均为 `1.9.x`，Site 为 `main`。
- 文档冻结前基线：Host 259 tests、MineRewind 28 tests 全绿；Host/MineRewind x64 Debug 串行 build 均为 0 warning / 0 error。
- 当前 MineRewind `ProjectReference` 会让并行构建争用 Host WinUI `obj`；M2/M3 必须以独立 Abstractions 引用消除此耦合。
- 开始每个里程碑前检查四仓库 dirty state；不覆盖用户已有修改。

---

## 1. 已冻结的领域与架构模型

### 1.1 四类身份

禁止用泛化 `ProviderId` 同时承担以下角色：

| 身份 | 语义 | 持久化示例 |
|---|---|---|
| `PluginId` | 插件产品永久身份 | `com.folderrewind.minerewind` |
| `ConfigKindRef` | 配置领域语义 owner + local kind | `com.folderrewind.minerewind/minecraft-saves` |
| `DiscoveryProviderId` | Discovery candidate 来源 | 可与 MineRewind PluginId 使用相同文本，但类型独立 |
| `StateOwnerId` | Config/Folder opaque state namespace owner | 可与 MineRewind PluginId 使用相同文本，但类型独立 |

Core default Kind 固定为 `folderrewind.core/default`。Config Kind、Config ID、Folder ID、Discovery Set Identity、Scope ID、Command ID 都是稳定程序身份，显示名称永远不能参与路由。MineRewind 局部 ID 固定为 `minecraft-saves`、`selected-regions`、`hotbackup.active-world`、`hotrestore.active-world`。

Artifact 扩展另使用严格角色身份，不能复用显示名、文件扩展名或 PluginId 代替：

| 身份 | 语义 | 规则 |
|---|---|---|
| `ArtifactId` | Host 为一个不可变 Artifact 分配的稳定 GUID | 文件移动、History 重命名、Cloud 同步不得改变 |
| `ArtifactFormatRef` | `OwnerId + FormatId`，声明 payload 的可解释格式 | Core 与插件格式共享同一类型；格式 owner 缺失时 fail-closed |
| `ArtifactTransformerId` | `PluginId + TransformerId`，声明一次可选择的转换策略 | Config 显式选择，不按 claim 顺序扫描 |
| `RestoreStrategyId` | `PluginId + StrategyId`，声明一个 Artifact materialization 策略 | Manifest 静态绑定支持的 ArtifactFormatRef/version |

除 `folderrewind.core/*` 外，ArtifactFormatRef.OwnerId 文本必须等于声明它的 PluginId，Runtime 使用不同 CLR 类型保持职责区分；第三方不能 claim 或覆盖另一插件的 format。Dependency 可以跨 format/owner（例如 plugin delta 依赖 Core Full），但不得跨 ConfigId/FolderId。

Core Artifact format 固定为 `folderrewind.core/archive-set` version 1，Core Restore Strategy 为 `folderrewind.core/archive-materializer`；Full/Smart/Overwrite 是独立 capture-mode fact，不用文件名前缀或扩展名冒充 format identity。

### 1.2 Capability 与生命周期

- `manifest.json` 是静态 Plugin identity、version、entry、API requirement、Config Kind metadata、settings schema 路径、requested Host Services 的唯一真相；运行时代码不得重复 Manifest。
- Plugin entry 只负责 `ActivateAsync` / `DeactivateAsync`。一个实例只 Activate 一次；settings 变化、新版本候选和恢复旧版本都创建新实例。
- Activation 使用临时 session：注册 capability、校验冲突、执行 staged Provider State migration、收集 Host-owned patch；全部成功后一次 commit，失败 rollback 并 best-effort Deactivate。
- Activation 期间 DataStore 不可用；commit 后才向 Runtime Session 开放 DataStore。
- 每个插件对每种 capability contract 最多注册一个实现，内部多组件由插件自行组合。
- 正式 runtime states：`Inactive`、`Activating`、`Active`、`Draining`、`Deactivating`、`Failed`；Installed、Enabled Intent、Runtime State 三轴分离。
- 单次 capability invocation 异常只影响该 operation；只有 activation/deactivation/contract fatal error 才让 runtime 进入 `Failed`。
- Safe Mode 只通过 `--safe-mode` 或“以安全模式重启”手动进入，不执行插件 DLL，不修改 Enabled Intent，仍允许 Recovery/Config/Plugin 管理 UI。
- Disable/Update/Settings Change 进入 Draining：拒绝新 lease，等待现有 lease 自然结束，绝不强制撕掉数据 operation。用户可取消变更；无法卸载时记录为重启后应用。
- 不提供可自由改写 Host Artifact 的通用 before/after Hook。备份前领域操作通过 Consistency Lease；capture 后 cleanup 必须在 `finally` 释放 lease；durable commit 后仅允许收到 immutable completion snapshot 的 Observer，Observer 不属于 Artifact transaction，也不能改变已提交结果。
- Artifact Transformer 和 Restore Materializer 都必须持有同一个 Runtime Session operation lease；update/disable 不得在 graph transaction 或 materialization 中途卸载插件。

### 1.3 公共 Capability 与 Host Services

Plugin API 3.0 至少提供：

- Discovery、Config Reconciliation + revision-bound Config Change Proposal；
- mandatory File Policy、Provider-owned Backup Scope；
- Backup Consistency Lease、Backup Completion Observer、Folder Metadata；
- Backup Artifact Transformer、Restore Materializer、Restore Coordinator；
- Plugin Command、KnotLink Integration；
- Provider State Migration。

Host Services 至少提供：只读 config query、backup request、restore request、history query、notification、KnotLink、Plugin DataStore、temporary storage、`ArtifactRead`、`ArtifactTransformStaging`、`RestoreMaterializationWorkspace`、progress 和 logging。Manifest 静态声明 requested services；Host 在执行代码前检查可用性，在安装/更新 UI 展示变化，但这不是 OS 权限或安全沙箱。ArtifactRead 可能暴露 Host 解开 envelope 后的备份内容，必须作为高影响 disclosure 单独突出。Official、Community、Manual 使用同一 Plugin API，没有 Core 私有能力。

### 1.4 Operation Resolution 与结果模型

- Kind-scoped capability 只由 `ConfigKindRef.OwnerId` 精确路由，不扫描所有插件 claim owner；v3.0 不支持隐式 cross-plugin middleware。
- Artifact Transformer 是显式组合例外：其 PluginId 可以不同于 Config Kind owner，但必须由用户在该 Config 的 ArtifactTransformPolicy 中按稳定 ID 选择，并由 Manifest 静态声明 compatible ConfigKinds。Host 不自动扫描/试用所有 transformer，也不允许 transformer claim Config Kind ownership。
- 纯 capability plugin 可以声明零个 ConfigKinds；例如 MineDelta 可只提供 transformer/format/materializer/reconciliation，而继续复用 MineRewind-owned Minecraft Config Kind、Discovery、FilePolicy、Scope、Consistency 和 Coordinator。
- 每个 plugin-related operation 获取 Runtime Session Lease；operation cancellation token 与 plugin lifetime token 分离。
- Resolution 输出 `Ready`、`Degraded`、`Blocked` 和结构化 diagnostics；diagnostic 至少包含稳定 code、severity、owner/capability、localized arguments。
- Operation outcome 统一为 `Success`、`SuccessWithWarnings`、`NoChanges`、`Canceled`、`Failed`、`Blocked`。
- 插件 cleanup/finalization 失败不得篡改已完成的核心数据结果；archive 成功后的 cleanup 失败、restore data 成功后的 Rejoin 失败均为 `SuccessWithWarnings`。
- 新 Backup Run/History 记录 outcome、diagnostics、FolderId、root ArtifactId、ArtifactFormatRef/version 和 dependency graph revision；旧记录继续允许以 path 解析和重命名关联。
- Restore Mode 只决定已物化内容怎样应用到目标（Clean/Overwrite）；Artifact completeness/partial policy 可以约束 effective mode。Restore Materializer 负责从 Artifact graph 重建内容，二者与包围 mutation 的 Restore Coordinator 是三个不同角色。

---

## 2. Plugin API 3.0 公共边界

### 2.1 Abstractions 包策略

- 新建 `FolderRewind.Plugin.Abstractions`，`TargetFramework=net10.0`，BCL-only，不引用 WinUI、FolderRewind App、CommunityToolkit UI 或 Host implementation project。
- PackageVersion 为 `3.0.0`；整个 3.x 保持 `AssemblyVersion=3.0.0.0` 和 assembly name/type identity 稳定。
- Manifest 使用 `{ major, minor }` 表达所需 Plugin API；Host 只接受 `major == 3 && hostMinor >= requiredMinor`。Patch 不参与 contract compatibility。
- 3.x minor 可以增加 capability/service 和带默认行为的可选 contract，不能破坏已有 contract；破坏性变更只能进入 API 4。
- 开发期先用 repository project reference 或本地生成 prerelease package；M3 freeze 后验证 `dotnet pack`，NuGet.org 正式发布另行授权。

### 2.2 公共数据形状

- 公共 boundary 只传 immutable snapshot、draft、patch、descriptor、request/result 和 cancellation token。
- Snapshot 不携带 observable collection、Host service、UI type 或可变 Host model；所有路径、ID、scope、diagnostic 均有明确 contract type。
- Settings values 使用 `JsonElement`/等价 typed JSON value map；Provider State snapshot/patch 使用 `{ Location: { ConfigId, FolderId? }, StateOwnerId, SchemaVersion, JsonElement Data }`，补丁必须同时匹配 location、owner 和 expected schema version。
- Config snapshot 带 Host-issued revision。`ConfigChangeProposal` 必须声明 expected revision 和逐项 operation；Host 负责 conflict、字段权限、影响级别、完整校验、用户审阅与原子 commit。插件永远不能直接调用 save。
- Config Kind、ConfigId、FolderId、History/Artifact identity 不允许由插件更新。AutoCreate/AutoApply 只适用于用户显式开启的新增 Config/Folder 和 provider-owned option/state；删除 Folder、扩大 SourceScope，或修改用户的 ArtifactTransformPolicy、destination、schedule、retention、encryption、archive mode、Cloud、filter 等字段必须逐次显示 diff 并确认。
- Revision 9 provisional `IConfigAugmentationCapability` 在最终 API Freeze 前删除，由 `IConfigReconciliationCapability` 取代：Discovery/ConfigDraft 只负责创建新 Config，Reconciliation 对现有 Config revision 产生 Change Proposal（包括添加/更新/删除 Folder 和 policy/state 建议）。
- Activation 返回/提交的 patch 只能针对 Host 明确开放的 settings/provider state；Discovery/Config Reconciliation 都是 activation commit 后的独立业务事务。Host 负责版本检查、冲突、去重、验证与原子保存。
- DataStore 用于大体量、可重建或插件私有数据；不参与 Host settings/provider state 事务，插件必须 crash-safe/versioned/cache-rebuildable。

#### 2.2.1 M3R public contract map

| Contract/type | 冻结职责与最小输入输出 |
|---|---|
| `ConfigRevision` | Host-issued opaque value；不能由插件构造下一版本 |
| `IConfigReconciliationCapability` | 静态/运行时声明 compatible ConfigKinds；`ProposeAsync(ConfigReconciliationRequest)` 返回零或多个 `ConfigChangeProposal`，Host 不全局扫描调用 |
| `ConfigChangeProposal` | ConfigId + ExpectedRevision + reason/diagnostics + ordered changes；不含 save/commit delegate |
| config changes | `AddFolder`、`UpdateFolder`、`RemoveFolder`、`SetProviderOptions`、`SetArtifactTransformPolicy` 和明确列举的 user-policy changes；禁止修改 Kind/ConfigId/FolderId/History/Artifact identity |
| `ArtifactId` / `ArtifactGraphRevision` | Host-owned GUID / opaque revision；graph patch 必须匹配 expected revision |
| `ArtifactFormatRef` / format version | OwnerId + local FormatId + non-negative integer version；插件只能声明自己 owner 的 format |
| `IBackupArtifactTransformerCapability` | `TransformerId`；从 `ArtifactTransformRequest` 读取 staged primary/compatible candidates 和 graph revision，向 Host staging 写 logical outputs，返回 `ArtifactGraphPatch` |
| `IBackupCompletionObserverCapability` | `ObserveAsync(BackupCompletionSnapshot)`；只返回 diagnostics，不返回 patch/path/mutation result |
| `IRestoreMaterializerCapability` | `RestoreStrategyId`；`MaterializeAsync(RestoreMaterializationRequest)` 将 verified graph 写入唯一 workspace，返回 outcome/diagnostics |
| Artifact handles/services | opaque read handle、bounded read-only tree lease、bounded transform staging、materialization workspace 和 progress；不公开 repository root、encryption password 或 Host mutable service |
| manifest descriptors | formats、transformers、compatible kinds/modes/completeness、parameter schema、failure behavior、restore strategies/version ranges 和 high-impact requested services；必须与 runtime registrations 一致 |

### 2.3 Artifact Graph 与受控转换契约

- `BackupArtifactSnapshot` 至少包含 ArtifactId、FormatRef/version、immutable content handle、SHA-256、size、completeness、Core capture mode、FolderId、HistoryItemId 和依赖；插件不能获得 backup repository 的任意写权限。Handle 可由 Host 受控地提供 raw logical payload 或 materialized read-only tree，不能暴露 encryption password。
- Artifact dependency 方向固定为“当前 Artifact 的 materialization 需要哪些 dependency”。Graph 必须为无环有向图；Host 验证 owner/format、存在性、hash、无环、最大节点/深度和跨 Folder/Config 禁止规则。
- Transformer request 携带 expected graph revision，patch 必须回显；同一 Folder 的 graph commit 串行化，stale revision 不重放插件副作用而是 conflict/重试整个 operation。
- Config 以 Host-owned `ArtifactTransformPolicy = { TransformerId, TypedParameters, FailureBehavior }` 显式选择 transformer；默认没有 transformer。Transformer 可以属于不同于 Config Kind owner 的插件，这使 MineDelta 一类独立插件能与 MineRewind 组合而不 fork 其全部能力。`FailureBehavior` 只允许 `KeepPrimaryWithWarnings` 或 `RequireTransform`，不能由运行时代码临时决定。
- Transformer 只能读取 Host 提供的 current staged primary Artifact 和兼容的 committed candidates，并写 Host-owned transaction staging；禁止原地覆盖、移动或删除 committed Artifact。
- 插件输出是 logical Artifact payload；Host 在 graph commit 前应用自己的 encryption/integrity envelope 并计算最终 storage hash。插件不能接管或获知 Host encryption password，临时明文 workspace 遵守 operation cleanup/recovery policy。
- Host staging handles 强制 canonical relative paths、file/byte quotas、available-space reservation，并拒绝 symlink/reparse/ADS/case-collision；超额或越界输出使 transaction 失败。插件同进程信任边界不等于安全沙箱，但 unsupported output 仍不得进入 committed graph。
- Transformer 返回声明式 `ArtifactGraphPatch`：新增 Artifact、替代某 History root、依赖边、format/version、RestoreStrategyId、hash/size 和 diagnostics。Host 在 commit 前重新计算/验证文件事实，插件声明不能自证 hash、path 或 provenance；只能改写 request 明确开放的 current/compatible prior roots。
- Graph transaction 使用 copy-on-write + journal：prepare staging → validate graph/files → atomically switch metadata/root references → mark committed → deferred garbage collection。任一失败恢复旧 graph/root，保留原 committed Artifact；不会出现 History 指向半写文件。
- Transformer 运行在 Core capture 成功之后、History/prune/Cloud/完成事件之前。只有 Host commit 后的 graph 才能被 retention、Cloud 或 restore 观察。
- v3.0 Artifact Transformer 优化的是已保留 History 的 Artifact 表示和存储，不减少本次 Core source scan/capture/archive 成本；它必须从 staged primary/candidate handles 工作。让插件在 capture 阶段直接贡献 chunk/page/CAS units 的 Content-Aware Capture Engine 属于后续 API，不伪装成本次能力。
- 同一次 backup operation 分别租用 Config Kind owner session（FilePolicy/Scope/Consistency）和显式 transformer session；任一进入 Draining 都停止新的 operation，已有双 lease 自然结束。Completion observer 只路由给 Config Kind owner和显式 transformer owner，不做全局广播。
- History Item 是 graph root，不等同于 Artifact。删除/retention 一个 History Item 只移除其 root；Host 从全部剩余 roots 重新计算 reachability，仍被其他恢复点依赖的 Artifact 继续保留，只有不可达 Artifact 才能 deferred garbage collect。显式物理删除 reachable Artifact 必须 Block 并列出依赖 History；v3.0 不提供通用 graph rebase engine。
- `BackupCompletionObserver` 在 Artifact/History root durable commit 和 Cloud queue transaction 建立后、Backup Run final outcome/diagnostic seal 前收到 immutable core-result snapshot；它可使用 Manifest 声明的 Notification/KnotLink/DataStore/logging 等服务执行集成动作，但对 Host Config/Artifact/History/Cloud queue 只读且不能返回 graph patch。delivery 为 best-effort/at-most-once，插件应自行令外部动作幂等；其异常只让 Host 在 append-only finalization 中追加 warning/将 overall outcome 提升为 `SuccessWithWarnings`，不回滚成功备份。若进程在 core commit 后、final seal 前崩溃，journal recovery 以 `observer_incomplete` warning 完成 seal，不重跑未知完成度的外部副作用。

### 2.4 Restore Materializer 契约

- Restore Strategy 由 Manifest 静态声明 `RestoreStrategyId`、支持的 ArtifactFormatRef/version range、completeness 和可接受的 Host Restore Mode；Runtime registration 必须与 Manifest 精确一致。
- Host 完成 history/graph resolution、Cloud 补全、artifact hash/integrity、password 和完整 preflight 后，才可进入 Restore Coordinator；owner/strategy missing、disabled、Failed 或 format/version 不支持一律 Block。
- Coordinator 的 once-only continuation 内先调用 Materializer，把所选 History 的 Artifact graph 重建到隔离的 Host-owned materialization workspace；Materializer不得直接写用户目标目录或修改 committed Artifact。
- Materializer 成功后，Host 使用既有 Safe Restore mutation 将 workspace 按 requested/effective Clean/Overwrite 与 Artifact completeness/partial policy 应用到目标；这样新 Artifact 格式不能绕过 BackupBeforeRestore、Safe Restore、progress、cancellation、integrity、player preservation 或 lifecycle events。
- Restore operation 可同时持有 Config Kind owner 的 Coordinator lease 与 Artifact 记录指定的 Materializer owner lease；二者身份、故障和 diagnostics 独立，Materializer 不因参与还原而取得 Config Kind ownership。
- Materializer request 提供 Host 已验证并解开 storage envelope 的只读、拓扑排序 Artifact handles，目标 History/Folder snapshots、requested/effective Restore Mode、workspace、progress 和 cancellation；一次 operation 只允许 materialize 一次。输出必须经过 Host 路径边界、完整性和 scope validation。
- Materialization workspace 使用独立 file/byte/depth quotas、disk reservation 和 canonical/reparse validation；超额、path escape、collision 或 unexpected output 在 target mutation 前 Block。
- Materializer 失败或取消发生在目标 mutation 前；Host 清理 workspace，Coordinator 执行必要的恢复/Rejoin，并持久记录 outcome/diagnostics。目标 mutation 成功后的 Coordinator finalization 失败仍为 `SuccessWithWarnings`。

### 2.5 静态 Schema 与 Manifest

- `settings.schema.json` 在禁用插件和 Safe Mode 下可读；根为 `{ schemaVersion: 1, settings: [...] }`，setting 至少包含唯一 `key` 与 `type`，可包含 `required`、`default`、`displayName`、`description`，enum 额外声明非空且唯一的 `enumValues`。
- v3.0 setting type 固定为 string、boolean、integer、multiline、folderPath、filePath、enum；Host 负责 default、required、type/enum validation。schema 未识别的旧/未来 value 原样保留并产生 warning，不因设置 UI 往返丢失。
- Manifest contract 静态声明 PluginId/version/API requirement/entry、Config Kind metadata、settings schema 相对路径、requested Host Services、Artifact formats、transformers（含 compatible ConfigKinds/core modes/completeness）、restore strategies 和 completion observer；运行时不得用执行插件代码补充这些声明。
- Backup Scope 复用轻量 form schema；未知 `EditorHint` 回退基础控件。
- Config Change Proposal 与 ArtifactTransformPolicy 的 typed parameters 复用同一受限 form schema；schema 不授予写权限，Host 仍按字段所有权和影响级别裁决。
- Manifest Config Kind metadata 至少包含 stable KindId、本地化 display、icon/description；metadata cache 允许插件缺失时展示 last-known identity。
- Requested Host Services 扩大时必须在更新前向用户展示；高影响服务变化不得静默自动更新。
- 首次启用请求 ArtifactRead/TransformStaging/RestoreMaterializationWorkspace 的插件时必须明确说明其可读取备份内容并生成可恢复数据；用户拒绝即保持 Disabled，不通过“权限不足 fallback”执行插件代码。

---

## 3. 持久化、迁移与恢复

### 3.1 App Config Schema 1 与 Artifact Ledger Schema 1

App Config 新增整数 `schemaVersion=1`；字段缺失视为 legacy v0，未来版本高于 Host 支持范围时不允许降级读取或保存。

Schema 1 至少包含：

- `BackupConfig.Kind = { OwnerId, KindId }`；
- `ManagedFolder.Id` 稳定 GUID；clone/template/history/UI 不得无意重新生成；
- Config/Folder `ProviderStates[StateOwnerId] = { SchemaVersion, Data }`；
- Plugin settings typed JSON、Enabled Intent；Installed/Runtime observation 不写回 intent；
- Provider-owned scope identity `{ OwnerId, ScopeId, Parameters }`；
- Config revision 与可选 `ArtifactTransformPolicy = { TransformerId, Parameters, FailureBehavior }`；
- legacy preservation 容器，只读保存未知 ConfigType/ExtendedProperties 及 migration warning。

Artifact Ledger 是独立于 App Config 的 Host-owned repository schema，`schemaVersion=1`，跟随对应 backup storage/Cloud metadata，而不是嵌入用户配置：

- Backup Run/History 保存 outcome、diagnostics、FolderId 和 root ArtifactId；
- 每个 immutable Artifact 保存 FormatRef/version、RestoreStrategyId、content location、logical/storage integrity metadata、size、completeness、Core capture mode、dependency IDs、graph revision、local/cloud state 和 transaction ID；
- 每个 committed graph revision 有可读回的 manifest/root mapping；History Item 只保存 root ArtifactId，删除 Config 不自动删除 ledger/artifacts；
- Cloud 必须同步 graph manifest 与 reachable Artifact closure，下载恢复也以同一 revision 校验，不能只按相邻文件名猜链。

Template/Preset schema 同步升级：`BaseConfigType` 改为 Config Kind；required plugin、provider scope 和明确标为 shareable 的 provider defaults 可导出。机器路径、运行时 state、未知 opaque state、DataStore 内容不进入分享包；`TemplateId`/`TemplateName` 等 Host origin 使用明确 Host 字段而非插件 state。

### 3.2 Legacy v0 → Schema 1 映射

| Legacy | Schema 1 |
|---|---|
| `ConfigType=Default` 或空 | `folderrewind.core/default` |
| `ConfigType=Minecraft Saves` | `com.folderrewind.minerewind/minecraft-saves` |
| `BackupScope.PluginScopeId=MineRewind.SelectedRegions` | owner `com.folderrewind.minerewind` + scope `selected-regions` |
| `PluginEnabled[com.folderrewind.minerewind]` | 同 PluginId 的 Enabled Intent |
| string settings `AutoDiscoverSaves`、`AutoCreateConfigs`、`PreservePlayerData` | typed boolean，保留现值和既有默认值 |
| `MinecraftVersion`、`MinecraftInstancePath` | 机械搬到 MineRewind config state v0，由 MineRewind migrator 解释/升级 |
| ExtendedProperties `Plugin` | 转为 Kind/required plugin；原值仍进入 legacy preservation 供诊断 |
| legacy ManagedFolder | 一次性生成 GUID 并在同一迁移事务持久化 |
| legacy Full/Smart/Overwrite History archive | 在 Artifact Ledger migration 中为现有文件生成 ArtifactId；映射为 `folderrewind.core/archive-set` v1、Core materializer 和既有 Smart dependency graph；不改写 archive bytes |
| 未知 ConfigType/ExtendedProperties | Core default/可解析通用数据 + unresolved legacy container + warning，不静默丢弃 |

Host 只做结构映射，不解释 Minecraft 私有数据；MineRewind 在 activation staged migration 中将自己的 state 从 v0 升到正式 schema。Migration 必须 idempotent，成功后的 runtime 不长期双读 legacy 字段。

### 3.3 原子迁移算法

1. 以 read-only stream 读取原始文件和原始 bytes，区分 missing、malformed、unsupported-newer、legacy、current。
2. legacy 时先在同目录创建不可覆盖的 timestamped recovery copy，并 fsync。
3. 在内存构建对应 Schema 1；App Config 校验 ID 唯一性、Kind/Scope、JSON 类型、Provider State wrapper、路径和必填 invariant；Artifact Ledger 校验 identity、History roots、DAG、hash/size facts、format/strategy 和 repository path。
4. 写入同目录 temporary file，flush-to-disk；从 temporary file 重新 deserialize 并执行同一 validator。
5. 使用 Windows 原子 replace 切换，并保留最近一次 pre-migration backup；重新读取正式文件验证。
6. 任一步失败都保留原正式文件，清理临时文件并进入 Recovery Center；第二次启动不得重新生成 FolderId 或重复 state mapping。

Import Config 使用同一 parser/migration/validator，不允许绕过 schema gate。当前“反序列化失败后创建并保存默认配置”的行为必须在 M1 消除。

### 3.4 Recovery Center

Recovery Center 是受限启动状态，不是普通主界面。它禁止插件 DLL、配置保存、备份、还原、自动化和更新，仅允许：查看本地化诊断与 App Config/Artifact Ledger 文件位置、导出原始文件/graph manifest、列出并选择 recovery copy、重试 migration/ledger validation、显式二次确认后重置。Artifact Ledger 故障不得以创建空 graph 覆盖；用户关闭应用不修改任何配置文件。

---

## 4. 标准备份与还原语义

### 4.1 Backup

固定顺序：

`normalize invocation → resolve kind/runtime/readiness → mandatory FilePolicy → Provider Scope → user SourceScope/config filter intersection → validate effective selection → acquire Consistency Lease → difference detection → Core primary Artifact capture into staging → release consistency in finally → optional Artifact Transform transaction → validate/commit Artifact graph + History root/core Run record → retention → Cloud queue → completion observer → final outcome/diagnostic seal → notification`

- Effective selection 等价于 `SourceScope ∩ UserSelection ∩ MandatoryProviderSafetyPolicy`；任何层只能收窄，不能重新加入已排除文件。
- FilePolicy 只表达 correctness/safety 强制规则；一般优化建议不能借此覆盖用户选择。
- Scope/filter invalid 必须发生在 consistency side effect 前；Consistency Lease 覆盖 diff + archive capture，并在 capture 后尽早释放。
- Core capture mode 与 Artifact transform policy 正交：Full/Smart/Overwrite 仍定义 primary capture；transformer 必须在 Manifest/descriptor 中声明其接受的 mode 和 Complete/Partial 条件。条件不满足时在任何 transformer side effect 前 Block 或按配置保留 primary + warning。
- Core primary Artifact、transformer outputs 和 graph metadata 在 durable commit 前均不可被 History/Cloud/retention/完成事件观察；transform 失败使用 copy-on-write 回滚，绝不原地破坏旧 Artifact。
- Full Minecraft backup 在 MineRewind/KnotLink consistency 不可用时，Manual 和 Automation 均可 raw fallback；结果为 `SuccessWithWarnings`，更新成功时间且 Automation 不重试，diagnostic 持久显示“不保证应用一致性”。
- Provider-owned `selected-regions` 缺失或 invalid 必须 Block，绝不退化为 Full；`ConsistencyIntent=Require` 缺失也必须 Block。
- 对 reverse-delta 类 transformer，`Smart` primary、Partial backup、不同 FolderId/ConfigId、format/version 不兼容或超过声明深度时不得被当作 Complete Full anchor。安全默认是保留新 primary Full 并产生 warning；用户选择 `RequireTransform` 时本次 operation Failed 且 staged primary 不提交。
- Core default 无插件路径的 Full/Smart/Overwrite、history、prune、cloud、encryption 行为保持不变。

### 4.2 Restore

固定顺序：

`normalize requested/effective mode → resolve kind/runtime/readiness + Artifact owner/strategy → resolve Artifact DAG/local/cloud availability → password → graph/hash/integrity verification → PRE-FLIGHT COMPLETE → enter Restore Coordinator → external Save & Exit/preparation → BackupBeforeRestore through normal v3 backup operation → once-only continuation: materialize Artifact DAG to isolated workspace → Host Safe Restore mutation → player-state/finalization → Rejoin → UI/events`

- Config Kind owner missing/disabled/Failed 时一律 Block restore；restore 不继承 backup 的 raw fallback。
- Archive/cloud/password/chain/integrity 失败不得先触发 Minecraft Save & Exit。
- Artifact dependency 缺失、graph cycle、hash mismatch、format owner/Restore Strategy 缺失或 version 不兼容均属于 preflight Block；不得退化为把 delta 文件当普通 archive 解压。
- `BackupBeforeRestore` 在世界退出后执行；失败或取消时不得调用 mutation continuation，并由 Coordinator 尝试必要的 Rejoin/恢复。
- Host continuation 每 operation 最多一次；Coordinator 可以完成、阻止或失败，但不允许隐式 engine takeover。Materializer 只拥有“Artifact → isolated source tree”，Host 仍拥有“source tree → target”的 Clean/Overwrite mutation；Partial Artifact 由 completeness policy 强制约束 effective mode。
- Core file restore 成功而 player finalization/Rejoin 失败时返回 `SuccessWithWarnings`，明确告知“文件已恢复，集成收尾失败”。
- 保持 safe restore workspace、partial backup restore policy、Smart/Cloud chain、encryption、Clean/Overwrite 的 Core 安全语义。

---

## 5. 六个实施里程碑与提交序列

下列提交是依赖顺序与语义边界，不要求机械拘泥文件拆分。每个提交完成后运行受影响项目 build/test；同一编号不得混入不相关重构。

### M1 — 安全与测试基础

1. `[Host] test(plugin-v3): capture host plugin and backup/restore baseline`
   - 固化 v2 scope/filter、augmentation、details、hotkey/KnotLink dispatch、启停/加载错误以及 Core Full/Smart/Overwrite/Restore/BackupBeforeRestore 行为。
   - 建 fake plugin doubles；测试锁业务语义，不锁 v2 type shape。
2. `[MineRewind] test(plugin-v3): capture complete parity baseline`
   - 补 discovery/path/config augmentation/file policy/metadata/snapshot/current_save/player preservation/hot restore/hotkey/KnotLink protocol state-machine 测试。
3. `[Host] refactor(testability): establish non-WinUI plugin core test seam`
   - 新建可直接 ProjectReference 的纯逻辑 runtime/policy/migration project；停止继续扩大 test project 链接生产源码的做法。
4. `[Host] feat(config): add schema parser, atomic migration, and Recovery Center`
   - 实现 3.3/3.4；加入代表性、malformed、unknown、新版 schema 和 fault-injection fixtures。

**M1 Gate**：migration atomic/idempotent、FolderId stable、unknown data retained、Recovery Center no-write；Host/MineRewind baseline 全绿。

#### M1 实施记录（2026-08-12，Gate 已获用户批准）

提交边界：

- Host `e509f85`：补充备份入口 consistency intent、不可恢复来源不得进入 restore mutation、可恢复来源逐项 once-only 的语义基线；
- MineRewind `514798b`，Host submodule pointer `73f3e65`：补充 hot restore backup-id 安全、partial/full restore mode、session.lock/LevelDB 占用探测基线；
- Host `05cbf34`：建立 `FolderRewind.Plugin.Runtime` 与独立 `FolderRewind.Plugin.Runtime.Tests`，配置 parser/migrator/validator/file transaction 不依赖 WinUI；
- Host `cc271d7`：Host source-generated JSON payload validation、Schema 1 入口、稳定 FolderId、unknown-field extension preservation、受限 Recovery Center、导入同一 schema gate 与显式恢复动作。

自检证据：

| 门禁 | 结果 |
|---|---|
| Runtime tests | 21/21；包含 representative/malformed/unknown/newer fixtures、每个 commit stage fault injection、payload validation、原文件与临时文件断言 |
| Host tests | 266/266，通过 |
| MineRewind tests | 42/42，通过 |
| Host x64 Debug build | 0 warning / 0 error |
| MineRewind x64 Debug build | 0 warning / 0 error；仍会经现有 ProjectReference 构建 Host，按计划在 M2/M3 消除 |
| migration atomic/idempotent | timestamped non-overwrite recovery copy、flush-to-disk、temporary read-back、bounded transient replace retry、atomic replace、formal read-back、post-replace rollback 均有自动测试 |
| FolderId stable | legacy 一次生成；Schema 1 重读不重生成；重复/非法值在持久化前修复并校验唯一性 |
| unknown data retained | JSON migration 使用 deep clone；Host `ObservableObject` 统一 extension-data round-trip；未知 ConfigType/ExtendedProperties 进入只读 preservation + warning |
| Recovery Center no-write | malformed/newer/current schema gate 的无写入断言通过；受限启动在普通 Shell 创建前 return；关闭窗口没有配置写路径 |

已知风险与后续边界：

- Recovery Center XAML 已通过 WinUI build，但本轮没有为手工 smoke test 改动真实 `%LocalAppData%` 配置；真实安装迁移 E2E 仍按 M6 门禁执行。
- Schema 1 的 Kind/Provider State/typed settings 目前通过受保护的 extension data 与 v2 字段并存，只为保证 M1 迁移与无损往返；M2 提交 6 必须替换成正式角色类型和持久化模型，M6 才删除 v2 字段。
- Host/MineRewind 并行构建争用仍是冻结计划中的已知基线问题，必须在 M2/M3 通过独立 Abstractions 引用消除，未在 M1 越界处理。
- 用户已于 2026-08-12 明确回复“M1 批准”；M2 授权生效。

### M2 — Abstractions、Runtime 与持久化

5. `[Host] feat(plugin-api): introduce FolderRewind.Plugin.Abstractions 3.0`
   - 建 public contracts、API compatibility validator、pack metadata 和 public API approval/baseline test。
6. `[Host] refactor(config): add role-specific identities and provider state`
   - Config Kind、FolderId、Provider State、typed settings、Enabled Intent、outcome diagnostics、template/preset schema。
7. `[Host] feat(plugin-runtime): transactional activation registry and runtime leases`
   - Temporary activation session、capability conflicts、states、Safe Mode、Draining、lease；所有 runtime core 可无 WinUI 测试。
8. `[Host] feat(plugin-loading): v3 loader and shared contract identity`
   - `AssemblyDependencyResolver`/等价 loader；Abstractions 始终来自 Default ALC；plugin package 出现其副本即拒绝。
9. `[Host] feat(plugin-settings): static schema and transactional switch`
   - Candidate validate → drain old → activate new instance with staged state → commit settings/state → failure restore old instance；augmentation 是 commit 后独立业务 step。

**M2 Gate**：Abstractions 可 build/test/pack；半途 activation 无注册残留；settings/state rollback；Safe Mode 不执行 DLL；两个插件私有依赖版本隔离。

#### M2 Gate 实施记录（2026-08-12，已批准）

实现提交：

- `9f63384 feat(plugin-api): introduce Abstractions 3.0`
- `adf5a67 feat(config): formalize plugin v3 persistence`
- `0fb5d94 feat(plugin-runtime): add transactional activation and draining`
- `1479e48 feat(plugin-runtime): isolate plugin assembly dependencies`
- `73c5721 feat(plugin-runtime): validate and transact typed settings`
- `21d95bf fix(config): keep v2 bridge aligned with v3 intent`
- `87f5054 test(plugin-loading): honor active build configuration`

Gate 证据：

| 检查项 | 结果 |
|---|---|
| Abstractions Release build/test | 9/9；0 warning / 0 error；BCL-only、AssemblyVersion `3.0.0.0`、public API SHA-256 baseline 全绿 |
| Abstractions local pack | `FolderRewind.Plugin.Abstractions.3.0.0.nupkg` 成功；只含 README、nuspec、`net10.0` DLL/XML 与 NuGet metadata；SHA-256 `1BA5F6AF40B924673314D5D12651915180799EE9F8C62EF491D011323BB27C8E` |
| Runtime Release tests | 42/42；覆盖 schema migration、activation rollback、capability conflict、state migration、Safe Mode、Draining/lease、settings rollback、ALC unload、contract DLL 拒绝及双私有依赖版本隔离 |
| Host tests | 266/266 Debug 全绿 |
| MineRewind tests | 42/42 Debug 全绿 |
| Host x64 Debug build | 0 warning / 0 error |
| MineRewind x64 Debug build | 0 warning / 0 error |
| activation 可见性 | capability 仅在 staged validation、provider migration 和 store commit 全部成功后进入 committed registry；失败实例 Deactivate 且不保留 key |
| settings/state rollback | 顺序固定为 candidate schema validation → old session Draining → new instance staged activation/migration → atomic store commit → registry swap；失败恢复旧 Active session/settings/state |
| Safe Mode | `--safe-mode` 在配置/插件初始化前识别；v2/v3 loader 均有硬门禁，factory/DLL 不执行；Enabled Intent 不改写；设置页提供确认后的“以安全模式重启”入口 |
| loader identity/isolation | Abstractions 强制返回 Default ALC；payload 自带该 DLL 即拒绝；两个同时加载的 fixture 各自得到 `Fixture.PrivateDependency` 1.x/2.x；collectible ALC 可释放 |

M2 后续边界与已知风险：

- 本地 NuGet 包只是四仓库开发候选，不代表 NuGet.org 发布授权；M3 API Freeze 后重新产出 candidate，正式发布仍需单独批准。
- Safe Mode 的 no-DLL 行为已有 runtime factory 测试、Host loader 硬门禁和 WinUI build 证据；真实安装进程 smoke test 仍属于 M6 安装 E2E。
- M2 建成 runtime/store/loader 边界和 Host schema 模型，但尚未让 MineRewind 采用 v3；Host v2 runtime 与 MineRewind Host ProjectReference 按计划保留到 M3 垂直切片/M6 clean break，当前仍不可并行构建两仓库。
- 用户已于 2026-08-12 明确回复“M2 批准”；M3 授权生效。M3 将接入 fake plugin 与 MineRewind 四条最小 E2E，并在其全绿后冻结 API 3.0。

### M3 — 最小垂直切片与 API Freeze

#### M3R 变更来源与重新批准规则

真实第三方 MineDelta fork 证明：Revision 9 能支持 MineRewind 的 discovery/consistency/coordinator/command，却不能安全表达 reverse semantic delta。v2 `OnAfterBackupFolder` 在 History/Cloud/完成状态之后原地修改旧 archive，v2 restore interceptor/takeover 又可绕过完整 preflight、BackupBeforeRestore、Safe Restore、progress/cancellation 和 target mutation lifecycle；把这些自由 Hook 原样带入 v3 会固化 chain poisoning、middle deletion、Cloud divergence 和不可追溯后台失败。

用户于 2026-08-12 明确认同“Host-owned Artifact Graph Transaction + Restore Materializer”的修订方向，并随后明确回复 `M3R 批准`。Revision 10、`CONTEXT.md`、ADR 0003 的 high-impact disclosure consequence 与 ADR 0005 因此冻结；ADR 0005 改为 accepted，当前门切回 M3 实施，并从提交 13 开始修改产品代码。用户同时授权在门禁证据全绿后自动批准 M3、M4，并连续实施到 M5 Gate；该授权不降低任何测试门禁，也不包含外部发布动作。

10. `[Host] feat(plugin-operation): operation resolution and outcomes`
11. `[MineRewind] refactor(plugin-api): remove Host project reference and adopt manifest v3`
12. `[Host+MineRewind] test(plugin-v3): run four vertical slices`
    - Discovery candidate → Host draft/commit；
    - consistency source → diff/archive same source；
    - restore preflight → coordinator → continuation once；
    - command identity → Host backup/restore request。
13. `[Host] feat(plugin-config): revision-bound config change proposals`
    - 删除 provisional `IConfigAugmentationCapability`；引入 ConfigSnapshot revision、`IConfigReconciliationCapability`、field ownership、add/update/remove operations、impact classification、stale rejection、review/auto-apply policy 和 atomic store；
    - AutoCreate/AutoApply 只允许用户预先授权的 additive/provider-owned 变化，destructive 或 user-owned policy 修改必须展示 diff 并逐次确认。
14. `[Host] feat(plugin-artifact): immutable artifact graph contracts and validator`
    - Artifact/format/transformer/strategy identities、manifest descriptors、completeness/core-mode facts、dependency DAG、hash/size/path facts、Artifact Ledger Schema 1 和 bounded graph validation；
    - legacy Core Full/Smart/Overwrite history 可映射为 Core Artifact graph，不改 archive bytes。
15. `[Host] feat(plugin-artifact): transactional transform staging and completion observer`
    - primary staged capture → transformer staging → graph patch validation → copy-on-write journal commit → deferred garbage collection；
    - committed Artifact 不允许原地变更；completion observer 只读且在 History/Run/Cloud queue durable commit 后调用。
16. `[Host] feat(plugin-restore): artifact materializer inside safe restore continuation`
    - preflight 解析/下载/校验完整 DAG；Coordinator once-only continuation 内先 materialize 到隔离 workspace，再由 Host 按 effective Restore Mode 执行 Safe Restore mutation；
    - strategy/owner/version missing、cycle/hash mismatch、materialization failure 均在目标 mutation 前 fail-closed。
17. `[Host] test(plugin-artifact): fake reverse-delta vertical slice and refreeze API 3.0`
    - fake plugin 只接受 Complete Full，产生 `older delta → newer full` graph；Smart/Partial 在插件 side effect 前拒绝；
    - 覆盖 transaction fault injection、middle History root 删除后依赖仍可达、reachable Artifact 物理删除 Block、depth anchor、Cloud/History order、restore mode propagation、materializer cancellation 和 missing owner；
    - 重新生成 public API fingerprint 与本地 NuGet candidate。此前 Revision 9 fingerprint/package SHA 全部作废。

**M3 Gate / API Freeze**：原四条 fake + MineRewind E2E、Config Change Proposal E2E、fake reverse-delta Artifact Transform/Restore Materializer E2E 全绿；committed Artifact 不可原地修改，History/retention/Cloud 只观察 committed graph；公开 API review 无 Host/UI leakage；MineRewind 可与 Host 并行独立 build。随后只允许兼容性 contract 修正，准备新的 NuGet 3.0.0 candidate；正式发布待授权。

#### M3 Revision 9 临时 Gate 记录（2026-08-12，已撤回）

Revision 9 曾满足当时定义的最小垂直切片，但真实第三方 MineDelta/Reverse Delta 用例证明 API 缺少安全的 Artifact transaction 和 Restore Materializer 边界。用户于 2026-08-12 接受修订方向；根据变更控制，原 M3 Gate 不再可批准，以下证据仅作为 Revision 10 的已完成基线，不构成 API Freeze。

实现提交：

- Host `7ddfbb3 feat(plugin-operation): resolve readiness and outcomes`：固化 Kind/runtime/scope/consistency 的 `Ready`、`Degraded`、`Blocked` 解析及结果 warning promotion；
- MineRewind `9c773a8 refactor(plugin-api): adopt MineRewind v3 abstractions`：产品项目移除 Host App ProjectReference，改为只引用 Abstractions，并实现 discovery、consistency、restore、command、provider migration 最小能力；
- MineRewind `c604ec2 test(plugin-v3): harden MineRewind vertical contracts`：补齐 Manifest kind metadata、command argument schema 和 restore warning preservation；
- Host `cfb148f test(plugin-v3): run four runtime vertical slices`：通过 committed Runtime Session/lease 跑通 fake 与 MineRewind 两组四能力 E2E，并冻结公开 contract 候选。

Gate 证据：

| 检查项 | 结果 |
|---|---|
| fake plugin 四条 Runtime E2E | Discovery candidate → Host validation/atomic draft commit；consistency lease 在 diff 前取得且 diff/archive 使用同一 source；restore safety backup → once-only mutation；command → Host backup service，全绿 |
| MineRewind 四条 Runtime E2E | activation commit 后通过 lease 执行 discovery/consistency/restore/command；Discovery 不调用 Host Config query，AutoCreate policy 由 Host coordinator 提交；全绿 |
| Abstractions Release tests | 9/9；0 warning / 0 error；BCL-only、AssemblyVersion `3.0.0.0`、public API baseline 全绿 |
| Revision 9 API fingerprint（已撤回） | SHA-256 `3f47ba8375c6d608fa551e08dcb6ac7da7e317fea9ab1dab69cf2d7e816ae730`；不得用于发布或兼容性承诺 |
| Revision 9 local package（已撤回） | `artifacts/nuget/FolderRewind.Plugin.Abstractions.3.0.0.nupkg`，SHA-256 `94A5A975325A487A392B7C7DCD07209D0A094CFB8A8F7F41B79674321ABD48E8`；M3R 实施后必须覆盖生成并记录新 SHA |
| Runtime Release tests | 61/61；其中新增两组共 8 条真实 activation/lease 垂直切片，并锁定 operation cancellation 与 restore continuation once-only |
| Host tests | 266/266 Debug 全绿 |
| MineRewind tests | 50/50 Release 全绿；v3 manifest、draft、consistency、restore failure/warning、command schema/routing、state migration 均覆盖 |
| Host + MineRewind 并行 build | Host x64 Debug 与 MineRewind x64 Debug 同时执行，均 0 warning / 0 error；未发生 WinUI `obj` 争用 |
| MineRewind 独立 build | x64 Debug 与 AnyCPU Release 均 0 warning / 0 error；产品 ProjectReference 仅指向 `FolderRewind.Plugin.Abstractions` |
| API/UI leakage review | Abstractions 只引用 BCL；MineRewind v3 产品只编译 `V3/**/*.cs`，不引用 WinUI、Host App、Host mutable model 或 v2 runtime interface |

仍然成立的边界：

- `Discovery Draft Commit` 明确为 Host-owned 原子事务：plugin 只返回 immutable candidate/draft，Host 深拷贝并验证后才依据用户 `AutoCreateConfigs` policy 提交；Host identity 的分配和持久化不属于 plugin capability。
- Restore mutation continuation 由 Host once-only gate 包装；operation cancellation 与 plugin lifetime token 已在 Runtime Session lease 中分离。

Revision 10 新增边界与非目标：

- M3 只完成 public contract、graph/transaction core 和 fake reverse-delta vertical slice；现有 Host backup/restore/discovery UI 的完整接线、retention/Cloud integration 和完整 MineRewind parity 属于 M4。
- MineRewind 产品 DLL 已 cleanly 采用 v3，但旧 v2 源仍保留且从产品编译中排除，legacy parity tests 仍链接这些源作为 M4 行为基线；v2 删除门仍是 M6。
- 1.9.0 不把 MCA delta engine 合并进官方 MineRewind；目标是让 MineDelta 一类第三方插件能复用其 codec/diff 算法，改写 Host integration 到 Artifact Transformer + Restore Materializer。
- 该迁移仍会先产生 Core primary Artifact，再做受控 reverse-delta transform；它改善长期历史存储而非单次备份读取/压缩成本。真正的 chunk-level incremental capture 不在 1.9.0 范围。
- 不承诺自动采用实验 fork 既有 `.rvdl`。任何 legacy Artifact import 必须先完整验证 graph/hash/format，再通过独立 Host-owned import transaction；未验证文件只读保留，不能进入正常 restore graph。
- `.frplugin` 安装、Manifest/package 静态 parser、Catalog 与 bundled offline upgrade 尚未开始，分别属于 M5；本地 NuGet candidate 不代表 NuGet.org 发布授权。
- Host 中现有 v2 runtime path 仍保留到完整 parity 和真实安装 E2E 通过；不得因 M3 API Freeze 提前删除。
- Revision 10 文档、术语和 ADR 0005 已获用户批准；从提交 13 开始继续 M3 产品代码。M3 Gate 证据全绿后依本轮特别授权自动批准并进入 M4，M4 Gate 同理；完成 M5 后必须暂停在 M5 Gate 等待人工测试与审阅。

### M4 — 完整 Host/MineRewind 迁移

18. `[Host] refactor(backup): integrate FilePolicy, Scope, Consistency, Artifact transaction, History/retention/Cloud`
19. `[Host] refactor(restore): coordinator and materializer around safe mutation continuation`
20. `[Host] feat(plugin-command): unify commands, hotkeys, and KnotLink integration`
21. `[MineRewind] refactor(discovery): migrate discovery, reconciliation, file policy, metadata, region scope, and state migration`
    - `.minecraft`、直接 saves、`versions/*/saves`、server root 保持；plugin 只给 candidate/draft，Host AutoCreate policy 提交。
    - `session.lock`、Voxy、Distant Horizons 变为 runtime FilePolicy，不污染用户 filter。
22. `[MineRewind] refactor(backup): migrate snapshots and active commands`
23. `[MineRewind] refactor(restore): migrate hot restore and player preservation`
24. `[Host] test(plugin-artifact): integrate fake third-party artifact format with retention, Cloud, and Safe Restore`
25. `[Host+MineRewind] test(plugin-v3): complete regression and parity gates`

**M4 Gate**：第 8 节三份 checklist 全绿；backup degrade/restore fail-closed 矩阵、Artifact graph/history/retention/Cloud/Safe Restore 全绿；尚不删除 v2。

### M5 — Package、Update、Catalog 与 Preset

26. `[Host] feat(plugin-package): .frplugin validator and versioned install layout`
    - ZIP root 直接包含 manifest；先 canonical validate 全 entries 再写盘；限制 entry count、单项/总展开大小和压缩比；staging 后 commit。
    - `plugins/{PluginId}/versions/{SemVer}` + Host-owned ledger/current/previous pointer；binaries 与 settings/state/data 分离。
27. `[Host] feat(plugin-update): journaled known-good rollback`
    - Download/hash/static validate → stage → drain → candidate pointer → Activate/migrate → commit known-good；失败恢复 pointer/settings/state/runtime。
    - 启动时读取 transaction journal，将 prepare/committing 中断恢复到 last committed known-good。
    - 更新前比较候选 Manifest/Runtime registration 与所有 reachable owned ArtifactFormat/version；候选不能 materialize 既有数据时 Block update 并保留当前版本，不能仅靠 previous known-good 猜测路由旧格式。
28. `[Catalog] feat(catalog): schema, validation CI, and GitHub Pages production index`
    - 独立 repo；source entries 经 CI 生成 production JSON。字段至少包括 PluginId/version/channel/API/architecture/artifact URL/SHA-256/provenance/trust classification/requested services，以及 artifact formats/transformers/restore strategies 的静态摘要。
29. `[MineRewind] chore(release): build .frplugin and create catalog PR`
    - Artifact 不含 Abstractions DLL；release workflow 计算 SHA 并自动开 Catalog PR，Catalog CI 下载复验 manifest/hash。
30. `[Host] feat(plugin-store): official catalog and manual install`
    - 删除 StoreRepo；Catalog cache 离线不影响 installed plugin；Manual build 不被 official background update 覆盖。
31. `[Host] feat(plugin-upgrade): bundle MineRewind v3 for offline migration`
    - Host 安装介质携带固定官方 `.frplugin` + hash。检测 legacy MineRewind installed/enabled/data 时安装 v3 并保留 intent；新用户仅在选择 Preset 时安装且默认 Disabled。
    - 旧 flat v2 payload 移入可恢复 `legacy-quarantine`，不执行、不删除。
32. `[Host+Catalog] feat(plugin-preset): Minecraft Enhanced Experience`
    - 数据驱动有限 actions：install/enable plugin、Host feature/integration setup、notice；禁止脚本。
    - KnotLink external installer 使用固定 URL+SHA-256，下载和启动前显式确认；步骤结果可诊断，半成功不伪装整体成功。
33. `[Host] feat(plugin-uninstall): preserve-data and delete-data flows`
    - 默认仅删 code，保留 intent/settings/provider state/data；独立危险操作列出范围并二次确认后删除。

**M5 Gate**：malformed/ZipSlip/collision/bomb 无半安装；update crash/failure 恢复 known-good；离线 legacy MineRewind 升级；Catalog/Manifest/hash 一致；普通卸载保留数据；Site/Package/Catalog CI 全绿。

### M6 — Clean break 与发布候选

34. `[Host] refactor(plugin-v3): delete v2 runtime and Minecraft special cases`
    - 删除 v2 interfaces/hooks/takeover、runtime claim scan、PluginHostContext convenience API、string settings、ConfigType、PluginScopeId、IsMinecraftConfig、Plugins.Enabled、StoreRepo、MinHostVersion runtime/update 语义。
    - 仅保留一次性 legacy migration/quarantine code，不得可执行 v2 contract。
35. `[MineRewind] refactor(plugin-v3): delete v2 and Host-internal references`
36. `[Site] docs(plugin-v3): replace all 1.8 plugin development and Minecraft integration docs`
    - 同步中英文 architecture/API/settings/package/update/trust/migration/commands/KnotLink/MineRewind reference docs。
37. `[Host+MineRewind+Catalog+Site] test(release): validate 1.9.0 release candidate`
    - 完成真实安装 E2E：Host 1.9.0 → bundled/catalog `.frplugin` → enable → activation → discovery → backup → restore → failed update rollback。

**M6 Gate**：第 9 节 Definition of Done 全满足；生成本地 RC 证据，等待外部发布授权。

---

## 6. Package、安装、更新与 Catalog 规范

### 6.1 `.frplugin` 静态安全

- 扩展名 `.frplugin`，内部 ZIP，root 必须直接包含 manifest；不接受 v2 顶层 wrapper 作为 v3 包。
- PluginId 严格 reverse-domain lowercase，非法即拒绝，不 sanitize。
- SemVer 支持 prerelease；API/architecture/entry/schema/kind/scope/service/artifact-format/transformer/restore-strategy IDs 和交叉引用在执行 DLL 前校验；Artifact capability 缺少对应高影响 service declaration 即拒绝包。
- 拒绝 `FolderRewind.Plugin.Abstractions.dll`、install/post-install script contract。
- 在 extraction 前拒绝 absolute/drive/UNC、`..`、sibling-prefix escape、directory entry escape、reserved Windows names、trailing dot/space、ADS、canonical duplicate 和 case-insensitive collision。
- 使用固定安全上限并作为 internal constants + boundary tests；实施时数值可调，但默认建议：10,000 entries、1 GiB total、256 MiB per entry、100:1 compression ratio。
- 安装不 Activate；manual/catalog 共用同一 validator/installer pipeline；新安装默认 Disabled。

### 6.2 事务与 provenance

- Install provenance 为 Official Catalog 或 Manual；Manifest 不能自证 provenance/trust/update source。
- `install-state.json`/等价 ledger 由 Host 原子写入，记录 current、previous known-good、versions、provenance 和 transaction ID。
- Journal phases 至少为 prepared/candidate-selected/activation-validated/committed；启动 recovery 将任何未 committed transaction 恢复到 last committed pointer/state snapshot。
- Host 自动 snapshot settings/config-state/folder-state；DataStore 不复制回滚。
- Current + Previous Known-Good 至少保留；清理更老版本只能发生在新版本 commit 后。
- 如果 installed plugin 是任一可恢复 History ArtifactFormat 的唯一 owner，普通 disable/uninstall 必须预警并明确展示受影响 History；代码删除后 History/Artifact 仍保留，restore fail-closed。危险“删除数据”不得默认删除仍被 History graph 引用的 Artifact。

### 6.3 Official Catalog

- 独立 `Leafuke/FolderRewind-Plugin-Catalog` 仓库为目录源码真相，GitHub Pages 的 CI generated production JSON 是 Host 获取入口。
- Catalog release 指向确定 `.frplugin` URL + SHA-256；GitHub Release 仅托管 artifact，不猜第一个 ZIP，不 fallback source zipball。
- Host 强制比较 Catalog 与 package Manifest 的 PluginId、Version、API、architecture，并验证 SHA-256。
- v3.0 不实现 publisher signature、第三方 Catalog 或 plugin dependency solver；schema 保留未来 signature/trust-root 扩展位。

---

## 7. 故障、降级与恢复矩阵

| 场景 | 必须结果 |
|---|---|
| Config malformed / migration failure / newer schema | Recovery Center；原文件不变；插件/operations 禁用 |
| MineRewind missing，打开 Minecraft Config | 通用 Config/Folder/History 可管理；last-known metadata 显示 owner missing |
| MineRewind/KnotLink unavailable，Manual Full Minecraft backup | raw backup + persistent `SuccessWithWarnings` |
| MineRewind/KnotLink unavailable，Automation Full Minecraft backup | raw backup + persistent `SuccessWithWarnings`；算成功、不重试 |
| `selected-regions` owner missing/invalid | `Blocked`，不能退化 Full |
| Consistency `Require` unavailable | `Blocked` |
| Config Change Proposal expected revision stale | 不写入；返回 conflict + current revision；重新 proposal/review |
| Config proposal 删除 Folder/扩大 scope/修改 user-owned policy | 永不 auto-apply；显示逐字段 diff 并要求显式确认 |
| 用户拒绝 Artifact high-impact disclosure | 插件保持 Disabled，DLL 不执行；已安装文件/intent/data 不被伪装成运行失败 |
| Artifact transform policy owner missing/Failed | `KeepPrimaryWithWarnings` 时提交 Core primary；`RequireTransform` 时不提交本次 staged primary 并 Failed |
| Transformer 收到 Smart/Partial/不兼容 format | 在插件 side effect 前 Block 或按静态 failure policy 保留 primary；不得建立 delta edge |
| Transformer 异常/取消/graph patch 非法 | 丢弃 transaction staging；旧 graph/root/artifacts 不变；journal 可恢复 |
| Concurrent transform graph revision stale | 不提交、不复用旧 patch；返回 conflict，并从新 snapshot 重新执行完整 operation |
| Transformer/materializer staging quota、disk reservation 或 path validation 失败 | 清理 staging/workspace；graph/History/目标均不变；持久 diagnostic |
| Crash during Artifact graph commit | 启动 recovery 恢复 last committed graph/root；未引用 staging/garbage 延后清理 |
| 删除/retention 一个 middle History root | 只移除 root；仍从其他 roots 可达的依赖 Artifact 保留；所有剩余 History 仍可恢复 |
| 显式物理删除 reachable Artifact | Block 并展示依赖 History；v3.0 不静默断链、不在 delete callback 临时 rebase |
| Artifact owner/Restore Strategy missing/disabled/Failed | History 可查看/导出，restore `Blocked`；不得 raw extract |
| Artifact graph cycle/missing dependency/hash mismatch/version unsupported | preflight `Blocked`；不进入 Coordinator，不修改目标 |
| Materializer 失败/取消/path escape | workspace 丢弃；目标 mutation 未开始；Coordinator 尝试恢复/Rejoin |
| Minecraft Kind owner missing/disabled/Failed，restore | `Blocked`，不允许 raw restore |
| Archive/password/chain/integrity preflight failure | 不发送 Save & Exit，不进入 Coordinator mutation |
| BackupBeforeRestore failure/cancel | 不调用 restore continuation；Coordinator 尝试恢复/Rejoin |
| Plugin Activate exception | Runtime Failed；Enabled Intent 不变；session registration/state patch rollback |
| Settings candidate activation failure | 恢复旧 settings/state，并用新旧实例规则恢复旧 runtime |
| Update candidate activation/migration failure | pointer/settings/state/runtime 回滚 previous known-good |
| Plugin update drops support for reachable owned Artifact format/version | 静态/activation compatibility gate `Blocked`；当前版本继续运行，用户 History 不失去 restore owner |
| Process crash during update | 下次启动 journal recovery 回到 last committed known-good |
| Backup archive 成功，snapshot cleanup 失败 | `SuccessWithWarnings` |
| Backup core commit 成功，Completion Observer 失败/进程中断 | `SuccessWithWarnings`；Artifact/History 不回滚，不重跑 observer；final seal diagnostic 可追溯 |
| Restore files 成功，player finalization/Rejoin 失败 | `SuccessWithWarnings`，明确文件已恢复 |
| Disable/Update/Settings Change 有 active lease | Draining 等待；不强杀 operation；可取消变更/重启后应用 |
| Safe Mode | 不执行 DLL；intent/data 不变；Config/Plugin/Recovery UI 可访问 |
| Catalog offline/error | installed plugins 正常使用；Store 显示 cache/error |
| 普通 uninstall | code 删除，intent/settings/state/data 保留 |
| uninstall and delete data | 二次确认后删除列明数据；不可与普通 uninstall 混淆 |

---

## 8. 测试与验收清单

### 8.1 MineRewind parity gate

- [ ] Config Kind 正确显示；owner missing 时保持 unresolved provider-backed identity。
- [ ] `.minecraft`、direct saves、`versions/*/saves`、server root discovery 不回退。
- [ ] 批量 draft/Host auto-commit 与 `AutoDiscoverSaves`、`AutoCreateConfigs` 行为不回退。
- [ ] `session.lock`、Voxy、Distant Horizons mandatory policy 不回退且不写用户 filters。
- [ ] level.dat world name/mode/seed/days/time/last-played/player-data/format 不回退。
- [ ] overworld/nether/end、areas、region/entities/poi scope 不回退；mods 等 folder 仍 NotApplicable。
- [ ] WORLD_SAVED、snapshot、current_save、cancel/failure cleanup 不回退。
- [ ] PreservePlayerData、Save & Exit → BackupBeforeRestore → Host restore → Rejoin 不回退。
- [ ] Hot Backup/Quick Restore commands 和默认 hotkeys 不回退。
- [ ] KnotLink inbound command/capability/signal/query/broadcast 不回退。
- [ ] MineRewind state migration、uninstall/reinstall data preservation 全绿。
- [ ] MineRewind production code 不引用 Host App project/internal namespace/type。

### 8.2 FolderRewind Core gate

- [ ] Default Kind Full、Smart/Incremental metadata/deletion、Overwrite、NoChanges/Automation count。
- [ ] Encryption/password、history path/entry/run、pruning、folder rename association。
- [ ] Cloud upload queue、restore chain completion、offline installed plugin behavior。
- [ ] Safe Restore、Clean/Overwrite、partial policy、Smart chain、integrity、BackupBeforeRestore。
- [ ] Template create/apply/import/export 在 Kind/Provider State/schema 迁移后不丢通用数据或泄露 private state。
- [ ] Config migration atomic/idempotent、Recovery Center、Safe Mode、unknown data preservation。
- [ ] Activation transaction/conflicts、settings/update rollback、lease/draining、outcome diagnostics。
- [ ] Config proposal revision conflict、field ownership、additive auto-apply、destructive confirmation、atomic rollback。
- [ ] Legacy Full/Smart/Overwrite history 映射为 Core Artifact graph 后，restore/prune/cloud 行为不回退。
- [ ] Artifact Ledger migration/manifest read-back/hash/DAG validation 原子且幂等；malformed/newer ledger 不被空 graph 覆盖并进入受限恢复。
- [ ] Package path safety、catalog hash、offline bundled MineRewind、uninstall modes。
- [ ] MSI/MSIX x86/x64/ARM64 build/publish 不因 Abstractions/bundled package 回退。

### 8.3 第三方 Artifact 扩展 gate

- [ ] fake reverse-delta transformer 只接受 Complete Full；Smart、Overwrite/Partial、跨 Folder/Config 和不兼容 format 在 side effect 前拒绝。
- [ ] transformer 只能写 transaction staging；对 committed Artifact 的 path/write/delete 请求被拒绝。
- [ ] graph patch 校验 identity、format version、hash/size、路径、无环、bounded depth/node count 和引用存在性。
- [ ] concurrent same-Folder transforms 由 expected graph revision/serialization 防止 lost update；stale patch 不能提交或只重放 commit。
- [ ] transformer/materializer 对 absolute/`..`/ADS/reparse/case collision、file count/bytes/depth、disk exhaustion 的 boundary tests 全部无半提交。
- [ ] prepare/文件写入/graph validate/metadata switch/commit mark 每一阶段 fault injection 均保持旧 root 可恢复、无半提交 History。
- [ ] transform 完成后 History、retention、Cloud 只看到 committed graph；completion observer 最后执行且只读。
- [ ] observer exception 通过 final outcome seal 持久为 warning；core commit 后崩溃恢复不重跑 observer，并写入 `observer_incomplete`。
- [ ] encrypted primary/plugin output 由 Host envelope 解开/封装；插件从未收到 password，local/Cloud restore round-trip 全绿。
- [ ] oldest History root 删除后其不可达 delta 可 GC；middle/anchor History root 删除仅移除 root并保留仍可达依赖；显式物理删除 reachable Artifact Block。
- [ ] depth 到界时保留新的 Full anchor，不生成超深 edge；失败不修改旧 chain。
- [ ] restore preflight 补齐 Cloud graph、校验每个 Artifact hash；cycle/missing/corrupt/unsupported/missing owner 全部 fail-closed。
- [ ] Materializer 在 Host workspace 重建目标，收到 requested/effective mode、progress/cancellation；不得写真实 target 或 repository。
- [ ] BackupBeforeRestore 失败不调用 Materializer；Materializer 失败不进入 target mutation；成功后 Host Safe Restore/Clean/Overwrite/Partial 语义保持。
- [ ] plugin disable/update 在 transform/materialization active lease 期间 Draining；不强制卸载。
- [ ] package/enable/update 对 Artifact high-impact services 做静态一致性和 disclosure；候选丢失 reachable format/version support 时 update Block。
- [ ] fake transformer PluginId 与 Config Kind owner 不同：只有 Config 显式选择且 Manifest compatibility 匹配时运行；未选择时不扫描、不调用，两个 Runtime Session leases 均受 Draining 保护。
- [ ] fake third-party plugin 仅引用 versioned Abstractions，无 Host/UI 类型，并可在 Host build 同时独立构建。

### 8.4 可执行命令

每个里程碑按受影响范围执行，M6 必须全部执行：

```powershell
dotnet test .\FolderRewind.Tests\FolderRewind.Tests.csproj -c Release --nologo
dotnet test .\FolderRewind.Plugin.Runtime.Tests\FolderRewind.Plugin.Runtime.Tests.csproj -c Release --nologo
dotnet test .\FolderRewind-Plugin-Minecraft\MineRewind.Tests\MineRewind.Tests.csproj -c Release --nologo
dotnet pack .\FolderRewind.Plugin.Abstractions\FolderRewind.Plugin.Abstractions.csproj -c Release --nologo
```

Host build/publish 必须按现有 workflow 对 x86/x64/ARM64 和 MSI/MSIX 执行；MineRewind 在独立仓库 checkout 使用本地/API candidate feed build、test、pack `.frplugin`。Site 执行：

```powershell
npm ci
npm run typecheck
npm run check:i18n
npm run check:images
npm run build
```

Catalog CI 执行 schema validation、duplicate identity/version、SemVer/API/architecture、HTTPS URL、artifact download、SHA-256，以及 services/formats/transformers/strategies 的 Manifest cross-check。命令的最终脚本路径在 M5 建仓时写回本节。

---

## 9. Definition of Done

Plugin System v3 只有同时满足以下条件才完成：

1. FolderRewind production code 只存在 v3 runtime，不存在可执行 v2 compatibility layer。
2. `FolderRewind.Plugin.Abstractions` 3.0 可独立 build/test/pack，公共 contract 无 Host/UI leakage。
3. MineRewind 只引用 versioned Abstractions，能与 Host 并行独立构建并作为 v3 reference plugin。
4. App Config Schema 1 与 Artifact Ledger Schema 1 可分别原子、幂等迁移 legacy data；失败进入无写入 Recovery Center，配置重置不隐式删除 Artifact repository。
5. 四类插件身份、Artifact/strategy identities、FolderId、Provider State、typed settings、outcome diagnostics 全部落地。
6. Config Change Proposal 使用 revision/field ownership/impact/review/atomic commit，插件不能直接保存或静默执行破坏性变更。
7. Backup 使用 Policy/Scope → Consistency → staged primary Artifact → optional transform transaction → graph/history commit → retention/Cloud；允许的 fallback 持久记录 warning。
8. Committed Artifact immutable；dependency graph 可恢复、无环、hash-verified，middle delete/retention 不断链，Cloud 与 History 只观察 committed revision。
9. Restore owner/format/strategy missing fail-closed；完整 graph/integrity preflight 后才进入 Coordinator，Materializer 在隔离 workspace 运行，Host continuation once-only 并保留 Safe Restore/Restore Mode。
10. Draining 不撕掉 active operation；settings/update/state/Artifact transaction 可回滚并可从 process crash recovery。
11. MineRewind parity、Core regression、第三方 Artifact extension 三份 checklist 全绿后才删除 v2。
12. `.frplugin` 静态验证、路径/炸弹防护、staging、versioned known-good 和不执行安装代码全部完成。
13. Official Catalog 由独立 repo 校验并通过 GitHub Pages 提供确定 artifact + SHA-256。
14. Host 安装介质可离线迁移 legacy MineRewind，未知 v2 payload 保留在 quarantine。
15. Minecraft Enhanced Experience 使用数据驱动 Preset，不含 Core MineRewind repo/ID 业务特判（legacy mapping 例外）。
16. 普通 uninstall 保留数据，危险删除数据是独立二次确认操作；可恢复 History 的 Artifact 不被静默删除。
17. Host/MineRewind/Site/Catalog 文档、CI、build/test/package 和真实安装/更新回滚 E2E 全绿。
18. 本地 1.9.0 RC 完成；所有外部发布动作另获授权。

---

## 10. 明确不在 1.9.0 / API 3.0 范围

- 第三方 Catalog source、publisher signature/证书链/吊销/密钥轮换；
- plugin-to-plugin dependency/version solver；
- 进程外插件沙箱；
- 完整 BackupPlan Engine、插件替换 Core capture/history/retention/Cloud/encryption 的 Backup Engine takeover、直接写真实目标的 Restore Engine takeover；
- 通用 VSS cross-plugin middleware；
- 官方 MCA/SQLite/CAS Semantic Delta 实现、content-aware incremental capture、通用 graph rebase、Branch/Merge、2D/3D Minecraft Viewer、通用 Tool Provider UI；API 3.0 只提供 capture 后受控 Artifact Transformer/Restore Materializer seam 和 fake reference slice；
- 全面重写 KnotLink core event system；
- Ludusavi/Data Pack/通用游戏数据库的额外重构（现有 discovery 行为只做回归保护）。

---

## 最终架构目标

> **FolderRewind Core 不理解 Minecraft；FolderRewind 产品可以极度理解 Minecraft。插件提供领域语义与受控行为，Host 负责用户状态、执行顺序、冲突、安全、持久化和 UI。**

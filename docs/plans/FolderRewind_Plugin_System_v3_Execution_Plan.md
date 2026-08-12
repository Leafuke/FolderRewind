# FolderRewind Plugin System v3 — 1.9.0 冻结与执行计划

> 状态：已冻结 / 实施中（D0 于 2026-08-12 经用户批准）
>
> 计划版本：2026-08-12 / Revision 7
>
> 产品版本：FolderRewind 1.9.0、MineRewind 1.9.0
>
> Plugin API：3.0.0
>
> App Config Schema：1
>
> 目标仓库：`Leafuke/FolderRewind`、`Leafuke/FolderRewind-Plugin-Minecraft`、`Leafuke/FolderRewind-Site`、新建 `Leafuke/FolderRewind-Plugin-Catalog`
>
> 当前执行门：M2 实施完成，Gate 证据已记录，等待用户批准；未进入 M3。

本文件是 Plugin System v3 的唯一执行依据。它先作为受版本控制的 proposed specification 接受审阅；用户明确通过 D0 后，才可把状态改为“已冻结 / 实施中”并修改产品代码。实施中若发现本计划无法满足仓库事实，必须先修订本文件、说明影响并重新通过当前里程碑，禁止在代码中静默偏离。

---

## 0. 执行治理与授权边界

### 0.1 执行原则

1. **Host owns state and execution; plugins own semantics.** 插件不得获得 `ConfigService`、`BackupService`、ViewModel、WinUI 控件或其他 Host 内部可变对象的编译期访问。
2. **Discovery proposes; the user owns the resulting configuration.** 插件 Discovery/Config Augmentation 只能返回候选、草稿或 patch；`AutoCreateConfigs` 是用户明确开启后由 Host 执行的校验与原子提交策略，不是插件直接持久化配置的权限。
3. **Clean break runtime, lossless known-data migration.** 1.9.0 只运行 v3 API，不提供 v2 runtime compatibility layer；MineRewind 的已知数据和 enabled intent 必须迁移，未知 v2 代码包只隔离和保留，不执行、不删除。
4. **MineRewind parity is the v2 removal gate.** Discovery、自动补全、文件策略、世界详情、区域备份、热备份、热还原、玩家数据、命令/热键和 KnotLink 未全绿前，不得删除 v2。
5. **每个提交保持可构建、可测试、可审阅。** 两仓库短期可以在开发分支保留编译期过渡 adapter，但发布态不得存在 v2 compatibility path。
6. **不借 v3 重写备份引擎。** 本次建立 Operation Resolution 和标准扩展边界，不重写完整 BackupPlan、归档/增量算法、历史、Cloud 或 KnotLink 核心协议。
7. **破坏性动作先建恢复点。** 配置迁移、设置切换、插件更新、Provider State migration、版本指针切换必须有明确 prepare/commit/recovery 语义。
8. **用户可见行为本地化。** 状态、诊断、降级、阻止、信任、包错误、Preset 和 Recovery Center 同步更新中英文资源。

### 0.2 审阅门与外部发布

| 门 | 通过条件 | 通过后允许进入 |
|---|---|---|
| D0 文档冻结 | 本文件、`CONTEXT.md`、ADR 0002–0004 经用户批准 | M1 产品代码实施 |
| M1 安全基础 | migration/recovery/test seam 全绿 | M2 runtime 与持久化 |
| M2 Runtime Core | Abstractions/runtime/config model 集成全绿 | M3 垂直切片 |
| M3 API Freeze | fake plugin + MineRewind 四条最小 E2E 全绿 | 冻结并准备 API 3.0.0 |
| M4 Parity | Host regression + MineRewind parity 全绿 | 包、更新、Catalog、Preset |
| M5 Distribution | 安装/更新/离线迁移/Catalog/站点全绿 | 删除 v2 |
| M6 Release Candidate | 四仓库 DoD 全部满足 | 单独申请发布授权 |

每个门完成后必须暂停并提交：变更摘要、测试证据、已知风险、下一阶段计划。用户当前只授权本地修改、测试和里程碑内提交；push、PR、NuGet.org、GitHub Pages、Catalog 合并和正式 release 均需另行批准。

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

### 1.3 公共 Capability 与 Host Services

Plugin API 3.0 至少提供：

- Discovery、Config Augmentation；
- mandatory File Policy、Provider-owned Backup Scope；
- Backup Consistency Lease、Folder Metadata；
- Restore Coordinator；
- Plugin Command、KnotLink Integration；
- Provider State Migration。

Host Services 至少提供：只读 config query、backup request、restore request、history query、notification、KnotLink、Plugin DataStore、temporary storage、logging。Manifest 静态声明 requested services；Host 在执行代码前检查可用性，在安装/更新 UI 展示变化，但这不是 OS 权限或安全沙箱。Official、Community、Manual 使用同一 Plugin API，没有 Core 私有能力。

### 1.4 Operation Resolution 与结果模型

- Kind-scoped capability 只由 `ConfigKindRef.OwnerId` 精确路由，不扫描所有插件 claim owner；v3.0 不支持隐式 cross-plugin middleware。
- 每个 plugin-related operation 获取 Runtime Session Lease；operation cancellation token 与 plugin lifetime token 分离。
- Resolution 输出 `Ready`、`Degraded`、`Blocked` 和结构化 diagnostics；diagnostic 至少包含稳定 code、severity、owner/capability、localized arguments。
- Operation outcome 统一为 `Success`、`SuccessWithWarnings`、`NoChanges`、`Canceled`、`Failed`、`Blocked`。
- 插件 cleanup/finalization 失败不得篡改已完成的核心数据结果；archive 成功后的 cleanup 失败、restore data 成功后的 Rejoin 失败均为 `SuccessWithWarnings`。
- 新 Backup Run/History 记录 outcome、diagnostics 和 FolderId；旧记录继续允许以 path 解析和重命名关联。

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
- Activation 返回/提交的 patch 只能针对 Host 明确开放的 settings/provider state/config augmentation draft；Host 负责版本检查、冲突、去重、验证与原子保存。
- DataStore 用于大体量、可重建或插件私有数据；不参与 Host settings/provider state 事务，插件必须 crash-safe/versioned/cache-rebuildable。

### 2.3 静态 Schema 与 Manifest

- `settings.schema.json` 在禁用插件和 Safe Mode 下可读；根为 `{ schemaVersion: 1, settings: [...] }`，setting 至少包含唯一 `key` 与 `type`，可包含 `required`、`default`、`displayName`、`description`，enum 额外声明非空且唯一的 `enumValues`。
- v3.0 setting type 固定为 string、boolean、integer、multiline、folderPath、filePath、enum；Host 负责 default、required、type/enum validation。schema 未识别的旧/未来 value 原样保留并产生 warning，不因设置 UI 往返丢失。
- Manifest contract 静态声明 PluginId/version/API requirement/entry、Config Kind metadata、settings schema 相对路径和 requested Host Services；运行时不得用执行插件代码补充这些声明。
- Backup Scope 复用轻量 form schema；未知 `EditorHint` 回退基础控件。
- Manifest Config Kind metadata 至少包含 stable KindId、本地化 display、icon/description；metadata cache 允许插件缺失时展示 last-known identity。
- Requested Host Services 扩大时必须在更新前向用户展示；高影响服务变化不得静默自动更新。

---

## 3. 持久化、迁移与恢复

### 3.1 Config Schema 1

App Config 新增整数 `schemaVersion=1`；字段缺失视为 legacy v0，未来版本高于 Host 支持范围时不允许降级读取或保存。

Schema 1 至少包含：

- `BackupConfig.Kind = { OwnerId, KindId }`；
- `ManagedFolder.Id` 稳定 GUID；clone/template/history/UI 不得无意重新生成；
- Config/Folder `ProviderStates[StateOwnerId] = { SchemaVersion, Data }`；
- Plugin settings typed JSON、Enabled Intent；Installed/Runtime observation 不写回 intent；
- Provider-owned scope identity `{ OwnerId, ScopeId, Parameters }`；
- legacy preservation 容器，只读保存未知 ConfigType/ExtendedProperties 及 migration warning；
- Backup Run/History 的 outcome/diagnostics/FolderId。

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
| 未知 ConfigType/ExtendedProperties | Core default/可解析通用数据 + unresolved legacy container + warning，不静默丢弃 |

Host 只做结构映射，不解释 Minecraft 私有数据；MineRewind 在 activation staged migration 中将自己的 state 从 v0 升到正式 schema。Migration 必须 idempotent，成功后的 runtime 不长期双读 legacy 字段。

### 3.3 原子迁移算法

1. 以 read-only stream 读取原始文件和原始 bytes，区分 missing、malformed、unsupported-newer、legacy、current。
2. legacy 时先在同目录创建不可覆盖的 timestamped recovery copy，并 fsync。
3. 在内存构建 Schema 1；校验 ID 唯一性、Kind/Scope identity、JSON 类型、Provider State wrapper、路径和必填 invariant。
4. 写入同目录 temporary file，flush-to-disk；从 temporary file 重新 deserialize 并执行同一 validator。
5. 使用 Windows 原子 replace 切换，并保留最近一次 pre-migration backup；重新读取正式文件验证。
6. 任一步失败都保留原正式文件，清理临时文件并进入 Recovery Center；第二次启动不得重新生成 FolderId 或重复 state mapping。

Import Config 使用同一 parser/migration/validator，不允许绕过 schema gate。当前“反序列化失败后创建并保存默认配置”的行为必须在 M1 消除。

### 3.4 Recovery Center

Recovery Center 是受限启动状态，不是普通主界面。它禁止插件 DLL、配置保存、备份、还原、自动化和更新，仅允许：查看本地化诊断与文件位置、导出原始文件、列出并选择 recovery copy、重试 migration、显式二次确认后重置。用户关闭应用不修改任何配置文件。

---

## 4. 标准备份与还原语义

### 4.1 Backup

固定顺序：

`normalize invocation → resolve kind/runtime/readiness → mandatory FilePolicy → Provider Scope → user SourceScope/config filter intersection → validate effective selection → acquire Consistency Lease → difference detection → Core archive capture → release consistency → history/prune/cloud/notification`

- Effective selection 等价于 `SourceScope ∩ UserSelection ∩ MandatoryProviderSafetyPolicy`；任何层只能收窄，不能重新加入已排除文件。
- FilePolicy 只表达 correctness/safety 强制规则；一般优化建议不能借此覆盖用户选择。
- Scope/filter invalid 必须发生在 consistency side effect 前；Consistency Lease 覆盖 diff + archive capture，并在 capture 后尽早释放。
- Full Minecraft backup 在 MineRewind/KnotLink consistency 不可用时，Manual 和 Automation 均可 raw fallback；结果为 `SuccessWithWarnings`，更新成功时间且 Automation 不重试，diagnostic 持久显示“不保证应用一致性”。
- Provider-owned `selected-regions` 缺失或 invalid 必须 Block，绝不退化为 Full；`ConsistencyIntent=Require` 缺失也必须 Block。
- Core default 无插件路径的 Full/Smart/Overwrite、history、prune、cloud、encryption 行为保持不变。

### 4.2 Restore

固定顺序：

`normalize mode → resolve kind/runtime/readiness → resolve local/cloud chain → password → build chain → integrity verification → PRE-FLIGHT COMPLETE → enter Restore Coordinator → external Save & Exit/preparation → BackupBeforeRestore through normal v3 backup operation → Host mutation continuation once → player-state/finalization → Rejoin → UI/events`

- Config Kind owner missing/disabled/Failed 时一律 Block restore；restore 不继承 backup 的 raw fallback。
- Archive/cloud/password/chain/integrity 失败不得先触发 Minecraft Save & Exit。
- `BackupBeforeRestore` 在世界退出后执行；失败或取消时不得调用 mutation continuation，并由 Coordinator 尝试必要的 Rejoin/恢复。
- Host continuation 每 operation 最多一次；Coordinator 可以完成、阻止或失败，但不允许隐式 engine takeover。
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

#### M2 Gate 实施记录（2026-08-12，待用户批准）

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
- M3 未开始。只有用户明确批准本 Gate 后，才允许接入 fake plugin 与 MineRewind 四条最小 E2E，并在其全绿后冻结 API 3.0。

### M3 — 最小垂直切片与 API Freeze

10. `[Host] feat(plugin-operation): operation resolution and outcomes`
11. `[MineRewind] refactor(plugin-api): remove Host project reference and adopt manifest v3`
12. `[Host+MineRewind] test(plugin-v3): run four vertical slices`
    - Discovery candidate → Host draft/commit；
    - consistency source → diff/archive same source；
    - restore preflight → coordinator → continuation once；
    - command identity → Host backup/restore request。

**M3 Gate / API Freeze**：四条 fake + MineRewind E2E 全绿，公开 API review 无 Host/UI leakage，MineRewind 可在 Host build 并行期间独立 build。随后只允许兼容性 contract 修正，准备 NuGet 3.0.0 candidate；正式发布待授权。

### M4 — 完整 Host/MineRewind 迁移

13. `[Host] refactor(backup): integrate FilePolicy, Scope, and Consistency Lease`
14. `[Host] refactor(restore): coordinator around mutation continuation`
15. `[Host] feat(plugin-command): unify commands, hotkeys, and KnotLink integration`
16. `[MineRewind] refactor(discovery): migrate discovery, augmentation, file policy, metadata, region scope, and state migration`
    - `.minecraft`、直接 saves、`versions/*/saves`、server root 保持；plugin 只给 candidate/draft，Host AutoCreate policy 提交。
    - `session.lock`、Voxy、Distant Horizons 变为 runtime FilePolicy，不污染用户 filter。
17. `[MineRewind] refactor(backup): migrate snapshots and active commands`
18. `[MineRewind] refactor(restore): migrate hot restore and player preservation`
19. `[Host+MineRewind] test(plugin-v3): complete regression and parity gates`

**M4 Gate**：第 8 节两份 checklist 全绿；backup degrade/restore fail-closed 矩阵全绿；尚不删除 v2。

### M5 — Package、Update、Catalog 与 Preset

20. `[Host] feat(plugin-package): .frplugin validator and versioned install layout`
    - ZIP root 直接包含 manifest；先 canonical validate 全 entries 再写盘；限制 entry count、单项/总展开大小和压缩比；staging 后 commit。
    - `plugins/{PluginId}/versions/{SemVer}` + Host-owned ledger/current/previous pointer；binaries 与 settings/state/data 分离。
21. `[Host] feat(plugin-update): journaled known-good rollback`
    - Download/hash/static validate → stage → drain → candidate pointer → Activate/migrate → commit known-good；失败恢复 pointer/settings/state/runtime。
    - 启动时读取 transaction journal，将 prepare/committing 中断恢复到 last committed known-good。
22. `[Catalog] feat(catalog): schema, validation CI, and GitHub Pages production index`
    - 独立 repo；source entries 经 CI 生成 production JSON。字段至少包括 PluginId/version/channel/API/architecture/artifact URL/SHA-256/provenance/trust classification/requested services。
23. `[MineRewind] chore(release): build .frplugin and create catalog PR`
    - Artifact 不含 Abstractions DLL；release workflow 计算 SHA 并自动开 Catalog PR，Catalog CI 下载复验 manifest/hash。
24. `[Host] feat(plugin-store): official catalog and manual install`
    - 删除 StoreRepo；Catalog cache 离线不影响 installed plugin；Manual build 不被 official background update 覆盖。
25. `[Host] feat(plugin-upgrade): bundle MineRewind v3 for offline migration`
    - Host 安装介质携带固定官方 `.frplugin` + hash。检测 legacy MineRewind installed/enabled/data 时安装 v3 并保留 intent；新用户仅在选择 Preset 时安装且默认 Disabled。
    - 旧 flat v2 payload 移入可恢复 `legacy-quarantine`，不执行、不删除。
26. `[Host+Catalog] feat(plugin-preset): Minecraft Enhanced Experience`
    - 数据驱动有限 actions：install/enable plugin、Host feature/integration setup、notice；禁止脚本。
    - KnotLink external installer 使用固定 URL+SHA-256，下载和启动前显式确认；步骤结果可诊断，半成功不伪装整体成功。
27. `[Host] feat(plugin-uninstall): preserve-data and delete-data flows`
    - 默认仅删 code，保留 intent/settings/provider state/data；独立危险操作列出范围并二次确认后删除。

**M5 Gate**：malformed/ZipSlip/collision/bomb 无半安装；update crash/failure 恢复 known-good；离线 legacy MineRewind 升级；Catalog/Manifest/hash 一致；普通卸载保留数据；Site/Package/Catalog CI 全绿。

### M6 — Clean break 与发布候选

28. `[Host] refactor(plugin-v3): delete v2 runtime and Minecraft special cases`
    - 删除 v2 interfaces/hooks/takeover、runtime claim scan、PluginHostContext convenience API、string settings、ConfigType、PluginScopeId、IsMinecraftConfig、Plugins.Enabled、StoreRepo、MinHostVersion runtime/update 语义。
    - 仅保留一次性 legacy migration/quarantine code，不得可执行 v2 contract。
29. `[MineRewind] refactor(plugin-v3): delete v2 and Host-internal references`
30. `[Site] docs(plugin-v3): replace all 1.8 plugin development and Minecraft integration docs`
    - 同步中英文 architecture/API/settings/package/update/trust/migration/commands/KnotLink/MineRewind reference docs。
31. `[Host+MineRewind+Catalog+Site] test(release): validate 1.9.0 release candidate`
    - 完成真实安装 E2E：Host 1.9.0 → bundled/catalog `.frplugin` → enable → activation → discovery → backup → restore → failed update rollback。

**M6 Gate**：第 9 节 Definition of Done 全满足；生成本地 RC 证据，等待外部发布授权。

---

## 6. Package、安装、更新与 Catalog 规范

### 6.1 `.frplugin` 静态安全

- 扩展名 `.frplugin`，内部 ZIP，root 必须直接包含 manifest；不接受 v2 顶层 wrapper 作为 v3 包。
- PluginId 严格 reverse-domain lowercase，非法即拒绝，不 sanitize。
- SemVer 支持 prerelease；API/architecture/entry/schema/kind/scope/service IDs 在执行 DLL 前校验。
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
| Minecraft Kind owner missing/disabled/Failed，restore | `Blocked`，不允许 raw restore |
| Archive/password/chain/integrity preflight failure | 不发送 Save & Exit，不进入 Coordinator mutation |
| BackupBeforeRestore failure/cancel | 不调用 restore continuation；Coordinator 尝试恢复/Rejoin |
| Plugin Activate exception | Runtime Failed；Enabled Intent 不变；session registration/state patch rollback |
| Settings candidate activation failure | 恢复旧 settings/state，并用新旧实例规则恢复旧 runtime |
| Update candidate activation/migration failure | pointer/settings/state/runtime 回滚 previous known-good |
| Process crash during update | 下次启动 journal recovery 回到 last committed known-good |
| Backup archive 成功，snapshot cleanup 失败 | `SuccessWithWarnings` |
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
- [ ] Package path safety、catalog hash、offline bundled MineRewind、uninstall modes。
- [ ] MSI/MSIX x86/x64/ARM64 build/publish 不因 Abstractions/bundled package 回退。

### 8.3 可执行命令

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

Catalog CI 执行 schema validation、duplicate identity/version、SemVer/API/architecture、HTTPS URL、artifact download、SHA-256 和 Manifest cross-check。命令的最终脚本路径在 M5 建仓时写回本节。

---

## 9. Definition of Done

Plugin System v3 只有同时满足以下条件才完成：

1. FolderRewind production code 只存在 v3 runtime，不存在可执行 v2 compatibility layer。
2. `FolderRewind.Plugin.Abstractions` 3.0 可独立 build/test/pack，公共 contract 无 Host/UI leakage。
3. MineRewind 只引用 versioned Abstractions，能与 Host 并行独立构建并作为 v3 reference plugin。
4. App Config Schema 1 可原子、幂等迁移 legacy data；失败进入无写入 Recovery Center。
5. 四类身份、FolderId、Provider State、typed settings、outcome diagnostics 全部落地。
6. Backup 使用 Policy/Scope → Consistency → Diff → Archive；允许的 raw fallback 持久记录 warning。
7. Restore owner missing fail-closed；完整 preflight 后才进入 Coordinator，continuation once-only。
8. Draining 不撕掉 active operation；settings/update/state migration 可回滚并可从 process crash recovery。
9. MineRewind parity 与 Core regression 两份 checklist 全绿后才删除 v2。
10. `.frplugin` 静态验证、路径/炸弹防护、staging、versioned known-good 和不执行安装代码全部完成。
11. Official Catalog 由独立 repo 校验并通过 GitHub Pages 提供确定 artifact + SHA-256。
12. Host 安装介质可离线迁移 legacy MineRewind，未知 v2 payload 保留在 quarantine。
13. Minecraft Enhanced Experience 使用数据驱动 Preset，不含 Core MineRewind repo/ID 业务特判（legacy mapping 例外）。
14. 普通 uninstall 保留数据，危险删除数据是独立二次确认操作。
15. Host/MineRewind/Site/Catalog 文档、CI、build/test/package 和真实安装/更新回滚 E2E 全绿。
16. 本地 1.9.0 RC 完成；所有外部发布动作另获授权。

---

## 10. 明确不在 1.9.0 / API 3.0 范围

- 第三方 Catalog source、publisher signature/证书链/吊销/密钥轮换；
- plugin-to-plugin dependency/version solver；
- 进程外插件沙箱；
- 完整 BackupPlan Engine、自定义 Backup/Restore Engine takeover；
- 通用 VSS cross-plugin middleware；
- 2D/3D Minecraft Viewer、通用 Tool Provider UI、Semantic Diff、Branch/Merge；
- 全面重写 KnotLink core event system；
- Ludusavi/Data Pack/通用游戏数据库的额外重构（现有 discovery 行为只做回归保护）。

---

## 最终架构目标

> **FolderRewind Core 不理解 Minecraft；FolderRewind 产品可以极度理解 Minecraft。插件提供领域语义与受控行为，Host 负责用户状态、执行顺序、冲突、安全、持久化和 UI。**

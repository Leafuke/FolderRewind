# Branch Merge 实施进度（2026-09-18）

本文件记录已落地结果；设计范围和验收 invariant 仍以 [实施计划](FolderRewind_1.9.0_Branch_Merge_Implementation_Plan.md) 为准。

## 当前状态

计划步骤 1–14 的代码与文档实现已经接通。自动化验证覆盖核心图、文件算法、持久 Session、配置冻结、精确产物、联合事务恢复、插件 continuation/staging preparation 和 MineRewind 玩家数据 proposal。尚未执行计划第 13 节的破坏性手工验收，因此发布 Gate 仍保持关闭；不得把“自动化实现完成”表述为真实 Minecraft 世界验收通过。

## 已落地能力

### 基础与插件恢复边界（步骤 1–5）

- Configuration Checkpoint semantic parents、Workspace ancestry anchor、SourceVersion creation kind、Exact checkpoint admission、权威 Source boundary 和可靠 working-state 探测均已接入。
- Checkout/Restore/Merge 的最终阶段使用同一配置级 operation gate，并冻结影响路径和 boundary 的可变配置对象；gate 内重新读取 Workspace、Config signature 和 resolved bindings。
- Plugin API 为 3.4 / NuGet 3.4.0。配置级 coordinator 携带全部 affected folders、operation identity 和 Restore/Checkout/Merge kind；continuation once-only、drain 及 RecoveryRequired/CommittedRecoveryRequired 语义完整。
- 普通 Restore 在 Host gate 内调用 staging preparation。插件只读取锁定的 current/target view 并返回受限 proposal；Host 验证路径/容量/冲突后写入 staging。proposal 改变目标时 Workspace baseline 为 Derived。
- MineRewind 不再在 continuation 返回后写 live NBT，也不再用 VersionId/FolderId 全局字典传递一次性意图。`RestoreRequestOptions` 以 operation identity 传递保留玩家数据请求；准备失败降级为 warning。Checkout/Merge 不启用此策略。

### Merge 规划、文件算法与 Session（步骤 6–10）

- Checkpoint DAG 支持 No-op、FastForwardLike、唯一 best base、NoCommonBase 和 MultipleMergeBases；损坏 parent/cycle fail closed，不借用 Branch control DAG 或 representation dependency。
- roster 规则区分 absent 与 unknown，覆盖 add/add、delete/modify、boundary conflict、删除和复用。参与细粒度合并的输入必须满足 Exact admission。
- 通用 provider 使用内容摘要执行保守三方文件合并，覆盖单侧增改删、同结果、modify/delete、add/add、零字节、文件/目录前缀和 Windows 大小写结构冲突。首发无 Minecraft 特判。
- SQLite Merge Session 保存不可变 plan revisions、分页冲突、输入签名、resolution、人工导入产物、Apply intent 和 CAS revision。重算不会误复用旧 resolution；数据库/产物损坏时阻止 GC。
- 未完成 Session 的 Version roots 和 representation closure 已接入 retention、release、local-replica 删除；Committed/Abandoned 后才释放。
- 结果构建会固定完整结果树、复用可证明相等的现有 Version，否则生成无 BackupRun 的双 parent Merge Version和 self-contained Exact archive，并执行真实 materialization round-trip。metadata 从 merged staging 获取，普通 metadata provider 失败仅记录 warning。

### 联合事务、Apply 与 UI（步骤 11–14）

- Restore filesystem executor 与可选 History Pack intent 共用一个 journal。每个 Source 分别记录 planned/started/applied；pack 前失败只回滚已开始 Source，pack durable 后只完成 Workspace/catalog，不撤销 immutable facts。
- 启动恢复先于普通 mutation/GC。Session 在 Applying 状态保存 transaction/pack identity；恢复和重试不会生成第二个 Merge Update。
- Apply 顺序为：恢复检查 → 固定产物 → Dirty protection/SafetySnapshot → Config + Runtime gate → tips/config/bindings/worktree revalidation → filesystem apply → pack → catalog/workspace → cleanup。source Branch tip 不移动。
- 新目录使用事务所有权标记避免恢复误删外部创建的路径；逻辑结果校验显式排除该临时标记，提交后删除。
- 历史页已有配置级 Merge 入口、Session 新建/选择/恢复/重算/放弃、100 条分页、批量 ours/theirs、单文件人工导入、Base/Ours/Theirs 预览和 Apply 状态。Config/tip 漂移会把 Session 标为 Stale 并要求重算。
- ADR、Plugin v3 文档、M5 手工清单和 MineRewind README 已对齐首发边界。内置 MineRewind 1.9.2 包 SHA-256 为 `dbdffdeb8c67dbb9100c17c03758c4433500d95b6f9bfe41a5ee48aa47033cd8`。

## 仍需发布前人工验收

1. 按实施计划第 13 节在复制的通用目录和测试 Minecraft 世界执行最小手工验收；不得使用维护者正在运行的真实世界做故障注入。
2. 验证应用关闭重启后继续冲突 Session、旧 Session stale 拒绝、Dirty SafetySnapshot 可恢复、current-only Source 不丢失。
3. 验证第二个 Minecraft 世界活跃时仍进入配置级协调；多个活动世界整体阻断；`.mca` 双改只显示文件冲突。
4. 验证普通 Restore 玩家数据保留产生 Derived baseline，Merge/Checkout 没有 post-commit NBT 写回；RecoveryRequired 和 CommittedRecoveryRequired 都不自动 rejoin。

## 本轮自动验证

- Host 定向 Merge/联合事务测试通过 32 个。
- Host 普通 Restore preparation 与配置写入/operation gate 定向测试通过 14 个。
- Plugin Abstractions 契约测试通过 15 个；内置包固定 hash 与 Runtime 黑盒激活/协调测试通过 2 个。
- MineRewind Manifest 与玩家数据 staging preparation 定向测试通过 3 个。
- Host `FolderRewind.slnx` Debug 构建通过，0 warning / 0 error。
- MineRewind 1.9.2 Release `.frplugin` 已重新打包并同步到 Host 固定 hash 资产。

未运行无关测试工程或重复全量矩阵；最终提交前只再执行一次受影响契约/包校验和解决方案构建。

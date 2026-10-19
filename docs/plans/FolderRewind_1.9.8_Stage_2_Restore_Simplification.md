# FolderRewind 1.9.8 阶段二：还原简化、兼容性与性能收敛

状态：第二阶段实施中；自动化、硬件及性能验收均为 **待执行**。实施基线 `3bfc761`，分支 `codex/1.9.8-stage-2`。阶段一人工 UI 验收按用户要求本轮跳过，不计为通过，不阻止本阶段实施。

## 本轮复核与实施补充

- 身份查询与 ADS/硬链接策略耦合、过滤前扫描、Checkout 重复物化旧基线均由当前源码证实；安全快照仅凭统计复用旧版本属于可推导风险，不认定已经造成用户数据丢失。
- Checkout 保留按需保护：旧基线至多比较一次；一致证据须保持有效，不同或无法确定时创建恢复点。目标与旧基线相同时复用未修改的准备内容。
- Core 内部保护快照缺少可靠内容证据时完整捕获当前管理内容，绕过统计跳过和捕获缓存重建，按项目备份验证等级入库；普通备份 Full/Smart/Rolling 策略保持原样。
- `restore-cleanup.json` 已有阶段一格式，升级时仅作为准备清理记录读取；新格式区分放弃准备、已提交、已回滚，不能把准备清理当提交证明。
- 范围规则同时覆盖计划、读取租约、比较、插件 LockedView 和发布校验。Checkout 保留 Exact 语义，不引入普通还原保留补丁。
- 依靠当前一致证据跳过快照时不释放证据来强行目录切换；必要时采用 FileDelta，非空目录按文件及空目录动作退役。
- 分别统计目标准备、旧基线比较、快照及清理；无窗口 Host 验证走真实 BackupService 接线。性能增加 `3bfc761` 五次工程对照，不取代阶段三的两个正式基线。
- 本阶段不运行 UI 自动化；本轮人工 UI 验收跳过。正式发行的 UI、联动及渠道验收仍由阶段三承担。

以下为接受的原有边界；本节补充在有差异时优先。

初始基线及证据分类见[阶段一](FolderRewind_1.9.8_Stage_1_History_Recovery.md)。本阶段依赖阶段一的可读初始化、旧事务恢复、健康重算和三态比较边界；完整发布条件见[阶段三](FolderRewind_1.9.8_Stage_3_Release_Acceptance.md)。

## 1. 产品行为与必须保留的保护

本阶段追求更少的重复 I/O、更小的拒绝范围和可解释的恢复流程，不新增兼容后端或通用检测框架。

| 项目 | 确定行为 |
|---|---|
| 支持范围 | 本地/USB NTFS；配置根自身或祖先的常见 Junction、目录符号链接；应用及备份存储的正常根重定向 |
| 暂不扩展 | exFAT/FAT32、网络还原目标、任意重解析类型、管理树内部链接的通用跟随语义 |
| 文件语义 | 所选普通文件内容和目录结构；按路径替换，不保证 ADS、硬链接拓扑等完整文件系统保真 |
| 无关路径 | 边界外文件及其对象、数据流不受本次操作影响；范围外链接不能阻断整个还原 |
| 失败保护 | 修改前完成所需内容准备；执行中失败按持久意图回滚或完成；不接管陌生对象 |
| 默认选项 | 保留普通/快速还原各自的恢复点开关；安全快照采用项目的 BackupVerificationLevel |
| 实验能力 | Native 默认关闭；不改变其领域 codec、加密、闭包验证或资源上限以改善 Core 数字 |

必须保留归档路径安全、所需载荷完整性、凭据绑定、物理保护目录重叠、无覆盖发布、执行意图可靠落盘和崩溃恢复。不能以 UI 上的“可用”、时间戳相同或进程请求已接收替代这些事实。

## 2. 统一操作生命周期

```mermaid
flowchart LR
    A[选择与轻量检查] --> B[协调释放文件]
    B --> C[一次准备与领域补丁]
    C --> D[按设置创建安全快照]
    D --> E[固定正式执行计划]
    E --> F[修改目标并提交]
    F --> G[清理及后续联动]
```

快速还原证明无变化时在 C 后结束，不创建安全快照、不发布执行事务。关闭恢复点时跳过 D，不关闭事务回滚。

| 边界 | 是否可能已修改目标 | Workspace 约束 | 失败处理 |
|---|---|---|---|
| 选择、准备 | 否 | 准备材料不绑定旧 revision | 放弃可证明归属的准备内容；残留进入维护诊断 |
| 正式执行 | 是 | 以安全快照之后的状态固定 Expected/Desired | 根据计划、对象及提交依据恢复；不确定则保留材料并限制写入 |
| 已提交/已回滚后的清理 | 目标结果已确定 | 不要求未来 Workspace 恒等于旧 Desired/Expected | 持久登记待清理材料，后续仅处理原容器，不封锁正常新操作 |

### 2.1 锁和插件边界

- 前置轻量检查识别历史选择、凭据可用性、已知文件系统/路径问题、必需协调器和明显的容量问题。确定性拒绝尽量发生在要求游戏退出之前；不在此处全量解压或扫描世界。
- 插件完成保存退出及文件释放后，Host continuation 内取得现有配置级操作租约，覆盖恢复门禁、一次准备、安全快照、计划固定和持久提交。
- 内部快照、最终配置保护和恢复入口传入同一租约，复用已有 `existingOperation` / InsideOperation 路径，避免重入同一 SemaphoreSlim。
- 不在持有 History Mutation Gate 时回调等待游戏保存；不把游戏重新进入、通知或长时间维护放在核心操作锁内。
- 核心提交、回滚或“需要恢复”的结果先确定，再交给插件处理后续动作。不同配置沿用现有独立操作机制；文件绑定和占用保护仍须阻止实际目标冲突。

## 3. 实施单元

### S2-01：把准备材料从 Workspace 事务中拆出

主要落点：`HistoryRestorePreparationSession.cs`、`HistoryRestoreService.TargetVolume.cs`、`HistoryQuickRestoreExecution.cs`、`HistoryRestoreService.cs`、`HistoryCheckoutService.cs` 及 Host 编排接线（均位于 `FolderRewind` 现有 History/Services 下）。

采用以下最小持久边界：

1. **独立准备记录。** 在事务目录保存 `restore-preparation.json`，包含事务/配置身份、来源绑定、物理位置、容器归属 nonce 和容器身份。它不保存旧 ExpectedWorkspace，也不作为“目标变更待恢复”的标志。
2. **先记录位置，再创建容器。** 沿用排他创建和私有 ACL。取得容器身份后可靠登记，并保存归属标记。创建与登记之间中断而无法证明归属时，保留该目录及诊断；新操作采用新的事务目录，不接管旧名字。
3. **准备内不逐对象持久登记。** 解压、插件补丁、新目录等由私有准备容器归属覆盖。清理只在验证过的容器内进行，逐层不跟随链接，不能按目录名前缀递归删除。对应用准备目录不承诺保留用户手工放入的内容。
4. **一次准备后决定是否执行。** 保留当前已具备的一次物化、一次领域补丁及 Quick 复用能力；让普通还原和 Checkout 也共享这条路径。保留补丁导致 Derived/Partial 的语义。
5. **快照后才固定正式状态。** 按开关创建快照，读取其提交后的 Workspace；在同一操作租约中建立不可变计划，再可靠发布 v2 Prepared journal，之后才能执行第一步目标修改。新流程不依赖“准备先记旧 Workspace、随后 RebindExpected”。
6. **迁移责任。** 现有 v1/v2 日志继续读取；旧 Preparing 按阶段一恢复。新执行记录继续使用 v2 的既有计划/进度语义，新增准备及清理记录仅由 Host 管理，不升级公共数据格式。

不可变执行计划仍保存恢复必要的动作、受影响对象与父目录证据；不能把“准备不逐文件登记”扩大为“执行也不记录”。DirectorySwap 所需成员证据保留在计划内，避免再向准备日志反复写入同一棵树。

准备记录与新清理原语由一个 History 所有者管理。可以复用 Runtime 的私有目录、预算等基础能力，但不能让两个清理器共同接管同一个目录。

### S2-02：持久记录已确定结果，清理解耦

主要落点：`HistoryRestoreTransactionJournal.cs`、`HistoryRestoreFileSystemPlan.cs`、`HistoryRestoreService.TargetVolume.cs`。

执行期间继续以 Workspace 持久提交或 Merge 的 Pack 权威判定结果。只有验证了已发布结果或已完成回滚后，才建立独立的 `restore-cleanup.json`：绑定事务身份、不可变计划摘要、确定的结束方向和容器归属；记录依据必须来自实际观察到的持久结果，而非阶段名推测。

顺序固定为：**确认持久结果 → 保存清理记录 → 结束变更 journal → 尝试清理**。

- 清理记录与 journal 必须绑定同一事务和计划；中断在两者之间时，恢复须验证一致性后补全，不能把任意清理文件当作提交证明。
- 结束变更 journal 后，允许合法的新 Workspace revision。后续清理不再要求旧 Expected/Desired 与当前 Workspace 相等，也不再校验用户当前世界必须保持上次发布对象。
- 清理只访问登记容器中的旧材料，校验其容器和必要对象归属；不能删除或重置当前目标，也不能调用反向还原。
- 未知替换对象、无法访问的设备或回滚容器中的陌生成员继续保留，显示维护警告。它们不重新把已确定的文件结果降级为未知。
- 清理完成前保留清理记录、计划及所需材料，避免完成事务目录被另一回收流程提前删除；完成后幂等移除准备/清理记录。
- 清理失败返回“已提交但维护待处理”或“已回滚但维护待处理”。通知或游戏重进失败也不得使调用方再次执行文件还原。

已有 v2 执行事务如果没有足够的结束证明，仍按原证据恢复，不把阶段名、CleanupCommitted 或 Workspace 漂移单独当作任意放行理由。旧二进制不保证能处理 1.9.8 的未完成操作；降级不能替代本版本恢复。

### S2-03：身份查询与文件语义解耦

主要落点：`FolderRewind/History/Storage/WindowsRestoreFileSystem.cs`、`HistoryRestoreFileSystemPlan.cs`、`HistoryRestoreMutationPlanner.cs`。

| 对象/操作 | 规则 |
|---|---|
| 仅查询卷、对象 ID、类型 | 不枚举或拒绝 ADS，不因硬链接计数拒绝；实际访问/绑定错误仍报告 |
| 祖先、保护目录或无关文件有 ADS | 不因此拒绝还原，不修改其 ADS |
| 被替换/删除的普通文件有 ADS | 原对象退役到回滚位置；成功后采用归档普通内容，不把旧 ADS 拼到新文件 |
| Clean 目录切换，旧根有 ADS | 成功后的新根按文件级恢复承诺处理；失败回滚原目录及原 ADS |
| 目标中的硬链接 | 退役当前目录项，再发布新对象；不原位写入共享 inode，不承诺成功后维持链接拓扑 |
| 归档中的冒号路径、链接或越界条目 | 继续执行安全清单规则；允许目标已有 ADS 不等于允许归档写入任意命名流 |

计划当前含“所有对象 ID 全局唯一”的假定，需要按目录项位置检查调整：文件的不同硬链接可以有同一对象 ID，位置证明须结合路径/父身份和文件名；目录别名、重叠 Source 和位置冲突仍拒绝。不能仅删除入口处 Links 检查而保留不兼容的计划加载断言。

清理只读硬链接不得通过修改共享对象的 ReadOnly/ACL 来影响范围外链接。优先使用已验证的句柄删除语义；环境无法安全清理时保留为维护事项，不原位更改外部对象作为回退。

根 ACL、发布继承及已有属性处理维持当前可用行为，不扩展为完整 NTFS 克隆。不要把准备容器的私有权限带入用户目录。

### S2-04：统一物理根绑定

主要落点：`WindowsRestoreFileSystem.cs`、`WindowsDirectoryReadLease.cs`、`HistoryRestoreWorkspacePlanner.cs`，以及使用相同根绑定的受保护载荷读取入口。

为本次操作建立同一份逻辑路径—物理根绑定，按句柄解析最终路径及卷/目录身份。使用[GetFinalPathNameByHandleW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfinalpathnamebyhandlew)等现有 Windows 原语，正确处理本地扩展路径/卷 GUID 路径；不能仅以字符串以双反斜杠开头就把卷 GUID 当成网络路径。

- 允许配置根自身或祖先的常见 Junction/目录符号链接，解析结果必须是支持的本地 NTFS 目录。断链、循环、未知重解析类型及解析到网络卷明确拒绝。
- 记录并保护链接入口、必要祖先与实际父目录绑定；准备和执行共用该绑定，发布前重新确认。操作实际物理目录，保留配置中的链接入口，不把 Junction 本身换成普通目录。
- 保留配置路径、Source 身份与插件领域上下文中的逻辑信息。物理目录名不能替代游戏世界身份，不能为文件操作方便而改写历史描述或 KnotLink 的世界选择。
- 在实际物理父目录同卷创建准备容器；DirectorySwap 操作最终目录，FileDelta 操作其受管理路径。执行/恢复不能因逻辑盘符或别名变化写到另一位置。
- 应用、配置、History 和备份存储的保护关系按物理身份及祖先关系比较，支持 LocalState 等正常重定向。不同逻辑盘符不是跳过比较的依据。
- 受保护路径解析只承担比较职责，不借机要求 ADS 不存在。载荷、临时根或应用状态位于正常重定向下时，读租约也不能继续无条件拒绝全部链接祖先。
- 管理树内部的链接不自动跟随；受影响且尚不支持的对象须在目标修改前报告路径和角色。明确排除的子树按 S2-05 跳过。

Runtime 不反向引用 Host。Host 向 Runtime 工作区入口传递已绑定的实际位置并复用基础原语；不能通过全局关闭 Runtime 的重解析或私有目录限制实现兼容。

重绑定失败发生在修改之前时终止准备；已经修改后发现冲突则进入执行恢复，不能把新解析到的位置当成原目标。USB 设备重连按实际卷和对象验证；不能仅靠相同盘符判断是原设备。

### S2-05：按管理范围遍历

主要落点：`HistoryRestoreMutationPlanner.cs`、`HistoryRestoreFileSystemPlan.cs`、`FileSystemHistoryRestoreMutationBackend.cs` 的边界匹配能力、`FolderRewind/History/Storage/HistoryWorkingTreeReadLease.cs`、`HistoryExactWorkingStateProbe.cs`，以及 `RestorePreservePreparation.cs`。

- 遍历先判断路径角色、是否受本次操作影响及是否可能包含所需后代，再进行破坏性操作所需验证。
- 确定排除的整棵子树直接剪枝，不递归扫描、不枚举 ADS、不核对其子对象身份。
- 白名单中父目录可能不直接匹配，不能用 `include(parent) == false` 判断整棵树均排除。仅在现有规则语义能够证明没有受管理后代时剪枝。
- 目录/文件结构冲突如果包含范围外数据，不能为发布文件而退休整棵目录；在目标变更前明确拒绝该冲突。
- Preserve Paths、Whitelist 和玩家数据保留沿用现有语义。领域补丁由插件产生；Host 不解析 Minecraft 数据，不能把合成结果标为 Exact。
- 保留 DirectorySwap 与 FileDelta 的资格约束，不把带保留项或过滤语义的操作强行切成整根交换，也不退回整树复制。
- 读租约、内容比较与计划构造采用一致的遍历边界。只修改 mutation planner 不足以解决 Quick 在过滤前拒绝链接的问题；合并同一阶段文件/目录的重复枚举，保留提交前必要的重新观察，不能用旧枚举缓存掩盖外部新增内容。

### S2-06：验证复用、快速比较与容量

主要落点：`ArchiveRestoreVerificationContext.cs`、`SevenZipArchiveProcessBackend.cs`、`HistoryExactWorkingStateProbe.cs`、`BackupService.cs`、`BackupArchiveVerificationPolicy.cs`，以及既有空间预算原语。

验证政策：

| 操作 | 均衡 | 严格 |
|---|---|---|
| 新建备份/安全快照 | 按备份等级完成必要入库验证，不另加全内容测试 | 完整内容测试一次；复用同一受保护载荷的有效结果 |
| 实际还原 | 安全清单、存在的存储摘要及选中内容物化验证；不额外完整测试未选中数据 | 保留完整内容验证，避免对同一输入重复执行完整测试 |
| 用户主动 Deep | 真正执行完整验证并产生相应证据 | 同左 |

复用只限同一操作、同一受保护输入、相同表示/依赖、密码和验证政策。缓存命中不能只看大小、mtime 或文件名；保护解除、输入替换、凭据/依赖改变后失效。均衡证据不能标为 Deep/Verified 深度证明。

`CreateSafetySnapshotAsync` 不再在提交后无条件建立 Strict 上下文重做 Deep。把当前备份等级要求的验证在快照入库流程完成，保留其真实结果；快照建立失败且该操作要求快照时，目标修改不得开始。关闭快照不关闭失败回滚。

Quick 无变化路径先检查 Source、版本、边界和低成本结构差异；只有候选仍可能完全一致时才比较内容。逐项流式比较并遇差异尽早结束，避免先给两棵完整树计算所有摘要再判断。只有取得完整且仍有效的一致证据才返回 NoChanges；Unknown 不等于无变化。

容量政策：用已知清单/所选物化计划估计实际写入，按真实卷汇总多 Source 未完成预留，复用现有预算能力。不拿参数默认的 `0` 作为空间已验证的证明，不按最大配额预留小任务。准备写入完成后按预算生命周期释放未完成预留，已占用空间由真实可用空间反映，不能重复扣除。安全快照在其输出卷单独预算；同卷移动的原文件不虚构成一次复制开销。外部耗尽空间仍可能发生，失败须保留原目标并安全结束准备。

## 4. 回归矩阵

| 用例 | 必须验证 | 状态 |
|---|---|---|
| R2-01 | 祖先/保护目录/边界外文件有 ADS：正常还原，原无关对象及流不变 | 待执行 |
| R2-02 | 受影响文件和旧根有 ADS：成功采用备份内容，失败回滚原对象/流；拒绝危险归档条目 | 待执行 |
| R2-03 | LocalState Junction、备份根重定向、目标根/祖先 Junction 与目录符号链接：两种提交策略及两种模式正确，链接入口保留 | 待执行 |
| R2-04 | 通过别名发生真实保护目录重叠、嵌套 Source 或重复目标：修改前拒绝 | 待执行 |
| R2-05 | 循环、断链、未知 tag、准备后入口改变：不越界；修改后的冲突进入恢复 | 待执行 |
| R2-06 | 受影响硬链接指向管理范围外：成功/回滚内容正确，外部路径内容与属性不受原位修改；只读清理可安全处理或延后 | 待执行 |
| R2-07 | 边界外大目录及链接被剪枝；白名单后代、空目录、结构冲突、Preserve/玩家补丁、多 Source 仍正确 | 待执行 |
| R2-08 | 容器创建、归属登记、解压、补丁、构建计划任一点中断：准备可放弃，未知归属不删除，无永久写入阻断 | 待执行 |
| R2-09 | 快照提交后/Prepared 前强杀：新流程只留下准备材料，保留新 Workspace；旧记录仍通过 R1-04 | 待执行 |
| R2-10 | 第一/中间移动、64 动作边界、Workspace 或 Pack 提交前后、清理记录/结束日志之间强杀：恢复两次，结果一致，不丢原材料 | 待执行 |
| R2-11 | 备份/还原等级及普通/Quick 快照开关组合：快照等级准确、完整测试不重复、物化及补丁各一次，证据标签真实 | 待执行 |
| R2-12 | Quick 相同内容、首个/末个差异、大小/mtime 相同但字节变化、Unknown/取消：NoChanges 不误判，差异比较可提前结束 | 待执行 |
| R2-13 | 同配置备份与还原竞争、内部快照重用租约、配置变更：无死锁、无错误基线提交，重进等待不持有核心锁 | 待执行 |
| R2-14 | 多 Source 同卷预算、不同卷快照、准备中磁盘满、USB 暂时离线及重连：原数据保留，不把同盘符新设备当旧设备 | 待执行 |
| R2-15 | 提交/回滚后清理失败，再合法推进 Workspace，重启清理：旧清理不依赖旧 revision、不触及新世界 | 待执行 |

真实进程终止点要落在实际持久化边界，不以抛异常代替全部强杀测试。Junction/硬链接/ADS 使用真实 NTFS 集成测试；目录符号链接需要测试环境具备相应创建能力，缺少能力标为未验证，不把跳过算通过。

## 5. 验证命令与退出条件

从仓库根目录，在同一 PowerShell 进程中初始化工具，串行执行；初始化会准备真实 7-Zip、固定 rclone 与强杀探针。

```powershell
$ErrorActionPreference = 'Stop'
dotnet restore .\FolderRewind.slnx -p:Platform=x64
if ($LASTEXITCODE) { throw 'Restore failed.' }
& .\.github\scripts\Initialize-CoreTestEnvironment.ps1

dotnet test .\FolderRewind.Tests\FolderRewind.Tests.csproj -c Release --no-restore `
  --filter 'FullyQualifiedName~WindowsRestoreFileSystemTests|FullyQualifiedName~HistoryRestoreWorkspacePlannerTests|FullyQualifiedName~HistoryTargetVolumeRestoreTests|FullyQualifiedName~HistoryRestoreProtocolCrashTests|FullyQualifiedName~HistoryQuickRestoreExecutionTests|FullyQualifiedName~HistoryCheckoutServiceTests|FullyQualifiedName~ArchiveRestoreVerificationContextTests|FullyQualifiedName~BackupArchiveVerificationPolicyTests|FullyQualifiedName~RestorePreserve' `
  --logger trx --results-directory artifacts/1.9.8/stage-2/tests
if ($LASTEXITCODE) { throw 'Stage 2 tests failed.' }

dotnet build .\FolderRewind.slnx -c Debug -p:Platform=x64 `
  -p:GenerateAppxPackageOnBuild=false --no-restore
if ($LASTEXITCODE) { throw 'WinUI build failed.' }
```

新增准备/清理测试类补入筛选。将旧 ADS/硬链接一律拒绝的测试改为正向兼容与负向边界用例，不能只删断言。Native、Merge 和 Cloud 的相关回归继续使用原有真实工具。性能场景、五次采样与门槛统一按阶段三执行。

- [ ] 阶段一全部退出条件持续成立，R2-01～R2-15 取得实际结果。
- [ ] 新准备记录不绑定 Workspace；执行前意图和状态已可靠固定。
- [ ] 无待恢复事务时，浏览历史无物化、无整树世界读取、无 restore journal。
- [ ] 受支持还原单次物化、单次领域补丁；验证调用次数与用户选择一致。
- [ ] 物理根重定向可用；真实重叠、越界和不支持对象仍准确拒绝。
- [ ] 清理与未来 Workspace 解耦；强杀和重复恢复不损坏当前世界。
- [ ] 原文件回滚、只读/硬链接、过滤范围、加密及必要完整性保护未减弱。

## 6. 实际执行记录

| 单元 | 实际结果 | 证据 |
|---|---|---|
| 路径/ADS/硬链接/管理范围 | 定向 38 项通过、0 跳过；含真实目录符号链接；另 Checkout/范围组合 27 项通过 | `artifacts/1.9.8/stage-2/compatibility-final`、`scope` |
| 应用编译 | x64 Debug solution 通过；仅既有 MSTEST0037 警告 | `artifacts/1.9.8/stage-2/compatibility-build.log` |
| 初次验证异常 | 首个夹具提交 journal 遇到一次 Access denied；该 ADS 组单独复跑 4 项及后续完整定向 38 项通过，保留首次失败，不认定已定位其原因 | `artifacts/1.9.8/stage-2/compatibility`、`ads` |
| 准备/清理、统一编排、性能 | 待执行 | 后续补充 |
| UI、真实 USB 断连 | UI 按要求本轮跳过；真实 USB 待硬件验收 | 不计为通过 |

若实现发现无法满足上述边界，应记录具体不成立的保证并修订方案；不得以全树复制、全局拒绝特殊对象或静默关闭校验代替本阶段目标。

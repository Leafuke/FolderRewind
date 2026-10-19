# FolderRewind 1.9.8 阶段一：历史可用性与故障恢复

状态：实施规格已确定；代码实施、构建及功能验收均为 **待执行**。

规划日期：2026-10-19。初始 Host 基线：`1.9.x / 53975eb4d18d3b936bf13a15577ebcd755d94546`。实际实施时记录新的起始提交和工作区状态，保留已合入内容，Native 实验功能继续默认关闭。

本文件与[阶段二：还原简化](FolderRewind_1.9.8_Stage_2_Restore_Simplification.md)、[阶段三：发布验收](FolderRewind_1.9.8_Stage_3_Release_Acceptance.md)组成完整交付。按阶段推进；阶段验收不等于正式发布验收。

## 1. 目标、原则与证据边界

目标是使一个还原故障不再连带摧毁历史浏览能力，并让受影响用户能够通过应用内操作恢复正常备份。默认承诺所选文件内容、目录结构和失败恢复，不以完整 NTFS 元数据保真作为产品前提。

下列原则在三个阶段共同生效：

- Host 控制目标修改、历史提交、事务、完整性、加密、Retention 和 Cloud；插件负责领域语义与协调。
- 完好的不可变 Packs 是历史事实；SQLite 索引和捕获缓存可以重建；Workspace 是本机工作状态，不能用“最新备份”猜测重建。
- 备份载荷可用、当前内容一致、目标可执行还原是三个不同事实。轻量浏览不证明后两者。
- 能证明尚未修改目标的准备材料可以放弃；已经包含原文件的回滚材料按执行恢复协议保护。
- 用户数据、损坏现场和未知协议记录不通过删除、修改 JSON 阶段名或清除健康位来绕过。
- 本方案不新增插件公开契约成员，不升级 Packs、Workspace 或公开迁移包格式。

本地讨论材料为 `Reference/1.9.7更新后收到的问题反馈讨论汇总.txt`，受 Git 忽略规则限制。它记录多轮假设和纠正，只作为证据索引，其操作建议和历史授权不构成本次执行指令。关键结论摘要如下，其他环境无需依赖该本地文件。

### 1.1 反馈—根因—修改—回归对应表

“已证实缺陷”指基线源码中可以直接核对的机制，不表示已经取得每位用户的原始现场。用户级因果链不足时单独保留待确认状态。

| ID | 反馈与证据 | 分类及边界 | 实施归属 | 必须对应的用例 | 验收 |
|---|---|---|---|---|---|
| FR198-01 | Issue #63 日志报告 `alternate data streams`；身份查询无条件验证命名流 | 已证实缺陷：查询身份与 ADS 策略耦合。具体触发对象尚未确认；早期磁盘格式、容量推测已不能作为根因 | 阶段二：身份与元数据策略 | R2-01、R2-02 | 待执行 |
| FR198-02 | 受保护路径逐级查询祖先；讨论中的 LocalState 为 Junction | 已证实缺陷：正常受保护路径重定向可被拒绝。用户现场完整触发路径仍需回放确认 | 阶段二：物理根绑定 | R2-03～R2-05 | 待执行 |
| FR198-03 | `HistoryExactWorkingStateProbe` 在 `try` 外创建准备会话；Fast 分支规划调用内容探测 | 已证实缺陷：探测异常可逃逸；轻量评估承担物化、整树读取和事务准备成本 | 本阶段：读取与探测解耦 | R1-01～R1-03 | 待执行 |
| FR198-04 | v2 无计划 Preparing 恢复要求 Workspace 等于旧 Expected；Quick 在安全快照后才 Rebind | 已证实缺陷：恢复约束与合法快照更新冲突；特定用户是否在该窗口中断仍属可推导风险 | 本阶段：旧事务恢复；阶段二：消除新流程窗口 | R1-04～R1-07、R2-09 | 待执行 |
| FR198-05 | 计划构造和物化可先创建对象，后刷新 KnownObjects | 可推导风险：准备异常可能留下清理无法证明归属的对象，尚未取得对应用户现场 | 本阶段：不扩大故障；阶段二：容器归属 | R1-06、R2-08 | 待执行 |
| FR198-06 | 恢复异常可以终止 Runtime 初始化；健康重算依赖部分恢复方法返回有变化 | 已证实缺陷：异常传播范围过大；健康条件解除后仍阻断备份为可推导风险 | 本阶段：读取初始化、诊断及健康重算 | R1-01、R1-08～R1-10 | 待执行 |
| FR198-07 | Workspace 缺失、损坏、无法访问及事务异常共用宽泛报错 | 已证实缺陷：诊断混合；不能据此断言某次反馈必然是 Workspace 文件损坏 | 本阶段：分类诊断、明确修复入口 | R1-09～R1-12 | 待执行 |
| FR198-08 | KnotLink 先响应请求，再异步执行；重进世界还有独立结果 | 已有实现、待验收：Host/插件已有恢复待处理及重进警告语义；实际插件、模组版本和用户终态仍需核实 | 阶段三：最终组合联动 | R3-01～R3-04 | 待执行 |
| FR198-09 | 安全快照不论备份等级都追加 Strict/Deep 确认 | 已有实现、待验收：强制深测是原有策略，本轮按已确定政策调整；不能当作归档损坏证据 | 阶段二：验证证据复用 | R2-11 | 待执行 |
| FR198-10 | 历史迁移界面附加了 Runtime 恢复错误 | 可推导风险：迁移状态被其他故障掩盖。没有证据证明 1.8.2 归档已损坏 | 本阶段：诊断分离；阶段三：升级回归 | R1-10、R3-05 | 待执行 |

原始公开反馈：[FolderRewind Issue #63](https://github.com/Leafuke/FolderRewind/issues/63)。用户提供的截图、日志及事务副本如后续获得，应记录脱敏副本及其摘要，不存入公开仓库。

## 2. 代码落点与接口边界

| 子系统 | 现有落点 | 本阶段职责 |
|---|---|---|
| Runtime 生命周期 | `FolderRewind/History/Application/HistoryRuntime.cs`、`HistoryRuntimeManager.cs`、`NativeHistoryCoreGateway.cs` | 共享同一 Runtime，区分可读初始化与受控恢复，不另建一套历史缓存 |
| 事务与健康 | `FolderRewind/History/Application/HistoryRestoreService.cs`、`HistoryRestoreTransactionJournal.cs` | 收敛恢复入口、旧 Preparing 判定、健康重算与配置级诊断 |
| 工作状态探测 | `FolderRewind/History/Application/HistoryExactWorkingStateProbe.cs`、`HistoryCheckoutPlanner.cs` | 三态内容比较；Fast 不触发完整比对或创建准备会话 |
| Host 接线 | `FolderRewind/Services/NativeHistoryApplicationService.cs`、`BackupService.CaptureBaseline.cs`、`NativeHistoryConfigurationOperationGate.cs` | 按读写需求选择入口；执行时仍校验状态，不能依据 UI 上次评估跳过保护 |
| 本机状态修复 | `FolderRewind/History/LocalState/HistoryLocalStateStores.cs`、`HistoryLocalStateJournalRecovery.cs`、`HistoryWorkspace.cs` | 受控保全与替换；复用已有原子写入和本地状态恢复机制 |
| 归档关联与迁移 | `FolderRewind/History/Application/HistoryArchiveRecoveryService.cs`、`HistoryReplicaRecoveryService.cs`、`HistoryMigrationInitializationService.cs` | 保持独立诊断；复用现有关联恢复，不借迁移初始化覆盖损坏状态 |
| 界面 | `FolderRewind/ViewModels/HistoryPageViewModel.cs` 及其分部、`FolderRewind/Views/HistoryPage.xaml`、`FolderRewind/Services/HistoryLoadFailurePolicy.cs`、`MigrationHistoryStatusPolicy.cs` | 页面、评估、操作故障分别呈现；恢复入口复用现有布局和通知服务 |

需要增加的是 Host 层的可读入口、带原因的工作状态比较结果和本机状态修复操作。比较结果固定为 `Equal / Different / Unknown`；权限、占用、取消和不支持不能伪装成“已证明不同”。如保留 bool 过渡调用，Unknown 只能用于“尚未确认、需要保护”的保守决策。

不新增公共插件 API；不把恢复逻辑放进 ViewModel 或 XAML.cs；不新增通用健康评分或能力检测框架。

## 3. 实施顺序

### S1-01：建立可读初始化边界

1. 从 Runtime 初始化中分离 Packs 读取/验证及派生索引准备，与还原重放、链重写和其他维护操作。
2. 为页面、查询与只读预览接入可读入口，沿用同一 RuntimeManager 实例、索引协调与变更订阅。查询可以创建派生缓存，但不得隐式修改目标目录、重建 Workspace 或创建 restore journal。
3. 未完成 restore journal 的诊断挂在配置上，不能使已读取的有效历史全部不可见。不可读 Packs 或无效提交图仍如实报告仓库错误，不能通过吞异常伪装健康。
4. 实际备份、还原、Checkout、Merge、删除及 GC 继续经过已有操作锁和必要恢复门禁；不能以“Runtime 已可读”替代写入许可。
5. 导出到新目录只要求所选历史及其载荷具备足够的读取、完整性和凭据条件，不要求源世界的 Workspace 健康；导出不得接管现有目标。
6. 审计 Gateway 的失败缓存与初始化重试：恢复条件解除后应可重新评估，不能把首次失败永久缓存为不可用。

自动恢复与页面读取是独立调用。可确定、可安全完成的恢复可以在受控流程中执行；涉及目标变更的恢复必须取得对应操作保护及所需协调，不能由一次分支选择隐式发起。

### S1-02：收敛健康重算与诊断

1. 恢复入口在正确锁顺序内依次处理已有本地状态事务、Restore、Chain Rewrite 及已接入的 Native 恢复。
2. 在恢复尝试结束时重新观察实际状态，包括“没有 replay 工作”和部分恢复失败的路径。保留原始错误，不能由健康刷新异常覆盖主要失败。
3. 区分：目标变更待恢复、Workspace 缺失/损坏/不可访问、Replica Catalog 问题、迁移待完成，以及只影响维护的清理问题。
4. 诊断至少保留配置、事务（如有）、阶段、路径角色、原始异常/错误码和允许的下一步。主界面说明用户结果，技术详情可展开并沿用脱敏机制。
5. 某一配置的恢复故障不使其他配置失效；同一配置的危险写入仍阻断，不新增细到每个 Source 的并发许可体系。

### S1-03：修复旧 v2 Preparing 的恢复

对旧日志先识别协议与阶段，再判断是否需要 Workspace；不能在确认“仅准备”之前无条件要求 Workspace 可读取或与旧 Expected 相等。

允许放弃准备必须同时满足：

- 日志是有效 v2、阶段为 Preparing，未登记正式执行计划。
- 记录及目录内证据不存在已经执行目标操作的矛盾；存在执行进度、目标发布或其他冲突证据时不能仅凭计划字段为空推断安全。
- 清理对象的容器、路径边界和归属可以验证；清理不沿链接走到容器外。

满足条件时，保留当前 Workspace 和全部已提交 Packs，安全快照与期间合法提交均不回退。归属明确的准备材料可清理；归属不明的材料保留并登记维护诊断，不能仅因这种准备残留继续封锁项目。

无效 JSON、未知协议/阶段或可能涉及目标变更的矛盾记录，不转成 Complete、不猜测提交结果。历史读取仍按 S1-01 可用，相关写操作等待有证据的恢复。

原有 Prepared/Mutating 等执行事务继续按有效意图、物理对象及提交依据判定回滚或完成；不能扩大为“Workspace 不匹配都可忽略”。本阶段需要的残留归属及维护信息与阶段二共用实现，避免建立两套清理机制。

### S1-04：移除浏览中的完整内容探测

- Fast 规划保留历史图、Source 身份、边界及载荷可用性等必要元数据判断，不调用 DeepProbe，不解压世界、不全量计算世界摘要、不创建目标卷准备会话。
- “当前分支”按已提交工作状态显示，不声称当前磁盘内容实时完全一致。未做完整比对是正常状态，不显示错误横幅。
- 主动内容验证使用独立的只读物化工作区，生命周期内释放输入保护和临时数据，不复用具有目标变更门禁的准备事务。
- 将会话创建、物化和结果比较都放入适当的异常边界；Unknown 显示在验证操作或分支局部，不清空历史列表。
- 快速切换分支、离开页面及取消验证，应停止无效任务并释放资源；旧请求结果不能覆盖新选择。
- 真正执行 Checkout/还原时重新取得必要证据；不能把 Fast 的 Unknown 改为 Equal 来跳过保护。

### S1-05：提供明确的本机状态修复

入口复用历史页状态区域的 InfoBar/操作按钮；执行前用 ContentDialog 说明配置、保全位置及后续完整备份行为。业务操作放在 Host 服务，资源同步维护 `zh-CN` 和 `en-US`。

修复规则：

| 状态 | 处理 |
|---|---|
| 文件有效，仅旧健康位残留 | 重新计算状态，不重建 Workspace、不创建新分支 |
| 临时占用、权限或设备不可访问 | 提供重试及路径诊断，不当作丢失覆盖 |
| Workspace 确认缺失 | 明确确认后建立未知基线；原文件须通过实际打开/排他创建语义确认不存在 |
| Workspace 确认损坏 | 保全原始字节后受控替换，保留旧历史和其他有效本机文件 |
| Workspace 来自未知/更高格式版本 | 报告版本不兼容，使用兼容程序处理；不借损坏修复把未知格式降格 |
| 可能存在未解决的目标变更 | 先解决执行事务；禁止通过 Workspace 重建解除门禁 |
| Catalog 或迁移问题 | 单独呈现并进入相应恢复入口；Workspace 修复不顺带清空它们 |

不能仅用 `File.Exists == false` 授权重建：该结果可能掩盖访问错误。保存前还需校验确认对象没有改变，状态变化则重新诊断。

修复动作在配置操作锁及本机 Store 的同步边界内执行。当前 `HistoryWorkspaceStore.SaveAsync` 明确拒绝覆盖 Corrupt/Inaccessible，不能直接调用它并期待成功，也不能放宽普通 Save 的检查。为修复增加专用、受限的保全/替换路径：

1. 重新确认事务门禁、配置身份、Source 集合、文件状态及对象/字节证据。
2. 对损坏文件先以排他文件名保存原始字节并可靠落盘；保全失败则不替换。
3. 使用现有原子写入及本地状态恢复设施发布新状态；对 Missing 使用不接管现有对象的语义。中断后原始保全副本始终保留，重试可识别已完成结果。
4. 新 Workspace 保持原 ConfigId/SourceId；各 Source 的 BaseVersion 和分支续接锚点未知，Relation 为 Unknown，RequiresFullCapture 为 true。不能从时间戳选择旧分支。
5. 失效本配置可重建的捕获缓存和旧执行计划，保留相应恢复材料。下一次正常捕获通过已有分支创建流程分配新的分支身份并做完整备份，不能按名称复用旧分支；原有分支与 Packs 保持原样。修复动作本身不产生冒充完整备份的新 Pack。
6. 重算所有健康原因，返回实际修复结果；仍有 Catalog 等问题时继续说明，不把“Workspace 已修复”当成全部写入可用。

保全文件采用独立名称留在本地，不覆盖上一次修复材料，不纳入普通临时目录清理；提供打开保全位置的入口，不把原始状态写进普通通知或公共日志。

## 4. 回归用例与验证命令

优先扩展已有 `HistoryRuntimeTests`、`HistoryIndexAndLocalStateTests`、`HistoryCheckoutServiceTests`、`HistoryPresentationQueryTests`、`HistoryInteractionControllerTests`、`HistoryQuickRestoreExecutionTests`、`HistoryJointTransactionRecoveryTests` 和真实进程恢复夹具。新增用例验证可观察行为，不固定本次版本号、哈希或翻译字符串。

| 用例 | 场景及断言 | 状态 |
|---|---|---|
| R1-01 | 有效 Packs + 无法完成的 restore recovery：历史可读，受影响写入被限制，其他配置可用 | 待执行 |
| R1-02 | 同一历史配小/大世界：打开高级历史和切换分支均不物化、不读取世界内容、不创建 restore journal | 待执行 |
| R1-03 | 主动验证遇到 I/O/权限/取消：返回 Unknown 或取消状态，列表保留，句柄释放，无遗留目标事务 | 待执行 |
| R1-04 | 生成旧 v2 Preparing → 快照提交提高 revision → Rebind 前强杀：保留新 Workspace/快照/原世界，能够继续备份 | 待执行 |
| R1-05 | 上例以合法的其他提交或 Source 状态变化替代快照：不回滚合法状态；重复启动恢复幂等 | 待执行 |
| R1-06 | 创建容器或部分文件后强杀：可证明准备残留不阻断新操作；无法证明归属的目录不删除 | 待执行 |
| R1-07 | Preparing 与执行进度矛盾、未知协议/阶段、损坏日志：保留证据，禁止危险写入，历史仍可读 | 待执行 |
| R1-08 | 标志已设置但本次 Restore replay 返回无变化：在条件消失后恢复健康；真实待恢复原因不会被清除 | 待执行 |
| R1-09 | Workspace 真缺失、损坏、共享占用、拒绝访问分别诊断；访问失败不进入 Missing 覆盖路径 | 待执行 |
| R1-10 | Catalog、迁移与 Workspace 各自故障及组合故障：显示原因准确，修复一种不掩盖其余原因 | 待执行 |
| R1-11 | 明确修复损坏 Workspace：原始字节被保全，历史不变，新基线 Unknown，下一次完整捕获另起工作分支 | 待执行 |
| R1-12 | 修复确认后对象变化、保全失败、保全后/替换后中断：不误覆盖，重试可恢复，不能绕过执行事务 | 待执行 |

以下为实施后的命令，从仓库根目录执行。工具初始化可能下载现有固定依赖并生成测试产物；不读取用户现有世界作为测试输入。每条原生命令失败时停止，避免后续成功掩盖前面的失败。

```powershell
$ErrorActionPreference = 'Stop'
dotnet restore .\FolderRewind.slnx -p:Platform=x64
if ($LASTEXITCODE) { throw 'Restore failed.' }
& .\.github\scripts\Initialize-CoreTestEnvironment.ps1

dotnet test .\FolderRewind.Tests\FolderRewind.Tests.csproj -c Release --no-restore `
  --filter 'FullyQualifiedName~HistoryRuntimeTests|FullyQualifiedName~HistoryIndexAndLocalStateTests|FullyQualifiedName~HistoryCheckoutServiceTests|FullyQualifiedName~HistoryPresentationQueryTests|FullyQualifiedName~HistoryInteractionControllerTests|FullyQualifiedName~HistoryJointTransactionRecoveryTests|FullyQualifiedName~HistoryQuickRestoreExecutionTests|FullyQualifiedName~HistoryRestoreProtocolCrashTests' `
  --logger trx --results-directory artifacts/1.9.8/stage-1/tests
if ($LASTEXITCODE) { throw 'Stage 1 tests failed.' }

dotnet build .\FolderRewind.slnx -c Debug -p:Platform=x64 `
  -p:GenerateAppxPackageOnBuild=false --no-restore
if ($LASTEXITCODE) { throw 'WinUI build failed.' }
```

新增修复/探测测试类必须加入定向筛选，不能因不在上面的现有类列表而漏跑。强杀测试使用真实探针，缺少工具或 Inconclusive/Skipped 均不能作为阶段通过。按用户本次要求，第一阶段不运行 UI 自动化；读取、Unknown、重试、修复取消/确认及重启提供人工清单，未操作项标为待人工验收。

## 5. 阶段退出与失败处理

- [ ] FR198-03～FR198-07、FR198-10 的本阶段部分有可复现用例与实际结果。
- [ ] R1-01～R1-12 通过；强杀后至少执行两次恢复，确认幂等。
- [ ] 可读初始化不隐式修改目标；损坏 Packs 的真实错误仍能报告。
- [ ] 健康状态由实际条件重算；恢复入口、UI 与日志区分不同故障原因。
- [ ] 修复必须明确触发，保全先于替换，下一次完整备份不接错旧增量链。
- [ ] WinUI 构建及隔离 UI 验收完成，中英文资源齐备。

失败时保留原始日志、事务、保全副本和隔离夹具，记录失败阶段与实际目标状态。不得用删除 journal、禁用恢复门禁、清除 Health 位或重建仓库使测试变绿。

本阶段可以消除已有阻断并建立阶段二的边界；ADS、根重定向、准备生命周期和性能门槛全部通过前，不将 1.9.8 标为发布就绪。

执行记录：起始/结束提交、命令、TRX、UI 结果、未执行项及剩余问题均 **待填写**。本文件创建不代表其中任一功能已修复或测试已通过。

## 6. 本次执行安排

从 `53975eb4` 建立 `codex/1.9.8-stage-1`。用户已授权本地 Git 提交，不推送、不发布。
按文档基线、读取与诊断、旧准备恢复、轻量评估与显式比较、受控修复、验收记录分批提交；回归测试随对应行为修改进入提交。
执行期间保留第二、三阶段文档与边界。所有验收在取得实际证据前均为待执行，人工 UI 与代码验证分别记账。

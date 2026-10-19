# FolderRewind 1.9.8 第二阶段实施与验证记录

本报告对应[第二阶段方案](FolderRewind_1.9.8_Stage_2_Restore_Simplification.md)，实施基线为 `3bfc76108b73828b1b4811f9be3dad46cd0795e2`，分支为 `codex/1.9.8-stage-2`。仅在 Host 内实现，没有调整发行版本、插件公开契约、Packs 或 Workspace 格式，Native 实验功能保持默认关闭。本轮不推送、不发布。

状态：第二阶段代码退出条件已满足：服务自动化、应用编译、真实 Host 接线及双轮工程性能对照通过。UI 自动化未运行，人工 UI 按要求跳过，均不记为通过；硬件与正式发布验收留阶段三。

## 1. 问题、修改与证据

| 问题及分类 | 最终修改 | 回归与实际结果 |
|---|---|---|
| 已证实：身份查询普遍拒绝 ADS、硬链接和正常根重定向 | Identity 只返回卷/对象/类型；移动、读取与删除独立校验；物理根保留逻辑入口，保护链接及祖先 | 真实 NTFS 的 ADS、只读、硬链接、Junction/目录符号链接、重叠拒绝测试通过；最终全量及真实 Host 也通过 |
| 已证实：过滤前进入或拒绝范围外链接 | 统一 Managed / 必要祖先 / 确定排除三类；计划、读取租约、比较、LockedView、发布检查使用同一范围 | `HistoryRestoreCompatibilityTests`、保留服务及 Host 白名单/明确保留用例通过；排除链接和范围外对象保留 |
| 已证实：准备和清理被旧 Workspace 绑定 | `restore-preparation.json` 只保存准备位置/归属；快照后发布正式 v2 Prepared；清理格式 v2 区分准备/提交/回滚，保存交接后结束 journal | Workspace 推进后放弃准备/继续清理通过；阶段一清理记录只作为准备清理；未知原对象、异常进度和矛盾记录继续保留并阻断 |
| 已证实：输入锁与多层调用重复验证 | 单操作受保护输入上下文贯穿评估、锁及物化；证据在保护/凭据/依赖失效时失效 | Balanced/Strict 调用计数测试通过；显式 Deep 仍完整验证，均衡不标为 Deep |
| 已证实：Checkout 重复比较旧基线 | 目标准备一次；旧基线至多比较一次；同版本复用未打补丁的准备内容 | Equal / Different / Unknown、同版本零移动及 FileDelta 结构退役成功/回滚通过 |
| 可推导风险：内部恢复点可能复用相同统计信息的旧内容 | 内部 Core 保护强制完整捕获，绕过统计跳过和缓存重建；遵循项目备份验证等级，删除提交后无条件完整重验 | 真实 BackupService 的 Full/Smart/Rolling × 两档验证 × 缓存存在/缺失共 12 场景通过；同大小同时间戳修改保存在可导出的保护快照中 |
| 实施核对发现：保护快照仍可能经盘符入口读取其他位置 | 显式传递准备的物理绑定；直接捕获读实际位置，过滤/领域描述保留逻辑路径；快照及固定执行计划前重新确认入口 | 真实根重定向、逻辑绝对过滤及绑定错配拒绝通过；实际 USB 盘符交换仍待硬件验证 |
| 真实 Host 检出：Full/Smart 秒级归档名称可能相撞 | 三种普通模式复用既有重名处理，保留旧归档 | 第一次 Host 失败保留；后续保护与导出验收均验证旧载荷摘要不变。普通备份模式设置未改变 |
| 全量检出：删除后仍可见的 pending 目录项阻塞父目录清理 | 按身份打开并使用 DELETE / POSIX / IGNORE_READONLY；不修改共享硬链接属性或 ACL | 保留读取句柄的最小反例在修改前失败，修改后与 Merge/只读硬链接等 35 项通过；最终全量通过 |
| 已有结果语义需准确呈现 | 保留文件终态，维护/协调器后续警告单独保存；NoChanges 不伪造已提交，待恢复状态保持原值 | 新增结果映射、双语资源与已有服务回归通过；真实游戏重进留阶段三 |

未知对象、未知协议、异常进度或可能涉及原目标的矛盾不会按准备垃圾清理。访问失败的有效准备残留可靠交接维护，保留材料，权限恢复后可重试。普通保存和损坏状态修复沿用阶段一约束。

## 2. 代码落点与兼容边界

- 根绑定及对象操作：`History/Storage/WindowsPhysicalDirectoryLease.cs`、`WindowsDirectoryReadLease.cs`、`WindowsRestoreFileSystem.cs`。按句柄解析实际目录，支持两种已知根重解析类型；根入口及受保护路径保持固定。真实不受支持的 tag 环境尚未执行。
- 范围与比较：`HistoryRestoreTreeScope.cs`、`HistoryWorkingTreeReadLease.cs`、`HistoryExactWorkingStateProbe.cs`。先用路径集合、类型、大小排除一致，再比较字节；大小与时间戳不能证明一致。Equal 证据持有读取保护并在执行前验证。
- 准备/执行/清理：`HistoryRestorePreparationSession.cs`、`HistoryRestoreMaintenance.cs`、`HistoryRestoreTransactionJournal.cs`、`HistoryRestoreMutationPlanner.cs`、`HistoryRestoreService.TargetVolume.cs`。新准备格式 1 不含 Workspace；清理格式 2 含明确方向与计划/持久结果证明；原阶段一格式只按准备清理解释。旧 v1/v2 执行日志保留严格恢复。
- 编排：`HistoryCheckoutService.cs`、`HistoryQuickRestoreExecution.cs`、`NativeHistoryApplicationService*.cs`、`NativeHistoryRestoreOrchestrator.cs`。同一配置操作租约覆盖恢复、准备、内部快照及正式提交；核心 continuation 完成并释放资源后等待重进。
- 捕获与验证：`BackupInvocationOptions.cs`、`BackupService*.cs`、`ArchiveRestoreVerificationContext.cs`、`RepresentationRuntime.cs`、`SevenZipArchiveProcessBackend.cs`。内部强制捕获选项仅限 Host；普通 Full/Smart/Rolling 设置维持原策略。
- 容量：复用 `ArtifactSpaceBudget`，按实际物理卷累计尚未写出的物化需求及中间峰值；落盘后释放预留；插件补丁和快照输出分别预算。预算不是磁盘空间锁，运行中耗尽空间仍按失败保护处理。

成功替换不把旧 ADS 拼到归档内容上；失败回滚保留原对象及流。硬链接按目录项替换，不原位修改其共享对象。目录结构和管理边界保持原语义，不承诺完整 NTFS 保真。Windows 不支持安全删除或对象被替换时保留维护事项。

准备残留与执行回滚材料的职责不同。执行结束固定顺序为“验证持久结果 → 保存清理交接 → 完成 journal → 清理”；后台清理只处理登记容器，不核对未来 Workspace 或当前世界，不重复执行还原。

Windows 原语依据：[最终句柄路径](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfinalpathnamebyhandlew)、[句柄删除标志](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddk/ns-ntddk-_file_disposition_information_ex)。POSIX 标志用于及时解除目录项；已有读取句柄继续指向原数据，不需要修改其共享属性。

## 3. 实际验证

原始结果保存在本机仓库的 `artifacts/1.9.8/stage-2/`，该目录被忽略，未将大体积世界、归档或二进制纳入 Git。代码、测试、实施文档和小型汇总纳入版本控制。未执行项目不按通过统计。

| 验证 | 实际结果 | 原始路径（相对本阶段 artifacts） |
|---|---|---|
| Host 核心全量，含真实 7-Zip、进程终止探针、迁移/Cloud/Merge/Native 既有回归 | 1,324 通过，0 失败；5 个需显式启用的性能/真实世界入口跳过 | `core-complete-final/*.trx` |
| 之后新增循环及断链真实 Junction | 2 通过、0 跳过；未改变生产代码 | `disconnected-bindings/*.trx` |
| Plugin Runtime | 176 通过、0 跳过 | `runtime-final/*.trx` |
| 公开契约 | 15 通过、0 跳过 | `contracts-final/*.trx` |
| 新旧准备、维护及强杀定向组 | 96 通过；新版协议强杀 13 个窗口，每项恢复两次 | `protocol-final-guard/*.trx` |
| x64 Debug solution | 通过；仅既有 MSTEST0037 警告 | `final-solution-build.log` |
| MSI 无窗口 Host 工具 | 最终串行构建通过，0 警告 0 错误 | `smart-chain-host-build-serial.log`；测量用构建另见 `performance-candidate-build.log` |
| 实际生产服务接线，普通根 | 最终 22 场景通过，含实际六层 Smart 链 | `host-acceptance-serial-final/host-stage2.json` |
| 应用状态根整体 Junction | 同一 22 场景通过 | `serial-redirected-state-physical/run/host-stage2.json` |
| UI 自动化、人工 UI | 本轮未执行 / 跳过，不记为通过 | 阶段三仍需实际 UI 与联动验收 |

首次失败原始结果也保留：归档撞名见 `host-acceptance-01`；白名单测试误解原语义见 `host-acceptance-02`；清理 pending 见 `core-release-final` 和 `delete-pending-reproducer`；ACL 测试夹具错误请求 SACL 权限的失败见 `physical-snapshot-tests`。这些失败均有后续定向与最终通过记录，不从统计中删除后伪装为首次通过。最早一次 journal 提交的 Access denied 与后来的 pending 清理不是同一证据，未声称已经定位前者。

补充 Host 构建时曾提前启动验收，与 PRI 生成发生占用，失败日志见 `smart-chain-host-build.log`；该次运行不计正式通过。退出后等待串行构建成功，再重新运行普通根和 Junction 两组各 22 场景，正式结果如表。性能测量使用此前独立冻结目录，未与构建或测试并行。

真实 Smart 链用例由 BackupService 连续创建 Full + 五个 Smart delta，随后调用实际还原入口。计数为目标逻辑物化 1、owner 提取 2、严格测试 6 个独立输入各 1；新保护快照另完整捕获并严格测试 1 次，导出内容正确，全部旧载荷摘要不变。不能把依赖链的多个归档提取误判为重复准备。

复现顺序与命令见主实施文档及 `tools/performance/BackupRestore-README.md`。测试、构建、真实 Host 及性能均串行执行；没有运行 Release Setup 工作流。

## 4. 性能对照

两轮共 120 组、600 个样本全部通过文件结果及不可变载荷核对，基线/候选无无效组；单轮和连续双轮门槛均为 0 项。测量二进制保存在 `binaries/baseline` 和 `binaries/candidate`，不是正式发行候选。基线为 `3bfc761`，工具/诊断源码为 `4794e85`；候选业务提交为 `6782132`，并包含相同 I/O 适配。各 DLL/EXE/7-Zip SHA、环境、基线诊断补丁保存于 `baseline-manifest.json`、`candidate-manifest.json`、`performance-environment.json`、`baseline-diagnostics.patch`。[小型元数据记录](FolderRewind_1.9.8_Stage_2_Performance_Metadata.json)与[全部比较行 CSV](FolderRewind_1.9.8_Stage_2_Performance.csv)纳入 Git；完整样本、范围、阶段、计数及 I/O 位于本机 `artifacts/1.9.8/stage-2/performance-summary.json` 及 `performance/*/host-*.json`。

固定输入为 64 MiB 半随机/半零数据（seed 719）、1,000 个小文件和六层 Smart 链；两档验证、Full/Smart 及全部 Quick 开关/变化组合。每组五次、两轮，第二轮交换版本顺序。所有输入及状态放在同一 D: NTFS 卷，缓存未清空，不能声称冷启动、P95 或物理磁盘带宽。

旧版按秒命名，双方均在计时外等到前次操作完成的 UTC 秒结束，避免基线生成撞名的无效样本；等待不计入任何性能数字。无等待的同秒创建由功能用例单独验证。每次样本外核对不可变载荷 SHA，错误输出的组禁止参与性能比较。

文件可用耗时为 API 返回；总成本包括等待后台清理和日志排空。日志排空等待仅表示可观测等待，不等于全部日志 CPU 成本。OS I/O 为进程逻辑字节，含缓存；父 Host scope 计数不能与嵌套 scope 重复相加。归档调用及物化事件按实际发生位置统计，inclusive 阶段时间不相加。

门槛为同一项目连续两轮中位数退化同时超过 10% 和 20 ms。完整样本、中位数、范围、计数、I/O、日志及门槛判定由 `Summarize-StageTwo.ps1` 输出，不以延后清理隐藏成本。

以下为代表场景的五次中位数，单位 ms；Quick 行普通恢复点开关为关闭，独立 Quick 开关如行名。另一普通开关的全部组合也在 CSV 中，不以此表代替完整矩阵。

| 策略／场景 | 第一轮 API 原→新 | 第一轮总成本原→新 | 第二轮 API 原→新 | 第二轮总成本原→新 |
|---|---:|---:|---:|---:|
| 均衡／Full 备份 | 1549.6→1559.2 | 1549.7→1559.3 | 1564.4→1546.8 | 1564.8→1547.0 |
| 均衡／Smart 第五层备份 | 174.6→163.8 | 174.7→164.0 | 168.6→165.1 | 168.9→165.2 |
| 均衡／Quick 一致／保护关闭 | 1444.7→919.2 | 1452.2→1184.2 | 1467.0→893.9 | 1473.3→1139.7 |
| 均衡／Quick 不同／保护关闭 | 1762.1→1091.0 | 1765.6→1323.2 | 2119.6→1075.0 | 2125.6→1296.0 |
| 均衡／Quick 一致／保护开启 | 1245.1→910.7 | 1248.5→1170.1 | 1406.0→902.6 | 1410.5→1144.6 |
| 均衡／Quick 不同／保护开启 | 3506.0→2467.4 | 3509.4→2718.7 | 3912.6→2552.6 | 3919.9→2782.6 |
| 严格／Full 备份 | 1603.7→1612.5 | 1603.8→1612.6 | 1639.6→1774.8 | 1639.8→1775.1 |
| 严格／Smart 第五层备份 | 180.8→187.7 | 181.0→187.9 | 184.5→191.4 | 184.7→191.6 |
| 严格／Quick 一致／保护关闭 | 1522.0→988.6 | 1531.1→1234.8 | 1551.2→1054.8 | 1556.4→1321.0 |
| 严格／Quick 不同／保护关闭 | 2258.9→1136.7 | 2263.2→1381.7 | 2266.6→1235.0 | 2273.0→1459.8 |
| 严格／Quick 一致／保护开启 | 1534.8→1029.2 | 1542.6→1276.4 | 1575.1→1072.3 | 1583.3→1322.0 |
| 严格／Quick 不同／保护开启 | 3960.6→2769.8 | 3967.8→3001.8 | 4076.3→2853.4 | 4084.0→3097.5 |

Quick 总成本的改善两轮都存在，目录清理并未从成本中消失：例如均衡“不同／保护开启”第一轮，候选清理排空中位数为 241.6 ms，日志排空 0.06 ms，均计入总成本。普通备份并非每项都改善；严格 Full 第二轮总成本增加 135.3 ms（8.25%），未同时超过 10%，未达到约定回归门槛。其范围和每次样本仍保留。

同一代表场景第一轮的调用及持久化/I/O 中位数如下；缺少的旧版逻辑物化计数不解释成 0。Strict 的两次完整测试分别针对目标输入与新保护快照，没有重复测试同一受保护输入。

| 项目 | 均衡原→新 | 严格原→新 |
|---|---:|---:|
| 完整 `7z t` | 1→0 | 3→2 |
| `7z x` | 1→1 | 1→1 |
| 新版目标准备／逻辑物化 | 各 1 | 各 1 |
| journal 写入字节 | 1,973,557→10,311 | 1,964,942→10,281 |
| 不可变计划字节 | 614,935→722,107 | 610,656→717,823 |
| Host 逻辑读字节 | 174,596,746→5,236,682 | 174,442,093→5,233,565 |
| Host 逻辑写字节 | 3,089,760→1,238,915 | 3,078,728→1,236,299 |
| 7-Zip 逻辑读字节 | 134,340,866→100,742,925 | 201,517,866→167,922,098 |
| 7-Zip 逻辑写字节 | 101,277,837→101,094,803 | 101,278,961→101,096,362 |
| 用户日志字节 | 1,961→2,014 | 3,605→3,651 |

计划包含更充分的目录项及逻辑范围证据，因此计划本身增大；journal 不再反复包含整棵准备树。表中同时保留两项，不能只报告 journal 降幅。日志字节未下降；新增少量路径/维护信息的代价如实记录，未以关闭可靠刷新或日志取得结果。

## 5. 本轮边界与后续验收

代码完成与正式发布验收分别记录。实际 USB 断连/重连/盘符换设备、跨物理设备输出、真实耗尽磁盘、未知重解析提供程序仍待硬件环境；实际 MineRewind/MineBackup 玩家数据及重进、UI 双语呈现、Setup/Store 与 ARM64 均保留在阶段三。服务层故障注入及源码拒绝规则不替代这些实际验收。

损坏或未知执行证据继续保留现场。无法清理的已归属准备材料只成为维护事项；无法证明执行结果的旧执行事务继续阻断相关写入。第一阶段的修复保全材料和未知基线规则不变，修复缓存不会推断旧分支。

## 6. 提交记录

| 提交 | 目的 |
|---|---|
| `571e618` | 固化第二阶段修订、证据分类与验收边界 |
| `cb79e14` | 物理根绑定、ADS/硬链接和管理范围 |
| `7892c22` | 拆分准备/执行/清理及旧记录兼容 |
| `f48c7d7` | 受保护输入复用及实际物化空间预算 |
| `f606d1e` | 内部保护快照完整捕获及 Full/Smart 重名修正 |
| `6782132` | 统一租约与编排、三态比较证据、FileDelta/同版本路径、结果及维护诊断 |
| `4794e85` | 实际 Host 接线验收、I/O 诊断及五次性能汇总 |
| `21eac46` | 实际六层 Smart 链及双轮串行驱动 |

最终文档提交记录可由 `git log 3bfc761..codex/1.9.8-stage-2` 取得。基线临时 worktree 已归档并保留可恢复快照；测量二进制、准确诊断补丁、摘要及原始结果均保存在主工作区 artifacts 中，不依赖保留基线 checkout。没有推送或发布操作。

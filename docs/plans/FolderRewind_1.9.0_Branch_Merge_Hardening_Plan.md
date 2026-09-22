# Branch Merge 完整整改执行计划

日期：2026-09-22。实施依据为维护者确认的完整修复计划及 2026-09-22 实现评估。

Host 基线：688a1dac0f2773ad29b1b928a1453668b9146ffe。
MineRewind 基线：eea38e84c66a273b863fb36fb9207917f49a41cc。
两仓库使用 codex/branch-merge-hardening；仅本地提交，不 push、合并或发布。

## 固定决策

- 保留配置/Source semantic ancestry、Branch ownership 和单一联合事务；通用文件合并，不新增 Minecraft 语义 provider。
- Plugin API 3.4、MineRewind 1.9.2 和内置包不因测试修改升级。
- 复用 Version 的 Exact 副本失效时阻断，保留 resolutions，由用户显式重新准备，不自动补建历史表示。
- 最终 gate 内验证实际 Exact closure；锁定受控 payload 并验证 hash/manifest。
- durable pack 是提交事实；Session、缓存、清理、取消和 rejoin 失败不能否认提交。
- 锁顺序：Config operation gate → Runtime mutation/recovery gate → local-state store。
- Capture 前完成恢复检查；公开恢复取得配置门，已持门路径使用绑定配置的 typed lease。
- PreparedMerge 持久化 session/plan/resolution/provider identity、候选 facts/manifest、transaction/pack identity 和产物 ownership。
- 新最终 payload 使用 repository/payloads；pending intent 保护候选，catalog 接管已发布文件。
- coordinator 外完成压缩、验证、metadata；零写入不退出游戏；Snapshot 需要扩大 scope 时先明确协调全部保护目标。
- provider descriptor 固定 id/version/schema/policy；规范身份 id@version;schema=n。
- 只迁移完整 subject/boundary/B-O-T 内容/provider/policy 签名相同的 resolution；manual 重新验证。
- 云下载为用户显式动作，网络在 gate 外，本机发布在 gate 内。
- BCA 线性遍历；后台按 Source、500 条 keyset 分页；UI 100 条，批量 resolution 一个 CAS transaction。
- 不新增测试工程；损坏 Session/schema fail closed，不删除人工成果或 immutable facts。

## 提交顺序

| 编号 | 仓库 | 提交 |
| --- | --- | --- |
| C00 | Host | docs(merge): 记录完整整改计划与发布验收门槛 |
| C01 | Host | fix(history): 在配置操作门内协调恢复与备份准入 |
| C02 | Host | fix(merge): 保留持久提交结果并隔离提交后失败 |
| C03 | Host | fix(merge): 在提交前验证实际 Exact 表示闭包 |
| C04 | Host | fix(merge): 固定 provider 与 policy 身份 |
| C05 | Host | refactor(merge): 持久化准备结果并独立封存合并产物 |
| C06 | Host | fix(merge): 自动清理终态会话并保护已发布产物 |
| C07 | Host | fix(merge): 在环境协调前完成准备并缩小协调范围 |
| C08 | MineRewind | test(restore): 覆盖合并协调范围与恢复结果边界 |
| C09 | Host | feat(merge): 按完整输入签名保留重算成果 |
| C10 | Host | feat(merge): 支持显式准备所需云端副本 |
| C11 | Host | perf(merge): 优化共同祖先查询与冲突读取 |
| C12 | Host | fix(ui): 完善合并状态与操作反馈 |
| C13 | Host | test(merge): 补齐端到端故障与生命周期回归 |
| C14 | Host | docs(merge): 更新实现结果与自动化验收证据 |

每提交包含相关回归测试并通过定向检查；不提交红测试、临时探针、测试世界或构建产物。提交 body 记录问题和实际验证，不混入无关格式化。

## 验收

- R1：准备后删除/损坏/依赖失效，FF 与 ThreeWay 复用均零修改、零 Merge facts；替代 Exact replica 可用不误判 stale。
- R2：pack durable 后异常、Session UPDATE/读取失败、提交后取消、缓存和 cleanup/rejoin 错误均保留 committed 事实，恢复恰好一个 Update。
- R3：pending journal 不能进入 Capture；barrier 验证 recovery/Capture 互斥、跨配置独立、Snapshot 无重入死锁。
- 生命周期：成功/放弃/重启/中断清理，pending intent/catalog 交接，清理后可从 tip Exact Restore。
- 准备与协调：取消、重启、重复 Apply、resolution 变化、空 roster、零写入、PreserveCurrent、Snapshot scope 扩大及 scope 漂移。
- provider：非默认实现与 schema/policy 变化、越界/重复 proposal；重算只复用完整签名且人工文件有效的结果。
- 云准备：依赖优先、去重、取消、缺凭据、远端消失和 tip 漂移，不自动 Apply。
- 原语义：No-op/FF/BCA/criss-cross、source tip ownership、Dirty Snapshot、binding 和 current-only 不回归。
- 性能：1 万/5 万节点、朴素小图 oracle、节点边访问量；多 Source 共 10 万冲突、分页与批量事务，记录耗时/分配而非机器依赖阈值。
- 最终完整运行四个现有测试工程，Debug/Release x64 构建与 ARM64/x86 编译检查。缺工具链如实记录。

人工验收仅使用复制的目录与测试世界：两 Source 与 manual；Session 重启/stale；Snapshot 实际恢复；binding 修复与 resolution 保留；.mca 双改及多活动世界；零写入协调；普通 Restore 玩家保留 Derived；Session 清理后 Exact Restore。

## 发布 Gate

当前关闭。代码接通、自动测试、故障注入、人工验收分列记录；人工验收未完成不声称可发布。全部完成后才另行提交验收结论。代码回退前解决 pending journal，不能删除 durable pack 来撤销合并。

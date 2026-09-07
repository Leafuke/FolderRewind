# Branch Merge 实施进度（2026-09-07）

本文件记录已落地结果；设计范围仍以 [实施计划](FolderRewind_1.9.0_Branch_Merge_Implementation_Plan.md) 为准。

## 本轮审查与修复

审查起点：Host `bda9b6e`，MineRewind `baf5571`，两个工作区均干净。Host 历史中只有步骤 1–3 的提交，步骤 4 尚未提交。

- 从当前状态创建或捕获新 Branch 时保留已知的跨 BranchUpdate parent；原 Branch 的 local tip 不被消费。
- PartialSource 即使重新打包为自包含 Exact，也不能借由不可靠的 semantic parent 进入 Exact checkpoint。准入验证同 Config、同 Source、同 boundary 及声明的 Exact closure；不要求祖先 payload 永久保留在本机。
- 破坏性切换使用经过 Exact materialization 的文件内容与磁盘字节摘要比较，不再使用大小及时间戳作为相等证明。默认 Core planner 也执行探测，无法读取或出现链接时不判定 Exact。
- Checkout 在 Runtime gate 中重新读取 Host 权威 Config revision 和 resolved bindings，绑定变化时清理 staging 并拒绝 mutation。保护后再次探测当前文件。

## 步骤 4：已落地部分

- Plugin API / NuGet 版本为 3.3 / 3.3.0；公共 API 基线已更新。
- 配置级协调请求携带所有受影响 Folder、TargetIdentity、OperationId、Restore/Checkout/Merge 类型。
- continuation 在回调结束后关闭；已启动但未被插件 await 的操作由 Host 等待完成。失败处理保留 Host 的 RecoveryRequired 结果。
- `IRestoreStagingPreparationCapability`、只读输入及相对路径文件 proposal 契约、Runtime 注册和 Manifest capability 匹配检查已加入。
- proposal 整批验证 boundary、容量、重复/大小写/前缀冲突及 Windows 不安全路径，并复制插件内存。尚未接入普通 Restore 的实际 preparation 调用链。
- MineRewind 同步 batch 契约：扫描全部目标，协调唯一活动世界，多个活动世界整次阻断，mutation 前复查全部锁。RecoveryRequired / CommittedRecoveryRequired 不 rejoin。
- 内置 MineRewind 包和固定 hash 测试已同步重建。

## 尚未完成，不作为验收通过

1. 步骤 3 的 Config writer / reconciliation 与最终 Apply 仍需共用锁。当前 revalidation 修复了读取旧 bindings 的问题，尚不能消除检查后的并发配置写入窗口。通用目录外部 writer 的保护边界也需在联合事务中继续收紧。
2. 步骤 4 的受控 preparation 契约已有，Host 普通 Restore 调用、staged proposals 应用及 Derived baseline 尚未接线。
3. 步骤 5：普通 Restore 的玩家数据保留仍有 continuation 后 live NBT 写回及按 Version/Folder 缓存的 per-request intent，必须迁入 staging 并改为 operation identity。Checkout / Merge 已禁止启用此保留策略，但普通 Restore 的旧写回不满足最终 invariant。
4. 步骤 6–14 尚未实施；没有可供用户执行的 Branch Merge、Session、联合提交恢复或 Merge UI。
5. 首三步其他领域约束仍需随 Merge planner 验证补齐，例如 checkpoint boundary 不应省略后默认为 All、Merge provenance 及多 parent 角色验证。不得将当前审查当作全部发布 blocker 已解除。

## 验证

已运行现有 Host、Plugin Runtime、Plugin Abstractions、MineRewind 测试工程和 CONTRIBUTING 要求的 x64 Debug 解决方案构建。新增覆盖包括同长度且保留时间戳的文件修改、非 Exact semantic parent、最终绑定漂移、continuation 关闭与 drain、proposal 越界及冲突、第二个目标活动及恢复状态禁止 rejoin。

没有使用真实 Minecraft 世界进行破坏性测试。尚未进行 Merge 手工验收，因为 Merge 入口未实现。

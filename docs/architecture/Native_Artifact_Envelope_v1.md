# Native Artifact Full：P2 内部接线

## 权限边界

正常用户路径继续通过 `NativeHistoryArtifactTransformPolicy` 阻断现有策略字段。
只有 `NativeArtifactAcceptance=true` 的开发构建包含验收命令；运行时按 ConfigId 的 AsyncLocal scope 启用。
没有新增公共 Abstractions capability、enum、版本或用户设置。

`BackupService` 在 CoreFull 捕获和 Version Metadata 完成后调用 `NativeArtifactCaptureService`。
仅 Full 模式、FullSource、新捕获进入转换；Smart/Rolling/Partial 按失败策略处理；NoChanges/Reused 不补写表示。
Core archive 解出只读 primary view，经实际 Runtime capability lease 调用插件。
插件结果先封存，再经 Materializer 写 workspace，Host 用独立文件树摘要与 Core 对照验证。

## 存储

Native payload root：`<Destination>/.folderrewind/native-artifacts/<SHA256(ConfigId)>`。
它与 History metadata repository 分开，配置共享 Destination 时不会共用 GC 范围。
同一 root 使用进程内 semaphore 和跨进程排他文件句柄，保护 transform、import、read、GC 和更新校验。
Artifact DAG 仍使用 `FileArtifactLedgerStore`，未另建 Minecraft 增量链。

新物理节点是包含 `envelope.json` 与 `payload.7z` 的目录。
Envelope 版本 `1`，记录加密标记；7z 文件使用现有 Host 密码和工具，启用加密 header。
ledger 分别记录 logical tree hash/size 和 storage blob hash/size。
解封临时 lease 核对两套 hash；密码不传给插件。已提交目录不保留明文逻辑对象。
旧目录式逻辑 payload 的 Runtime 路径保持可读；P2 不迁移旧布局。

## 提交与恢复

Host 预留 VersionId；`SourceCaptureResult` 内部携带 Core candidate 和可选 Plugin candidate。
同一 Commit Pack 写一个 SourceVersion、两份 VersionRepresentation、Checkpoint、BranchUpdate、BackupRun。
两份表示复制 Host 的状态身份，不建立彼此的 materialization dependency。
`pairedCoreRepresentationId` 是 Host 内部保留关系。

Pending intent 保存 Version、Representation、Core payload、Artifact root、History pack/transaction identities。
阶段为 Prepared → PayloadsSealed → ArtifactGraphCommitted → HistoryPackCommitted → LocalStateApplied → Complete。
root 发布前先持久化 intent。History pack 是提交事实，journal 重放 local state，不重跑插件。
格式错误的 intent 阻断清理；正常失败不删除旧节点。
提交前崩溃留下的 Core archive 可能成为孤立文件：恢复将其记录在 native `orphans/`，不凭路径猜测并删除。
这是空间清理限制，不是成功 Version；新 Artifact orphan 由正常闭包 GC 回收。

## 消费者与生命周期

`NativePluginArtifactBackend` 验证 provider/格式/ownership/storage/逻辑树，实际物化后再核对提交时的文件树摘要。
默认同等级优先 CoreFull，指定 Plugin ID 的实验路径没有 Core fallback。
Restore/Checkout 继续使用 `HistoryRestoreService` 的事务；通用 Merge 使用相同输入物化，输出仍为 CoreFull。
读取期间持有 Artifact gate；插件调用持有 Runtime lifetime lease。
更新先 drain 旧 session，再获取 Host compatibility guard 检查保护格式，持有 storage gates 至 registry 发布。
插件缺失/停用不删除 History 和 Artifact；旧包不能通过更新或回滚绕过读取要求。

## Retention / Cloud

保留 Version 的两份表示都加入保护集合；pins、Safety Snapshot、Merge roots 沿用 History planner。
NativeArtifactRetention 在 Core 清理后处理 native catalog 与 DAG GC；pending capture/transfer 和 read lease 另行保护。
旧 Artifact GC 保护共享目标中其他配置的节点；不实现插件 compaction。

Cloud 继续使用真实 `HistoryReplicaSyncService` 与 rclone transport。
Native closure 作为一个不可变 ZIP 对象传输：依赖节点的密文 envelope 在前，root descriptor 最后。
完整对象上传及 SHA 验证完成才发布 Replica manifest/facts，避免暴露半个闭包；不上传可变 ledger。
这是一份自包含 closure bundle，不是每个 DAG 节点各建一个 Cloud Replica。
transfer identity 持久化；重试沿用同一 ReplicaId。导出规范化设备本地 transaction/availability，导入再导出保持字节一致。
下载先验证外层 bundle，再验证每个 storage blob、解密后的 logical tree、DAG 和 History root binding，最后登记 canonical local payload。
只允许用经过验证的传输修复本地缺失/损坏节点；身份冲突失败。

## 开发验收

`tools/Test-NativeArtifactFull.ps1` 驱动编译期隔离入口，真实安装并加载指定 hash 的 `.frplugin`。
所有 config/plugin/backup/remote 放入新建工作区；remote 为显式本地 rclone backend。
验收覆盖实际 BackupService、进程重启、指定插件表示事务还原、Checkout、SafetySnapshot、通用 Merge、Retention 和传输故障。
不能把生产 7z/Runtime/History 替换成直接调用插件来冒充端到端验收。

限制：双份存储，转换需要临时 seekable 文件视图，deep probe 会解封并验证；不承诺大世界低延迟。
进程 I/O 指标不含 7z/rclone 子进程；脚本磁盘峰值为 1 秒采样，包含隔离源副本与实验还原输出。
保持开发入口限制，不自动开启 P3 或公开新 Capture/Merge 协议。

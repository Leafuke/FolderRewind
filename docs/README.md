# Host 工程约束

本目录只维护需要与源码共同审查的长期约束。用户指南、开发教程、架构导览和安装说明见[网站](https://folderrewind.top/docs/intro)。

- [领域术语](domain.md)：概念、身份与职责含义。
- [架构决策](adr/)：仍有效的选择、取舍及替代关系；保留编号。
- [Minecraft 联动边界](adr/0004-minecraft-backup-degrades-restore-fails-closed.md)：Prefer／Require、运行级协调、恢复占用检查及兼容重置。
- [凭据存储隔离](adr/0010-credential-storage-isolation.md)：任务归属、旧快照兼容与显式新凭据恢复边界。
- [凭据存储与修订证据](specifications/credential-storage.md)：受管路径边界、静态认证任务隔离与有界修订账本。
- [历史完整性与恢复](specifications/history-integrity.md)：持久化权威、事务和恢复边界；Source 分支以 ADR 0007 为准。
- [产品消息协议 v1](specifications/product-messages-v1.md)：公告身份、适用范围、阅读状态与更新渠道边界。
- [Native Artifact Envelope v1](specifications/native-artifact-envelope-v1.md)：版本化数据格式、Full 受保护输入与临时空间生命周期。
- [受保护备份](specifications/protected-backups.md)：Pin、完整性与完成回执。
- [还原强保留](specifications/restore-preserve-paths.md)：范围、编码与暂存事务约束。
- [空间预览需求](specifications/spatial-preview-demand.md)：API 3.18 的视图身份、需求修订、共享工作、预算与关闭边界。

新增文档必须属于术语、有效决策或长期协议/安全约束，并加入本索引。规范变化应与实现和回归测试一同审查；ADR 仅解释选择，详细规范放 specifications。默认不提交计划、进度、评审、截图、验收报告和性能原始数据，这些内容写入 `artifacts/`。发布说明统一放 `.github/release-notes/`。过时内容直接删除，通过 Git 历史找回，不建立文档归档副本。

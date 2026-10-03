# FolderRewind 1.9.3

## 中文

本次发布汇总了自 1.8.2 以来的 1.9 系列更新，内置 MineRewind 1.9.5，插件开发 SDK 为 FolderRewind.Plugin.Abstractions 3.6.0（Plugin API 3.6）。

- 新历史系统：历史图谱、分支、检查点、Quick Restore、Checkout、保守文件级 Merge，以及 `.frhistory` 导入与导出。
- 旧版本迁移：增加 1.8.2 历史接管报告、来源定位和可重试恢复流程，保留插件启用意图，并加强恢复权限与缺失数据检查。
- 游戏存档发现：支持启动器扫描、Ludusavi 规则和 Minecraft 发现；分阶段确认配置和来源，保留再发现时的手工修改；流式下载与索引读取降低内存占用。
- 插件 API 3：公开契约与宿主实现分离，支持受控插件安装、升级和回滚，扩展历史、还原、发现与 KnotLink 能力。
- 内置 MineRewind 1.9.5：增加多启动器与基岩版存档发现、Minecraft 区域备份范围、玩家数据保留与当前世界命令协调。
- 改善异步配置保存、备份与云同步取消、单实例激活、任务栏图标、窗口和页面生命周期及中英文提示。
- 安装器提供中英文界面、安装范围和维护选项；卸载默认保留设置，可显式选择清理当前用户设置，备份保留。

### 下载与升级

选择操作系统对应的 x64 或 ARM64 Setup EXE；每个安装包提供 SHA-256。GitHub 本次仅发布 Setup EXE，不提供独立 MSI、MSIX 或侧载压缩包。旧版本未自动识别 Setup 时，请从本页手动下载。

升级前建议保留现有配置和备份。旧备份迁移与新历史的恢复权限取决于来源绑定和可用载荷；请查看应用中的迁移报告。Minecraft Merge 使用通用保守文件级合并，不提供 region/chunk/NBT 语义合并。

本次安装体验采用项目负责人的人工验收结论；历史安装诊断与未完成测试保留在仓库，不等同于所有场景已通过自动验收。安装包暂未进行生产代码签名。

## English

This release brings together the 1.9-series changes since 1.8.2. It bundles MineRewind 1.9.5 and uses FolderRewind.Plugin.Abstractions 3.6.0 (Plugin API 3.6).

- Adds a history graph, branches, checkpoints, Quick Restore, Checkout, conservative file-level Merge, and `.frhistory` import/export.
- Improves 1.8.2 history migration with takeover reports, source identification, retryable recovery, preserved plugin enablement intent, and payload/recovery checks.
- Adds launcher, Ludusavi and Minecraft discovery, staged configuration confirmation and preservation of manual rediscovery edits. Streaming manifest and index processing reduces memory use.
- Separates public Plugin API contracts from Host implementations and extends plugin installation, updates, rollback, history, restore, discovery and KnotLink integration.
- Bundles MineRewind 1.9.5 with additional launcher and Bedrock discovery, region backup scopes, player preservation and current-world command coordination.
- Improves asynchronous configuration saves, backup/cloud cancellation, single-instance activation, taskbar icons, lifecycle handling and localization.
- Provides Chinese/English Setup, installation scope and maintenance options. Uninstallation preserves settings by default; optional current-user settings cleanup preserves backups.

Choose the x64 or ARM64 Setup EXE for your operating system and verify its SHA-256. This GitHub release distributes Setup EXEs only. Older clients that do not recognize Setup assets should download them from this page.

Keep configuration and backup copies before upgrading. Migration and recovery depend on source bindings and available payloads; review the in-app migration report. Minecraft Merge uses conservative file-level merging, without region/chunk/NBT semantic merging.

Installer experience is accepted through the project owner's manual review. Historical diagnostic results remain recorded and do not certify every scenario. Production code signing is not included in this release.

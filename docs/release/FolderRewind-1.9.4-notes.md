官网与说明文档：https://folderrewind.top

建议优先使用 [Microsoft Store](https://apps.microsoft.com/detail/9nwsdgxdqws4) 下载本软件。

## 更新内容

这是一次次要更新，主要改进了历史备份清理、分支合并和日常运行性能，并修复了备份数量较多时的部分体验问题。

1. 改进了历史备份删除。删除增量链中的旧备份前可预览影响，并通过重建相关归档保留后续版本的恢复能力；重建过程中支持进度显示和中断恢复。
2. 改进了备份保留数量策略。每个备份源保留最近 N 个可恢复版本，受保护版本额外保留，设为 0 时无限保留。自动清理仅在能够净释放空间时执行。
3. 新增手动清理和清理报告。可在备份项目设置中立即清理，查看各来源的处理结果；也可选择优先达到保留数量，该选项可能增加磁盘占用。数量清理仅处理本地归档，保留历史记录和云端副本。
4. 改进了分支合并界面。新增完整合并工作页，支持文本文件双栏差异比较、冲突批量处理、应用前结果审阅和未完成会话恢复。合并仍以文件为单位，不提供 Minecraft region、chunk 或 NBT 语义合并。
5. 扩展了 KnotLink 备份联动。备份命令支持直接创建受保护备份，并可查询备份的重要标记；受保护创建要求完整、无过滤的单来源备份。
6. 修复了重建归档占用应用数据目录的问题。新归档写入配置的备份目录；旧位置的重建产物会在后续成功重建或清理时校验并迁移。重建可能需要临时磁盘空间。
7. 优化了历史列表加载和首页显示。历史按来源查询、分批加载，减少重复读取备份包；首页恢复列表虚拟化，减少重复刷新，并调整收藏文件夹布局。
8. 优化了长期运行时的资源开销。图片异步加载、日志批量处理，减少配置保存和路径匹配开销，并限制已完成备份任务的驻留数量。
9. 一些小问题修复，改善了合并任务状态提示、页面导航及中英文界面文案。

## 我要下载哪个？

本次 GitHub Release **仅发布 Setup EXE 安装程序**，不再提供独立 MSI、MSIX 或侧载压缩包。

一般 Intel / AMD Windows 设备请选择：

- `FolderRewind_1.9.4.0_Setup_x64.exe`

Windows on ARM 设备请选择：

- `FolderRewind_1.9.4.0_Setup_arm64.exe`

每个安装包均附带对应的 `.sha256` 校验文件。旧版若未自动识别 Setup 安装包，请在本页手动下载升级。安装包尚未进行生产代码签名。

---

## English

Official website and documentation: https://folderrewind.top

We recommend downloading FolderRewind from the [Microsoft Store](https://apps.microsoft.com/detail/9nwsdgxdqws4).

### What's new

This minor update improves history cleanup, branch merging and everyday performance, with fixes for managing larger backup collections.

1. Improved history deletion. Preview the impact before deleting older backups in an incremental chain. Required archives are reconstructed to keep later versions recoverable, with progress reporting and recovery after interruption.
2. Improved retention limits. Keep the latest N recoverable versions per source, plus protected versions. Set the limit to 0 for unlimited retention. Automatic cleanup runs only when it can reclaim space overall.
3. Added manual cleanup and cleanup reports in backup project settings. Review per-source results or choose to prioritize the retention count, which may increase disk usage. Retention cleanup affects local archives while preserving history records and cloud copies.
4. Improved branch merging with a dedicated workspace, side-by-side text comparison, bulk conflict decisions, review before applying changes and reopening unfinished sessions. Merging remains file-based, without Minecraft region, chunk or NBT semantic merging.
5. Extended KnotLink integration. Backup commands can create protected backups and query importance flags. Protected creation requires one complete, unfiltered source.
6. Fixed reconstructed archives occupying the app-data directory. New archives are written to the configured backup directory; older reconstruction outputs are verified and migrated during a subsequent successful reconstruction or cleanup. Reconstruction may require temporary disk space.
7. Improved history loading and the home page. Source-scoped queries and batched loading reduce repeated archive reads. The home page restores list virtualization, reduces redundant refreshes and updates the favorite-folder layout.
8. Reduced overhead during extended use with asynchronous image loading, batched logs, lower configuration-save and path-matching costs, and a limit on retained completed backup tasks.
9. Other small fixes to merge status messages, page navigation and Chinese/English interface text.

### Which download should I choose?

This GitHub Release provides **Setup EXE installers only**, without standalone MSI, MSIX or sideload archives.

For most Intel / AMD Windows devices:

- `FolderRewind_1.9.4.0_Setup_x64.exe`

For Windows on ARM devices:

- `FolderRewind_1.9.4.0_Setup_arm64.exe`

Each installer includes a corresponding `.sha256` checksum file. If an older version does not detect Setup installers automatically, download one from this page to upgrade manually. Production code signing is not included.

# FolderRewind 1.9.8

1.9.8 重点改善历史记录与还原体验：历史遇到异常时更容易查看和处理，还原过程减少重复工作，任务警告也更加清楚。修复了多项因内部检测导致还原/备份失败的问题。同时带来默认关闭的实验性语义备份功能。

## 更新内容

1. **历史异常时，仍可查看可用的备份。** 本机状态损坏或存在待恢复任务时，不再一并阻断历史浏览。页面会分别说明需要恢复、修复或清理的问题，并提供重试入口。
2. **新增“修复本机状态”和手动内容比对。** 对可修复的本机状态问题，先保全损坏材料，再重建工作状态，保留已有备份和分支。修复后的下一次备份会完整捕获，并建立新的分支身份。新增“比对当前文件与基线”，需要时再检查内容是否一致，浏览和切换分支时减少不必要的等待。
3. **优化还原与快速还原。** 减少同一次还原中的重复校验、解压准备和内容比对，改善还原耗时；同时减少无云同步记录时读取历史数据的开销。完善中断恢复与临时文件清理，清理尚未完成时会单独提示文件处理结果。
4. **加强还原前保护，改善文件兼容性。** 修复内部保护快照可能漏掉“大小和修改时间相同、实际内容已变化”的文件，以及短时间连续备份时归档重名的问题。改善目录重定向、硬链接、只读文件和 NTFS 附加数据流等场景的处理，修复部分文件占用误判，并提高 7-Zip 归档列表解析与还原验证的兼容性。（#63 @Hisiaaa）
5. **修复分支合并中的空文件夹处理。** 合并时正确处理空文件夹及目录结构冲突，最终审阅同时展示文件与文件夹数量。
6. **任务结果与提示更清楚。** 备份任务新增“已完成，有警告”状态，可在任务详情或日志中查看具体原因。细化世界占用、等待游戏保存退出超时、互联未响应及自动重进失败等提示，便于判断备份或还原是否完成，以及下一步如何处理。内置 MineRewind 更新至 **1.9.10**。
7. **新增实验性语义备份。** 可通过兼容插件，按文件内部的领域内容保存增量。MineRewind 当前支持 Minecraft Java 版存档。
8. **修复托盘初始化引发的效率模式问题。** 避免创建托盘图标时自动为整个应用启用效率模式，影响后台备份和还原任务的调度。

## 关于实验性语义备份

- **默认关闭，已有配置不会自动启用。** 支持完整来源的智能备份与完整备份，暂不支持滚动备份及选区／子集备份。
- 恢复语义备份和自包含检查点都需要兼容插件；“自包含”表示不再依赖之前的增量版本。
- 稀疏变化时可节省空间；大量内容变化或创建检查点时，耗时可能增加，也需要额外空间。
- 关闭功能后，已有语义版本仍然保留，下一次普通备份会建立完整文件基线。创建检查点不保证立即释放旧数据占用的空间。

## 下载与升级

- 一般 Intel / AMD Windows 设备：`FolderRewind_1.9.8.0_Setup_x64.exe`
- Windows on ARM 设备：`FolderRewind_1.9.8.0_Setup_arm64.exe`

---

1.9.8 focuses on history and restore improvements: history issues are easier to inspect and resolve, restores do less repetitive work, and task warnings are clearer. It also fixes several issues where internal checks caused backups or restores to fail, and introduces experimental semantic backup, disabled by default.

### What's new

1. **Keep browsing available backups when history needs attention.** Damaged local state or tasks awaiting recovery no longer block history browsing altogether. The page explains which issues require recovery, repair, or cleanup, and provides a retry option.
2. **New local state repair and manual content comparison.** For repairable local state issues, FolderRewind preserves the damaged data before rebuilding the working state, keeping existing backups and branches. The next backup after repair captures all content and establishes a new branch identity. Use “Compare current files with baseline” to check content when needed, with less unnecessary waiting when browsing or switching branches.
3. **Faster restores and quick restores.** Reduce repeated verification, extraction preparation, and content comparisons within a single restore. Also reduce history-reading overhead when there are no cloud sync records to process. Improve recovery after interruptions and temporary file cleanup, with file operation results reported separately when cleanup is still pending.
4. **Stronger protection before restoring and better file compatibility.** Fix internal protection snapshots potentially missing files whose contents changed while their size and modification time stayed the same, and archive name collisions when backups run close together. Improve handling of redirected directories, hard links, read-only files, and NTFS alternate data streams. Fix some false reports of files being in use, and improve compatibility when parsing 7-Zip archive listings and verifying restores. (#63 @Hisiaaa)
5. **Fix empty folder handling in branch merges.** Correctly handle empty folders and directory structure conflicts during merges. The final review now shows both file and folder counts.
6. **Clearer task results and messages.** Backup tasks can now show “Completed with warnings,” with details available in the task details or logs. More specific messages cover worlds in use, timeouts while waiting for the game to save and exit, unresponsive game integration, and failures to automatically rejoin a world. These help you tell whether a backup or restore completed and what to do next. The bundled MineRewind plugin is updated to **1.9.10**.
7. **New experimental semantic backup.** Compatible plugins can save incremental changes based on the domain-specific content within files. MineRewind currently supports Minecraft Java Edition worlds.
8. **Fix Efficiency mode being enabled during tray initialization.** Creating the tray icon no longer automatically enables Efficiency mode for the entire application, which could affect scheduling of background backup and restore tasks.

### About experimental semantic backup

- **Disabled by default; existing configurations are not automatically opted in.** Supports complete sources in Smart and Full backup modes. Rolling backups and region/subset backups are not currently supported.
- Restoring semantic backups and self-contained checkpoints requires a compatible plugin. “Self-contained” means the checkpoint no longer depends on earlier incremental versions.
- Sparse changes can save space. Extensive changes or checkpoint creation may take longer and require additional space.
- Turning the feature off keeps existing semantic versions. The next regular backup establishes a full file baseline. Creating a checkpoint does not guarantee that space occupied by older data is freed immediately.

### Download and upgrade

- Intel / AMD Windows devices: `FolderRewind_1.9.8.0_Setup_x64.exe`
- Windows on ARM devices: `FolderRewind_1.9.8.0_Setup_arm64.exe`

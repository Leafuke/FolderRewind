# Localization Text Optimization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Systematically rewrite all user-facing text in zh-CN and en-US resw files for clarity, consistency, professionalism, and WinUI 3 convention compliance.

**Architecture:** Two-pass approach — edit zh-CN first (primary locale), then en-US (secondary locale), applying a canonical terminology glossary throughout. Each task covers a logical functional area. Both resw files are XML with `<data name="Key"><value>Text</value>` structure.

**Tech Stack:** WinUI 3 `.resw` resource files (XML format)

**Spec:** `docs/superpowers/specs/2026-05-27-localization-text-optimization-design.md`

---

## File Structure

- Modify: `FolderRewind/Strings/zh-CN/Resources.resw` (~4700 lines, primary locale)
- Modify: `FolderRewind/Strings/en-US/Resources.resw` (~4700 lines, secondary locale)

No new files created. No code changes.

---

## Terminology Glossary (Reference)

| Concept | zh-CN | en-US |
|---|---|---|
| Clean restore | 安全还原 | Clean restore |
| Overwrite | 覆写 | Overwrite |
| Full backup | 全量备份 | Full backup |
| Smart/Incremental | 智能增量 | Smart incremental |
| Configuration | 配置 | Configuration |
| Restore (backup context) | 还原 | Restore |
| Metadata | 元数据 | Metadata |
| Proper nouns (rclone, 7-Zip, KnotLink, Mica, Acrylic, GitHub) | Keep English | Keep English |
| App name | 存档时光机 | FolderRewind |

---

## Task 1: zh-CN — Shell, Home, FolderManager, History

**Files:**
- Modify: `FolderRewind/Strings/zh-CN/Resources.resw` — keys from `ShellPage_*`, `HomePage_*`, `FolderManager_*`, `HistoryPage_*`, `History_*`, `BackupTasksPage_*`

- [ ] **Step 1: Apply terminology unification**

Replace all instances of inconsistent terminology in the target key range:

| Find | Replace | Example Key |
|---|---|---|
| `Clean 还原` | `安全还原` | `History_RestoreConfirm_Content` |
| `清洁还原` | `安全还原` | `CoreValidation_Step_CleanRestore` |
| `安全还原（Clean 模式）` | `安全还原` | `ConfigSettingsDialog_SafeRestore.Header` |
| `恢复` (in backup/restore context) | `还原` | `HistoryPage_RestoreButton`, `Notification_RestoreCompleted_Title` |
| `Smart 备份` | `智能增量备份` | `BackupService_RestoreMissingBaseFull_Content` |
| `Full 备份` | `全量备份` | (where applicable) |
| `metadata` (lowercase English) | `元数据` | `ShellPage_FolderConflict_Footer` |

- [ ] **Step 2: Fix awkward phrasing**

Specific rewrites:

`FolderManager_CommentPlaceholder`:
- Before: `例如: 修复了一处bug (选填)`
- After: `例如：修复了一处问题（选填）`

`FolderManager_BackupAll.Text`:
- Before: `一键备份所有`
- After: `备份全部文件夹`

`HistoryPage_CommentFilter.PlaceholderText`:
- Before: `按备注搜索（支持模糊匹配）`
- After: `按备注搜索`

`HistoryPage_Title.Text`:
- Before: `备份历史与恢复`
- After: `备份历史与还原`

`ShellPage_FolderConflict_Footer`:
- Before: `由于备份、metadata、云同步和还原仍按显示名定位，这些冲突会让不同文件夹共用同一槽位。`
- After: `由于备份、元数据、云同步和还原均按显示名定位，这些冲突会导致不同文件夹共用同一备份槽位。`

- [ ] **Step 3: Commit**

```bash
git add FolderRewind/Strings/zh-CN/Resources.resw
git commit -m "fix(l10n): zh-CN shell/home/folder/history terminology and phrasing"
```

---

## Task 2: zh-CN — Settings Page & Sponsor

**Files:**
- Modify: `FolderRewind/Strings/zh-CN/Resources.resw` — keys from `SettingsPage_*`, `Sponsor_*`, `CompletionSound_*`, `SponsorWindow_*`, `CloseDialog_*`

- [ ] **Step 1: Apply terminology unification**

| Find | Replace | Example Key |
|---|---|---|
| `已开启` (in ToggleSwitch OnContent) | `开启` | Multiple `*.OnContent` keys |
| `已关闭` (in ToggleSwitch OffContent) | `关闭` | Multiple `*.OffContent` keys |
| `Clean 还原` | `安全还原` | (if present) |

- [ ] **Step 2: Fix specific strings**

`SettingsPage_AboutDesc.Text`:
- Before: `© 2025 Leafuke。GPL 开源许可。`
- After: `© 2025-2026 Leafuke。GPL 开源许可。`

`SettingsPage_PresetsDesc.Text`:
- Before: `把常见场景的第一步集中在这里，少翻设置，少踩坑。`
- After: `常见场景的快速入门配置集中在此，帮助你更快上手。`

`SettingsPage_FontSizeDesc.Text`:
- Before: `12–20 号，影响文字区块`
- After: `12–20 pt，影响大部分界面文字`

`SettingsPage_SponsorLockedDesc.Text`:
- Before: `购买或恢复赞助者版本后即可使用这些外观小福利。`
- After: `购买或恢复赞助者版本后即可使用这些外观功能。`

`Sponsor_Status_Locked`:
- Before: `尚未购买赞助加载项；兑换促销码后可点击恢复购买。`
- After: `尚未购买赞助者版本；兑换促销码后可点击恢复购买。`

`SettingsPage_CustomCompletionSoundDesc.Text`:
- Before: `赞助者可以选择一个音频文件来替换默认音效`
- After: `赞助者可以自定义备份完成时的提示音效`

`CompletionSound_CustomLocked`:
- Before: `自定义完成音效是赞助者版本的小福利。`
- After: `自定义完成音效为赞助者版本专属功能。`

`SettingsPage_SilentStartupDesc.Text`:
- Before: `仅在开机自启时隐藏主界面，仅显示托盘图标`
- After: `开机自启时隐藏主界面，仅在系统托盘显示图标`

`SettingsPage_7zPathDesc.Text`:
- Before: `用于执行压缩的核心组件`
- After: `压缩和解压所必需的组件`

`SettingsPage_PluginsEnabledDesc.Text`:
- Before: `开启后可在插件商店下载并启用插件`
- After: `启用后可从插件商店下载和管理插件`

`SettingsPage_KnotLinkEnabledDesc.Text`:
- Before: `允许其他应用通过 KnotLink 协议远程控制备份任务`
- After: `允许其他应用通过 KnotLink 协议控制备份任务`

`SettingsPage_EnableNotificationsDesc.Text`:
- Before: `启用后将显示应用内通知、系统 Toast 和角标（如有）`
- After: `启用后将显示应用内通知和系统弹窗`

`SettingsPage_FileSizeWarningThresholdDesc.Text`:
- Before: `备份文件小于此大小时触发警告通知，设为 0 则禁用`
- After: `备份文件小于此阈值时触发警告，设为 0 可禁用`

`CloseDialog_Content`:
- Before: `你希望关闭窗口时执行什么操作？`
- After: `关闭窗口时执行什么操作？`

`SettingsPage_CloseBehaviorDesc.Text`:
- Before: `控制点击窗口右上角关闭按钮时的动作。`
- After: `设置点击窗口关闭按钮时的行为。`

`SettingsPage_DataMigrationDesc.Text`:
- Before: `导入或导出配置和历史记录，方便迁移`
- After: `导入或导出配置与历史记录，便于跨设备迁移`

- [ ] **Step 3: Commit**

```bash
git add FolderRewind/Strings/zh-CN/Resources.resw
git commit -m "fix(l10n): zh-CN settings/sponsor wording and ToggleSwitch labels"
```

---

## Task 3: zh-CN — ConfigSettingsDialog, BackupService, CoreValidation

**Files:**
- Modify: `FolderRewind/Strings/zh-CN/Resources.resw` — keys from `ConfigSettingsDialog_*`, `BackupService_*`, `CoreValidation_*`, `BackupConfig_*`, `AutoBackup_*`

- [ ] **Step 1: Apply terminology unification**

| Find | Replace | Example Key |
|---|---|---|
| `Clean 还原` | `安全还原` | Multiple keys |
| `清洁还原` | `安全还原` | `CoreValidation_Step_CleanRestore` |
| `覆写还原` | `覆写还原` (already correct) | `CoreValidation_Step_OverwriteRestore` |
| `覆盖` (in overwrite context) | `覆写` | `BackupService_Log_RestoreCleanFailedContinueOverwrite` |
| `Smart 备份` | `智能增量备份` | `BackupService_RestoreMissingBaseFull_Content` |
| `Full 备份` | `全量备份` | `BackupService_Log_SmartChainLimitReached` |
| `metadata` (English) | `元数据` | Multiple log keys |
| `已开启` (ToggleSwitch) | `开启` | Multiple `*.OnContent` |
| `已关闭` (ToggleSwitch) | `关闭` | Multiple `*.OffContent` |

- [ ] **Step 2: Fix specific strings**

`ConfigSettingsDialog_SafeRestore.Header`:
- Before: `安全还原（Clean 模式）`
- After: `安全还原`

`ConfigSettingsDialog_SafeRestore.OnContent`:
- Before: `已开启 - 失败时自动从同目录的 -Temp 副本回滚`
- After: `开启 — 失败时自动从临时副本回滚`

`ConfigSettingsDialog_SafeRestore.OffContent`:
- Before: `已关闭 - Clean 失败时可能丢失原文件`
- After: `关闭 — 还原失败时可能丢失原文件`

`ConfigSettingsDialog_VerifyArchiveBeforeRestore.OnContent`:
- Before: `已开启 - 先执行 7z test`
- After: `开启 — 还原前先校验备份完整性`

`ConfigSettingsDialog_VerifyArchiveBeforeRestore.OffContent`:
- Before: `已关闭 - 直接进入还原流程`
- After: `关闭 — 跳过校验直接还原`

`ConfigSettingsDialog_LowPriorityDesc.Text`:
- Before: `仅影响备份侧 7-Zip 进程，可减少备份期间对游戏和前台应用的抢占。默认关闭。`
- After: `仅影响备份时的 7-Zip 进程，可减少对游戏和前台应用的性能影响。`

`ConfigSettingsDialog_OverwriteWarning`:
- Before: `覆写模式只会更新和添加文件，不会删除归档里已经存在但源目录中已删除的旧文件。若你需要精确回到某个时间点，优先使用全量或智能增量并配合 Clean 还原。`
- After: `覆写模式只会更新和添加文件，不会删除归档中源目录已删除的旧文件。如需精确还原到某个时间点，建议使用全量或智能增量备份配合安全还原。`

`ConfigSettingsDialog_MaxSmartChainDesc.Text`:
- Before: `当连续增量备份达到此数量后，会自动执行一次全量备份以截断备份链，提高还原效率。仅在增量模式下有效。`
- After: `连续智能增量备份达到此次数后，将自动执行一次全量备份以截断备份链，提高还原效率。仅在智能增量模式下有效。`

`ConfigSettingsDialog_SafeDelete.OnContent`:
- Before: `已开启 - 删除旧备份时自动合并增量链`
- After: `开启 — 删除旧备份时自动合并增量链`

`ConfigSettingsDialog_SafeDelete.OffContent`:
- Before: `已关闭 - 直接删除（可能导致增量链断裂）`
- After: `关闭 — 直接删除（可能导致增量链断裂）`

`BackupService_RestoreMissingBaseFull_Content`:
- Before: `未能为目标 Smart 备份"{0}"找到可用的基础 Full，精确 Clean 还原已不可用。若继续，程序将改用兼容性倒序覆盖还原：从当前最新备份一路倒序覆盖到该 Smart 备份。这种方式能尽量回退文件版本，但不会删除多余旧文件。是否继续？`
- After: `未能找到智能增量备份"{0}"对应的基础全量备份，精确安全还原已不可用。继续将改用兼容模式：从最新备份倒序覆写到目标备份。此方式可回退文件版本，但不会删除多余旧文件。是否继续？`

`BackupService_Log_RestoreCleanFailedContinueOverwrite`:
- Before: `清理目录失败: {0}，尝试继续覆盖...`
- After: `清理目录失败：{0}，尝试继续覆写还原...`

`ConfigSettingsDialog_DestPath.Text`:
- Before: `备份存储位置 (目标路径)`
- After: `备份存储位置`

`ConfigSettingsDialog_Blacklist.Text`:
- Before: `黑名单 (排除的文件/文件夹)`
- After: `排除列表（不备份的文件/文件夹）`

`ConfigSettingsDialog_BackupWhitelist.Text`:
- Before: `白名单 (仅备份这些文件/文件夹)`
- After: `仅备份列表（只备份指定的文件/文件夹）`

`ConfigSettingsDialog_KeepCount.Header`:
- Before: `保留最近备份数 (0 为无限)`
- After: `保留最近备份数（0 为不限制）`

`ConfigSettingsDialog_CompressionLevel.Text`:
- Before: `压缩等级 (0-9)`
- After: `压缩等级（0-9）`

`ConfigSettingsDialog_CpuThreadsDescription`:
- Before: `0 = 自动，当前设备最高 {0} 线程`
- After: `0 表示自动，当前设备最高 {0} 线程`

`CoreValidation_Step_CleanRestore`:
- Before: `清洁还原校验`
- After: `安全还原校验`

`AutoBackup_Reason_AppStart`:
- Before: `程序启动时运行`
- After: `应用启动时运行`

`ConfigSettingsDialog_RunOnAppStart.Content`:
- Before: `程序启动时运行`
- After: `应用启动时运行`

- [ ] **Step 3: Commit**

```bash
git add FolderRewind/Strings/zh-CN/Resources.resw
git commit -m "fix(l10n): zh-CN config dialog/backup service terminology and phrasing"
```

---

## Task 4: zh-CN — Plugins, KnotLink, Cloud, Templates, Notifications, Misc

**Files:**
- Modify: `FolderRewind/Strings/zh-CN/Resources.resw` — keys from `Plugin*`, `KnotLink_*`, `CloudSync_*`, `Template*`, `OfficialTemplates_*`, `GitHub*`, `Notification_*`, `Hotkeys_*`, `MiniWindow_*`, `Encryption_*`, `Update_*`, `Picker_*`, `ProcessPathService_*`, `Tray_*`, `FirstLaunch_*`

- [ ] **Step 1: Apply terminology unification**

| Find | Replace | Example Key |
|---|---|---|
| `已开启` (ToggleSwitch) | `开启` | `ConfigSettingsDialog_CloudEnabled.OnContent` |
| `已关闭` (ToggleSwitch) | `关闭` | `ConfigSettingsDialog_CloudEnabled.OffContent` |
| `metadata` (English) | `元数据` | `CloudSync_Task_UploadingMetadata`, `CloudSync_Task_DownloadingMetadata` |
| `恢复` (backup context) | `还原` | `PluginService_BeforeRestoreFailed`, `PluginService_AfterRestoreFailed` |

- [ ] **Step 2: Fix specific strings**

`PluginStorePage_Desc.Text`:
- Before: `从 GitHub Release（latest）下载插件 zip。zip 根目录需包含 manifest.json。`
- After: `从 GitHub Release 下载插件压缩包，压缩包根目录需包含 manifest.json。`

`SettingsPage_ManualInstallPlugin.Content`:
- Before: `手动安装插件`
- After: `从 .zip 安装插件`

`Plugins_UninstallConfirm`:
- Before: `确定卸载插件：{0} ({1})？
提示：如果插件文件被占用，可能需要关闭应用后再卸载。`
- After: `确定卸载插件 {0}（{1}）吗？
如果插件文件被占用，可能需要关闭应用后再卸载。`

`KnotLink_Parse_ExpectedOption`:
- Before: `参数化指令只接受 -key=value，当前位置不是参数：{0}`
- After: `参数化指令只接受 -key=value 格式，遇到意外字符：{0}`

`SettingsPage_KnotLinkSendCustom_Placeholder`:
- Before: `请输入 event_data（将通过 OpenSocket 发送：SEND <event_data>）`
- After: `输入要发送的内容`

`CloudSync_Task_UploadingMetadata`:
- Before: `正在同步 metadata…`
- After: `正在同步元数据…`

`CloudSync_Task_DownloadingMetadata`:
- Before: `正在下载 metadata…`
- After: `正在下载元数据…`

`ConfigSettingsDialog_CloudSyncHistoryAfterUploadDesc.Text`:
- Before: `每次自动上传备份成功后，再额外合并上传当前配置的最新历史以及 active-history 清单，方便其他设备同步时避开那些已经在本地删除的旧云备份。`
- After: `自动上传备份成功后，同步上传当前配置的历史记录和活跃历史清单，便于其他设备识别已删除的旧备份。`

`Template_SyntaxHelp_Wildcard_Description`:
- Before: `{*} 会展开当前层级下的所有子文件夹。它既可以写在路径中间，也可以连续使用；如果放在最前面，会先遍历所有已就绪的盘符。`
- After: `{*} 展开当前层级下的所有子文件夹，可写在路径中间或连续使用。放在最前面时，会遍历所有可用盘符。`

`Encryption_PasswordWarning`:
- Before: `⚠ 密码一旦设置无法更改。如果丢失密码，加密的备份将无法还原，请妥善保管。`
- After: `密码一旦设置无法更改。丢失密码将无法还原加密备份，请妥善保管。`

`FirstLaunch_Content`:
- Before: `如果你不确定如何使用，可以观看 Bilibili 上的简要介绍视频。`
- After: `不确定如何开始？可以观看 Bilibili 上的介绍视频快速上手。`

`SettingsPage_DefaultBackupRootPathDesc.Text`:
- Before: `新建配置会自动使用"默认路径\配置名称"作为备份存储路径（可单独修改）`
- After: `新建配置自动使用此路径下的子目录作为备份存储位置，可单独修改`

`History_PartialCleanConfirm_Content`:
- Before: `该备份由白名单或区域备份生成，只包含部分文件。继续 Clean 还原可能删除目标文件夹中未包含在备份里的其他文件。确认继续？`
- After: `该备份由仅备份列表或区域备份生成，只包含部分文件。继续安全还原可能删除目标文件夹中未包含在备份里的其他文件。确认继续？`

`KnotLink_Error_PartialCleanRequiresConfirm`:
- Before: `目标备份是部分备份。若确实要 Clean 还原，请追加 -confirm_partial_clean=true。`
- After: `目标备份是部分备份。若确实要安全还原，请追加 -confirm_partial_clean=true。`

- [ ] **Step 3: Commit**

```bash
git add FolderRewind/Strings/zh-CN/Resources.resw
git commit -m "fix(l10n): zh-CN plugins/knotlink/cloud/templates/misc wording"
```

---

## Task 5: en-US — Shell, Home, FolderManager, History, Settings, Sponsor

**Files:**
- Modify: `FolderRewind/Strings/en-US/Resources.resw` — keys from `ShellPage_*`, `HomePage_*`, `FolderManager_*`, `HistoryPage_*`, `History_*`, `BackupTasksPage_*`, `SettingsPage_*`, `Sponsor_*`, `CompletionSound_*`, `SponsorWindow_*`, `CloseDialog_*`

- [ ] **Step 1: Apply terminology unification**

| Find | Replace | Example Key |
|---|---|---|
| `Clean restore` variations | `Clean restore` | Ensure consistency |
| `restore` (lowercase, in title context) | `Restore` | Sentence case for headers |
| `Configuration` vs `Config` | `Configuration` (formal), `config` (technical) | Context-dependent |

- [ ] **Step 2: Fix specific strings**

`SettingsPage_AboutDesc.Text`:
- Before: `© 2025 Leafuke. GPL License.`
- After: `© 2025-2026 Leafuke. GPL License.`

`Sponsor_Status_Locked`:
- Before: `The sponsor add-on is not purchased yet. After redeeming a promo code, use Restore purchase.`
- After: `Sponsor Edition is not purchased yet. After redeeming a promo code, use Restore purchase.`

`FolderManager_DuplicateDisplayName_Title`:
- Before: `This configuration already contains that folder name`
- After: `A folder with this name already exists in the configuration`

`FolderManager_DuplicateDisplayName_Content`:
- Before: `The configuration "{1}" already contains a folder named "{0}". Duplicate folder names are blocked so different directories do not share the same backup slot.`
- After: `The configuration "{1}" already contains a folder named "{0}". Duplicate names are not allowed to prevent different directories from sharing the same backup slot.`

`ShellPage_FolderConflict_Footer`:
- Before: `Because backup archives, metadata, cloud sync, and restore lookup still use display names, these conflicts can make different folders share one slot.`
- After: `Since backups, metadata, cloud sync, and restore all use display names, these conflicts can cause different folders to share the same backup slot.`

`SettingsPage_PresetsDesc.Text`:
- Before: `Common first-run setups live here so users can configure less and start sooner.`
- After: `Quick-start configurations for common scenarios, so you can get started faster.`

`ConfigSettingsDialog_SafeRestore.Header`:
- Before: `Safe restore (Clean mode)`
- After: `Safe restore`

`ConfigSettingsDialog_SafeRestore.OnContent`:
- Before: `On - auto rollback from a same-folder -Temp copy on failure`
- After: `On — auto-rollback from a temporary copy on failure`

`ConfigSettingsDialog_SafeRestore.OffContent`:
- Before: `Off - Clean failure may lose original files`
- After: `Off — restore failure may lose original files`

`ConfigSettingsDialog_VerifyArchiveBeforeRestore.OnContent`:
- Before: `On - run 7z test first`
- After: `On — verify backup integrity before restoring`

`ConfigSettingsDialog_VerifyArchiveBeforeRestore.OffContent`:
- Before: `Off - restore directly`
- After: `Off — skip verification`

`ConfigSettingsDialog_LowPriorityDesc.Text`:
- Before: `Only affects backup-side 7-Zip processes and can reduce stutter in games and foreground apps during backup. Off by default.`
- After: `Only affects 7-Zip processes during backup. Reduces performance impact on games and other apps.`

`ConfigSettingsDialog_OverwriteWarning`:
- Before: `Overwrite mode only updates and adds files. It does not remove stale files that still exist inside the archive after they were deleted from the source folder. If you need an exact point-in-time restore, prefer Full or Smart backups with Clean restore.`
- After: `Overwrite mode only updates and adds files — it does not remove files deleted from the source folder. For exact point-in-time restore, use Full or Smart incremental backup with Clean restore.`

`SettingsPage_7zPathDesc.Text`:
- Before: `Required to run compression`
- After: `Required for backup compression and extraction`

`SettingsPage_SilentStartupDesc.Text`:
- Before: `When auto-started at sign-in, hide the main window and show tray icon only`
- After: `Hide the main window at sign-in and show only the system tray icon`

`SettingsPage_PluginsEnabledDesc.Text`:
- Before: `Enable to download and use plugins from the store`
- After: `Download and manage plugins from the plugin store`

`SettingsPage_EnableNotificationsDesc.Text`:
- Before: `Enable to download and use plugins from the store`
- After: `Show in-app notifications and system alerts`

`SettingsPage_FileSizeWarningThresholdDesc.Text`:
- Before: `Backup files smaller than this size trigger a warning notification. Set to 0 to disable.`
- After: `Trigger a warning when backup files are smaller than this threshold. Set to 0 to disable.`

`CloseDialog_Content`:
- Before: `What would you like to do when closing the window?`
- After: `What should happen when you close the window?`

`SettingsPage_CloseBehaviorDesc.Text`:
- Before: `Control the action when clicking the window close button.`
- After: `Choose what happens when you click the close button.`

`SettingsPage_DataMigrationDesc.Text`:
- Before: `Import or export configuration and history records for easy migration`
- After: `Import or export configurations and history for migration across devices`

`ConfigSettingsDialog_KeepCount.Header`:
- Before: `Keep recent backups (0 = unlimited)`
- After: `Keep recent backups (0 for unlimited)`

`ConfigSettingsDialog_CpuThreadsDescription`:
- Before: `0 = automatic, current device max {0} threads`
- After: `0 means automatic. Device max: {0} threads.`

`ConfigSettingsDialog_Blacklist.Text`:
- Before: `Blacklist (excluded files/folders)`
- After: `Exclusion list`

`ConfigSettingsDialog_BackupWhitelist.Text`:
- Before: `Whitelist (only backup these files/folders)`
- After: `Inclusion list (only back up these files/folders)`

`BackupService_RestoreMissingBaseFull_Content`:
- Before: `The target Smart backup "{0}" no longer has a usable base Full archive, so precise Clean restore is unavailable. If you continue, FolderRewind will switch to a compatibility fallback: reverse overwrite restore from the newest backup down to the selected Smart backup. This can roll back many files, but it cannot delete extra stale files. Continue anyway?`
- After: `The Smart incremental backup "{0}" no longer has a usable full backup base, so precise Clean restore is unavailable. Continuing will use a compatibility fallback: reverse-overwrite from the newest backup to the target. This rolls back file versions but won't remove stale files. Continue?`

`SettingsPage_DefaultBackupRootPathDesc.Text`:
- Before: `New configurations automatically use "default path\config name" as the backup destination (can be changed per config)`
- After: `New configurations use a subdirectory of this path as the backup destination. Can be changed per configuration.`

`FolderManager_CommentPlaceholder`:
- Before: `e.g. Before update (optional)`
- After: `e.g., Before update (optional)`

`HistoryPage_CommentFilter.PlaceholderText`:
- Before: `Search by note/comment`
- After: `Search by note`

- [ ] **Step 3: Commit**

```bash
git add FolderRewind/Strings/en-US/Resources.resw
git commit -m "fix(l10n): en-US shell/home/settings wording and WinUI conventions"
```

---

## Task 6: en-US — ConfigSettingsDialog, BackupService, CoreValidation, Plugins, Cloud, Templates, Misc

**Files:**
- Modify: `FolderRewind/Strings/en-US/Resources.resw` — keys from `ConfigSettingsDialog_*`, `BackupService_*`, `CoreValidation_*`, `Plugin*`, `KnotLink_*`, `CloudSync_*`, `Template*`, `OfficialTemplates_*`, `GitHub*`, `Notification_*`, `Hotkeys_*`, `MiniWindow_*`, `Encryption_*`, `Update_*`, `Picker_*`, `Tray_*`, `FirstLaunch_*`, `AutoBackup_*`

- [ ] **Step 1: Apply terminology and convention fixes**

Ensure all ToggleSwitch labels use `On`/`Off` (not `On -`/`Off -` with descriptions).
Ensure sentence case for all headers and labels.

- [ ] **Step 2: Fix specific strings**

`PluginStorePage_Desc.Text`:
- Before: `Download plugin zip from the latest GitHub Release. The zip root must contain manifest.json.`
- After: `Download plugin packages from the latest GitHub Release. The package root must contain manifest.json.`

`Plugins_UninstallConfirm`:
- Before: `Uninstall plugin: {0} ({1})?
Tip: If plugin files are in use, you may need to close the app and try again.`
- After: `Uninstall plugin {0} ({1})?
If plugin files are in use, you may need to close the app first.`

`KnotLink_Parse_ExpectedOption`:
- Before: `Parameterized commands only accept -key=value. Unexpected character: {0}`
- After: `Parameterized commands only accept -key=value format. Unexpected character: {0}`

`SettingsPage_KnotLinkSendCustom_Placeholder`:
- Before: `Enter event_data (will be sent via OpenSocket: SEND <event_data>)`
- After: `Enter content to send`

`CloudSync_Task_UploadingMetadata`:
- Before: `Uploading metadata...`
- After: `Syncing metadata...`

`CloudSync_Task_DownloadingMetadata`:
- Before: `Downloading metadata...`
- After: `Downloading metadata...`

`Encryption_PasswordWarning`:
- Before: `⚠ Password cannot be changed once set. If you lose the password, encrypted backups cannot be restored. Please keep it safe.`
- After: `Password cannot be changed once set. Losing it means encrypted backups cannot be restored.`

`FirstLaunch_Content`:
- Before: `If you are not sure how to use FolderRewind, you can watch a brief introduction video on Bilibili.`
- After: `Not sure where to start? Watch a quick introduction video on Bilibili.`

`History_PartialCleanConfirm_Content`:
- Before: `This backup was created from a whitelist or partial backup scope and contains only some files. Continuing with Clean restore may delete other files in the target folder that were not included in the backup. Continue?`
- After: `This backup was created from an inclusion list or partial scope and contains only some files. Continuing with Clean restore may delete other files in the target folder not included in the backup. Continue?`

`KnotLink_Error_PartialCleanRequiresConfirm`:
- Before: `The target backup is a partial backup. To proceed with Clean restore, add -confirm_partial_clean=true.`
- After: `The target backup is a partial backup. To proceed with Clean restore, add -confirm_partial_clean=true.`

`ConfigSettingsDialog_CloudSyncHistoryAfterUploadDesc.Text`:
- Before: `After each successful auto-upload, additionally merge and upload the current configuration's latest history and active-history manifest, so other devices syncing can avoid re-downloading old cloud backups that were already deleted locally.`
- After: `After each successful auto-upload, sync the current configuration's history and active-history manifest. This helps other devices avoid re-downloading old backups that were already deleted locally.`

- [ ] **Step 3: Commit**

```bash
git add FolderRewind/Strings/en-US/Resources.resw
git commit -m "fix(l10n): en-US config/plugins/cloud/templates/misc wording"
```

---

## Task 7: Cross-check and Final Verification

**Files:**
- Modify: `FolderRewind/Strings/zh-CN/Resources.resw`
- Modify: `FolderRewind/Strings/en-US/Resources.resw`

- [ ] **Step 1: Verify key parity**

Run a script to check both files have identical key sets:

```bash
cd D:/Programs/FolderRewind
python -c "
import xml.etree.ElementTree as ET
zh = ET.parse('FolderRewind/Strings/zh-CN/Resources.resw')
en = ET.parse('FolderRewind/Strings/en-US/Resources.resw')
zh_keys = {d.attrib['name'] for d in zh.findall('.//data')}
en_keys = {d.attrib['name'] for d in en.findall('.//data')}
missing_in_en = zh_keys - en_keys
missing_in_zh = en_keys - zh_keys
if missing_in_en: print(f'Missing in en-US: {missing_in_en}')
if missing_in_zh: print(f'Missing in zh-CN: {missing_in_zh}')
if not missing_in_en and not missing_in_zh: print('Key sets match.')
"
```

Expected: `Key sets match.`

- [ ] **Step 2: Verify no leftover terminology**

Search for old terms that should have been replaced:

```bash
# Check zh-CN for leftover English terms
grep -n "Smart 备份\|Full 备份\|Clean 还原\|清洁还原" FolderRewind/Strings/zh-CN/Resources.resw
# Should return nothing

# Check zh-CN for "已开启"/"已关闭" in ToggleSwitch contexts
grep -n "已开启\|已关闭" FolderRewind/Strings/zh-CN/Resources.resw
# Should return nothing (or only non-ToggleSwitch contexts)

# Check zh-CN for lowercase "metadata" in user-facing strings
grep -n ">metadata<" FolderRewind/Strings/zh-CN/Resources.resw
# Should return nothing
```

- [ ] **Step 3: Commit if any fixes needed**

```bash
git add FolderRewind/Strings/zh-CN/Resources.resw FolderRewind/Strings/en-US/Resources.resw
git commit -m "fix(l10n): final cross-check and terminology cleanup"
```

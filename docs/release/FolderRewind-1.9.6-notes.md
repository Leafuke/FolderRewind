官网与说明文档：https://folderrewind.top

建议优先使用 [Microsoft Store](https://apps.microsoft.com/detail/9nwsdgxdqws4) 下载本软件。

## 更新内容

1.9.6 改进旧备份迁移与快速还原，并完善预设安装和中英文显示。本版本内置 **MineRewind 1.9.8**，插件 API 扩展至 **3.9**。

1. **新增只读世界地图预览。** 启用 MineRewind 后，可从 Minecraft Java 存档来源的操作菜单打开地图，查看彩色地形、切换维度、拖动与缩放，并查看区块详情。
2. **修复旧版备份迁移。** 修正 1.8.2 Smart 增量备份元数据目录的读取，改善增量依赖缺失和校验失败提示。迁移报告可保留有效的验证结果，归档或元数据变化后会要求重新验证；调整来源和归档位置后，验证结果与历史列表及时刷新。
3. **改善旧备份快速还原。** 来源尚未建立活动分支时，可选择最近的可恢复旧备份；已建立活动分支的来源仍按该分支解析目标。删除边界不明的旧备份及部分范围备份使用覆盖还原，保留目标目录中的其他文件；还原前会再次检查所选分支和目标是否变化。
4. **完善预设与依赖工具安装。** Minecraft 增强体验预设可检查并复用兼容的 KnotLink Service，在用户确认后下载、校验并启动安装程序，再检查服务就绪与连接状态。云配置向导优先复用已验证可用的 rclone、OpenList；下载失败时尝试后续来源，并核对 SHA-256。步骤失败原因和依赖状态提示更加明确。
5. **修复中英文显示。** 改善英文界面意外回退到中文的问题，同步安装版的 XAML 语言设置，并补全地图工具栏、维度、导航、详情、诊断和无障碍名称的中英文文案。
6. **修复退出时的重复保存。** 应用完成最终配置保存并确认退出后，设置页卸载不再追加保存请求，避免退出流程中的保存冲突。

## 下载与升级

- 一般 Intel / AMD Windows 设备：`FolderRewind_1.9.6.0_Setup_x64.exe`
- Windows on ARM 设备：`FolderRewind_1.9.6.0_Setup_arm64.exe`

旧版若未自动识别 Setup 安装包，请从本页手动下载升级。安装包尚未进行生产代码签名。

从 1.8 升级时，请保留旧归档及增量依赖，并查看应用中的迁移报告。需要使用分支、检出和合并时，请先创建一次新的全量备份，以建立明确的恢复边界。

---

## English

Official website and documentation: https://folderrewind.top

We recommend downloading FolderRewind from the [Microsoft Store](https://apps.microsoft.com/detail/9nwsdgxdqws4).

### What's new

1.9.6 improves legacy backup migration and Quick Restore, and refines preset installation and Chinese/English localization. This release bundles **MineRewind 1.9.8** and extends the plugin API to **3.9**.

1. **Added read-only world map previews.** With MineRewind enabled, open the map from a Minecraft Java source's actions menu to view colored terrain, switch dimensions, pan, zoom and inspect chunks. Previews read the current source folder rather than a historical backup snapshot. Refresh after the game writes changes.
2. **Fixed legacy backup migration.** Corrected the metadata directory used for 1.8.2 Smart incremental backups and improved missing-dependency and verification diagnostics. Migration reports preserve valid verification results and require re-verification when archives or metadata change. Verification results and history listings refresh after source or archive-location adjustments.
3. **Improved Quick Restore for legacy backups.** Sources without an active branch can select their latest recoverable legacy backup. Sources with an active branch continue to resolve targets through that branch. Legacy backups with unknown deletion boundaries and partial-scope backups use overwrite restore, preserving other files in the destination. The selected branch and restore target are checked again before restoring.
4. **Improved preset and dependency installation.** The Minecraft Enhanced Experience preset checks for a compatible KnotLink Service, reuses it where possible, or downloads, verifies and launches its installer with user confirmation. It then checks service readiness and connection status. Cloud setup prefers verified, usable rclone and OpenList installations. Downloads try subsequent sources on failure and verify SHA-256, with clearer step failures and dependency status.
5. **Fixed Chinese/English localization.** Improved unexpected Chinese fallback in English interfaces, synchronized XAML language settings in Setup installations, and completed map toolbar, dimension, navigation, detail, diagnostic and accessibility text in both languages.
6. **Fixed duplicate saves during exit.** Once the final configuration save succeeds and exit is confirmed, unloading the settings page no longer queues another save, avoiding save conflicts during shutdown.

### Download and upgrade

- Most Intel / AMD Windows devices: `FolderRewind_1.9.6.0_Setup_x64.exe`
- Windows on ARM devices: `FolderRewind_1.9.6.0_Setup_arm64.exe`

If an older version does not detect Setup installers automatically, download one from this page to upgrade manually. Production code signing is not included.

When upgrading from 1.8, keep legacy archives and their incremental dependencies, and review the in-app migration report. Create a new full backup before using branches, checkout or merge to establish a known recovery boundary.

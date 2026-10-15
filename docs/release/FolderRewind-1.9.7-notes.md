官网与说明文档：https://folderrewind.top

建议优先使用 [Microsoft Store](https://apps.microsoft.com/detail/9nwsdgxdqws4) 下载本软件。

## 更新内容

1.9.7 改进跨设备迁移、云连接管理与备份还原体验，并修复选择性迁移携带无关云连接的问题。

1. **整合数据迁移向导。** 配置与历史导入导出统一到连续流程，支持选择项目、批量映射来源和备份路径，以及携带归档的 v3 迁移包。
2. **完善迁移后的历史恢复。** 改善本地归档关联、加密备份凭据准备与原分支续接。
3. **改进云连接与原仓库恢复。** 统一管理共享连接和项目专属连接，完善受管凭据及 OAuth 令牌刷新。改善只读恢复、云端配置版本导入、上传中断后的任务恢复，以及本机 OpenList 的授权启动与状态反馈。(#61 @fcgdagfs)
4. **修复选择性迁移的云连接范围。** 只携带所选项目实际引用的连接，导入旧迁移包时也会忽略无关连接。连接 ID 冲突时保留目标设备已有连接并正确重映射项目引用。
5. **改进备份与还原处理。** 完善归档属性解析、还原验证策略和可恢复的同卷文件移动，减少部分还原场景中的重复验证及文件操作开销，并加强中断恢复与清理过程。
6. **改善界面。** 调整迁移向导、插件商店和设置操作的布局与错误反馈，补齐模板菜单的自动化标识。
7. **改善更新功能。** 完善更新元数据校验与按安装渠道选择升级入口。

## 下载与升级

- 一般 Intel / AMD Windows 设备：`FolderRewind_1.9.7.0_Setup_x64.exe`
- Windows on ARM 设备：`FolderRewind_1.9.7.0_Setup_arm64.exe`

---

## English

Official website and documentation: https://folderrewind.top

We recommend downloading FolderRewind from the [Microsoft Store](https://apps.microsoft.com/detail/9nwsdgxdqws4).

### What's new

1.9.7 improves cross-device migration, cloud connection management, and backup and restore workflows. It also fixes unrelated cloud connections being included in selective migrations.

1. **Integrate the data migration wizard.** Unify the configuration and historical import/export into a continuous process, support the selection of projects, batch mapping of sources and backup paths, and carry the v3 migration package for archiving.
2. **Improve historical recovery after migration.** Improve local archive association, encryption backup credential preparation, and continuation of the original branch.
3. **Improve cloud connectivity and restore original repositories.** Unify the management of shared connections and project-specific connections, enhance managed credentials and OAuth token refresh. Improve read-only recovery, cloud configuration version import, task recovery after upload interruption, as well as the authorized launch and status feedback of the local OpenList. (#61 @fcgdagfs)
4. **Fix the scope of cloud connections for selective migration.** Only the connections actually referenced by the selected projects will be carried, and irrelevant connections will be ignored when importing old migration packages. In case of connection ID conflicts, the existing connections on the target device will be retained and the project references will be correctly remapped.
5. **Improve backup and restore processing.** Refine archive attribute parsing, restore verification strategies, and recoverable same-volume file movement to reduce the overhead of repeated verification and file operations in some restore scenarios, and enhance the interruption recovery and cleanup processes.
6. **Improve the interface.** Adjust the layout and error feedback of the migration wizard, plugin store, and settings operations, and complete the automated identification of template menus.
7. **Improve the update function.** Improve the verification of update metadata and select upgrade entries based on installation channels.

### Download and upgrade

- Intel / AMD Windows devices: `FolderRewind_1.9.7.0_Setup_x64.exe`
- Windows on ARM devices: `FolderRewind_1.9.7.0_Setup_arm64.exe`

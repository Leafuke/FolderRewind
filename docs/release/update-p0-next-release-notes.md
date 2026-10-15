# 下一次正式 Release：更新系统修复说明

此文件是待合入下一次正式版本更新说明的中英文片段；发布时将内容加入对应的 `FolderRewind-<三段版本>-notes.md`。本次不修改版本号，不改写 v1.9.6 的历史更新说明，也不新增公开 Release 附件。

## 中文

- 修复应用更新入口的链接校验。版本、安装包文件名、设备架构与官方仓库地址必须一致；无法确认时改为打开官方发布列表。
- 对齐当前 Setup EXE 发行格式，移除旧 `.7z` 更新包准备及安装脚本引导。Setup 安装用户可下载对应操作系统架构的官方安装包，Store 用户仍前往 Microsoft Store。
- 侧载、非商店签名和无法识别的安装形态提供官方 Release 人工升级入口。请保留现有配置，并先确认安装方式及配置、插件数据的迁移步骤；应用不会自动切换安装方式或迁移这些数据。
- 镜像继续用于版本发现，并明确标注信息来源。更新操作只打开官方入口，但镜像提供的版本和说明尚未独立验真。

**下载与升级：**如果旧客户端（包括不能识别 Setup EXE 的 1.8.2 客户端）没有提供正确的下载入口，请从 [官方 Releases](https://github.com/Leafuke/FolderRewind/releases) 手动选择最新版本：Intel/AMD Windows 设备下载 x64 Setup，Windows on ARM 设备下载 ARM64 Setup。运行安装器前，请通过托盘菜单“退出”正常关闭 FolderRewind，等待正在进行的任务完成。

## English

- Fixed update-link validation. The version, installer filename, device architecture and official repository URL must agree. When validation fails, the app opens the official releases list.
- Aligned app updates with the current Setup EXE release format and removed legacy `.7z` package preparation and installation-script guidance. Setup installations can download the official installer for the operating system architecture; Store installations keep using Microsoft Store.
- Sideload, non-Store signed and unrecognized installations use the official Release page for manual upgrades. Keep existing configuration and confirm the installation method and configuration/plugin-data migration steps first. The app does not automatically switch installation types or migrate this data.
- Mirrors remain available for version discovery, with a source hint. Update actions open official entry points, but mirror-supplied versions and release notes are not independently authenticated.

**Download and upgrade:** If an older client, including a 1.8.2 client that cannot select Setup EXE assets, does not offer the correct download, select the latest version from the [official releases](https://github.com/Leafuke/FolderRewind/releases). Use x64 Setup on Intel/AMD Windows devices and ARM64 Setup on Windows on ARM. Before running the installer, exit FolderRewind normally through the system tray menu and wait for running tasks to finish.

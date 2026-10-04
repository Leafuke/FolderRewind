**中文** | [English](README_en.md)

<p align="center">
<img src="FolderRewind/Assets/StoreLogo.png" width="48px"/>
</p>

<div align="center">

# 存档时光机

[![GitHub release (latest by date)](https://img.shields.io/github/v/release/Leafuke/FolderRewind)](https://github.com/Leafuke/FolderRewind/releases) ![GitHub Release Date](https://img.shields.io/github/release-date/Leafuke/FolderRewind) ![GitHub stars](https://img.shields.io/github/stars/Leafuke/FolderRewind?style=flat) ![GitHub forks](https://img.shields.io/github/forks/Leafuke/FolderRewind)

</div>
<p align="center">
<a href="#简介">简介</a> &nbsp;&bull;&nbsp;
<a href="#亮点">亮点</a> &nbsp;&bull;&nbsp;
<a href="#安装">安装</a> &nbsp;&bull;&nbsp;
<a href="#使用">使用</a> &nbsp;&bull;&nbsp;
<a href="#受认可的插件">插件</a> &nbsp;&bull;&nbsp;
<a href="#开发">开发</a> &nbsp;&bull;&nbsp;
<a href="#交流讨论">交流</a> &nbsp;&bull;&nbsp;
<a href="#鸣谢">鸣谢</a>
</p>


## 简介

FolderRewind 是一款基于 **WinUI 3** 和 **.NET 10** 构建的现代化、高性能备份管理工具。它可以帮助您轻松地为重要数据（文档、工程文件、游戏存档等）创建自动化的版本控制备份。

作为 MineBackup 的精神续作，FolderRewind 增强其通用性的同时为不同需求的用户保留扩展性，内置强大的插件系统，插件作者们可以专为 **Minecraft 游戏存档** 等特殊场景优化，是游戏玩家和高级用户的理想选择。

## 亮点

- **🛡️ 多种备份模式**: 使用 **7-Zip-zstd** 引擎，支持 Full（全量）、Smart（增量）和 Rolling（基于可信基线生成独立新归档），可配置压缩等级、线程数、过滤规则及无变更跳过。
- **🤖 自动备份**: 应用运行期间，可按配置自动执行：
  - **间隔备份**: 支持每隔 X 分钟自动备份。
  - **计划备份**: 按月、日、时、分设置计划。
  - **启动备份**: 启动应用时执行备份。
  - **条件备份**: 指定文件从占用状态变为解除占用时触发备份。
- **🔌 插件扩展**: 
  - **自动发现**: 智能扫描已知目录结构（如 Minecraft 存档），一键批量创建备份配置。
  - **一致性与还原协调**: 插件可为特定场景提供一致性来源、还原协调和玩家数据保留；实际支持取决于插件与运行环境。
  - **扩展能力**: Plugin System v3 支持 `.frplugin` 安装包、官方目录、插件设置、命令及自定义备份表示；备份与还原仍由宿主编排和校验。
- **⏳ 历史与分支**: 按备份源浏览可恢复版本，支持重要标记、分支检出及文件级合并，提供文本差异比较与冲突处理。当前不提供 Minecraft region、chunk 或 NBT 语义合并。
- **☁️ 云备份**: 通过 **rclone** 连接 WebDAV、FTP、SFTP、OneDrive、S3 等存储，也可通过 **OpenList** 桥接网盘。需先准备工具并配置连接；历史同步与归档上传分别执行。
- **🎨 现代设计**: 
  - 完美适配 Windows 11 的 **Mica**/**Acrylic** 材质与设计语言。
  - 支持深色/浅色主题切换。
  - 界面简洁直观，操作流畅。

## 安装

支持 Windows 10 1809 及以上版本和 Windows 11；当前发行架构为 **x64 / ARM64**。GitHub Setup 安装包自带 .NET 与 Windows App SDK 运行环境。

### 商店下载（推荐）：

<a href="https://apps.microsoft.com/detail/9nwsdgxdqws4?referrer=appbadge&mode=direct">
	<img src="https://get.microsoft.com/images/en-us%20dark.svg" width="200"/>
</a>

### GitHub Setup EXE 安装：

1. 打开 [Release](https://github.com/Leafuke/FolderRewind/releases) 页面。
2. 下载与设备架构匹配的安装包及同名 `.sha256` 文件：`FolderRewind_{version}_Setup_x64.exe` 适用于绝大多数 Intel/AMD Windows 设备，`FolderRewind_{version}_Setup_arm64.exe` 适用于 Windows on ARM。
3. 使用 PowerShell 的 `Get-FileHash .\FolderRewind_{version}_Setup_x64.exe -Algorithm SHA256` 核对校验值（请替换版本号与架构），然后运行安装包。
4. 中英文安装向导默认选择当前用户安装，路径为 `%LocalAppData%\Programs\FolderRewind`；也可选择所有用户安装或其他本地路径。所有用户安装需要管理员权限，无需开启开发人员模式或导入证书。

当前 GitHub Release 仅发布 **Setup EXE 和 SHA-256 校验文件**，不再提供独立 MSI、MSIX 或侧载压缩包。安装包尚未进行生产代码签名，Windows 可能显示“未知发布者”或 SmartScreen 提示。请从本项目官方 Release 下载。旧版无法自动识别 Setup 更新时，可手动下载升级。

Store 更新由商店管理，版本可能与 GitHub 不同步。Setup 与 Store／旧 MSIX 使用独立数据目录，切换渠道不会自动迁移配置或插件；请先备份数据，并参阅[数据迁移指南](https://folderrewind.top/docs/guides/data-migration)。不要同时运行不同渠道的版本保护相同来源。

### 1.9 升级说明

- 1.9 系列使用 Plugin System v3，当前源码的插件 API 为 **3.6**。应用、插件和 SDK 各自独立版本；旧版 v2 插件不能直接作为 v3 插件加载，旧载荷会移入可恢复的隔离区。
- 新安装插件默认停用，显式启用后才运行。插件在应用进程内执行，请仅启用可信插件；包校验与服务声明不是安全沙箱。
- 旧版云配置和云存档不会自动迁移到新版历史。请保留旧归档及增量依赖，重新建立连接并验证恢复；详见[云存档指南](https://folderrewind.top/docs/guides/cloud-archive)。

## 使用

1. 创建备份项目，添加备份源文件夹，并选择独立的备份存放目录。
2. 手动创建一次备份，在测试目录验证还原结果，再启用自动备份或云上传。
3. 在历史页面选择可恢复版本进行还原；使用分支和合并前，先查看范围与冲突预览。

详细步骤见[官方网站文档](https://folderrewind.top/docs/intro)。

## 受认可的插件

| 插件名称               | 描述                                     | 作者          | 下载链接                                      |
|----------------------|----------------------------------------|-------------|-------------------------------------------|
| MineRewind | Minecraft Java / Bedrock 存档发现与备份；Java 提供一致性和还原协调等扩展，Bedrock 使用普通文件备份并提示关闭游戏。 | Leafuke | [仓库与下载](https://github.com/Leafuke/FolderRewind-Plugin-Minecraft/releases) |

安装包携带 MineRewind v3 包；请在插件管理中核对版本和启用状态。

## 开发

**开发环境要求:**

- Visual Studio 2026
- .NET 10 SDK
- `.NET 桌面开发`、`WinUI 应用程序开发` 工作负载

打开 `FolderRewind.slnx`，选择 x64 或 ARM64 构建。当前项目使用 Windows App SDK **2.5.1**。构建需准备对应架构的 `7za.exe`（可使用 `.github/scripts/Stage-SevenZip.ps1`），并通过 `SevenZipExecutable` 指定路径。Setup 构建流程见 [Installer 说明](Installer/README.md)，CI 配置见 [构建工作流](.github/workflows/build-github-sideload.yml)。

### 插件开发

插件引用独立的 `FolderRewind.Plugin.Abstractions` 契约，不引用应用或 UI 项目。当前契约版本为 **3.6.0**，API 为 **3.6**；参考[插件开发文档](https://folderrewind.top/docs/plugins/overview)、[仓库内 v3 说明](docs/plugin-v3/README.md)和 [SDK 说明](FolderRewind.Plugin.Abstractions/README.md)。


## 交流讨论

有兴趣一起交流的话，可以加 QQ 群。

<img src="./FolderRewind/Assets/qq_group_light.jpg" width="240px" />

## 鸣谢

- [Windows App SDK](https://github.com/microsoft/windowsappsdk)
- [WinUI](https://github.com/microsoft/microsoft-ui-xaml)
- [Windows Community Toolkit](https://github.com/CommunityToolkit/Windows)
- [KnotLink](https://github.com/KnotLink-Protocol/KnotLink)
- [7-Zip](https://www.7-zip.org/)
- [7-Zip-zstd](https://github.com/mcmilk/7-Zip-zstd)
- [MineBackup - 前作](https://github.com/Leafuke/MineBackup)
- [Bili.Copilot - 代码参考](https://github.com/Richasy/Bili.Copilot)
- 以及其他在开发过程中提供过助力的小伙伴

---
*为您的数字世界留一份后悔药。*

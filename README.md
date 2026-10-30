**中文** | [English](README_en.md)

<p align="center">
<img src="src/FolderRewind/Assets/StoreLogo.png" width="64" alt="FolderRewind 图标" />
</p>

<div align="center">

# 存档时光机 · FolderRewind

[![GitHub release (latest by date)](https://img.shields.io/github/v/release/Leafuke/FolderRewind)](https://github.com/Leafuke/FolderRewind/releases) ![GitHub Release Date](https://img.shields.io/github/release-date/Leafuke/FolderRewind) ![GitHub stars](https://img.shields.io/github/stars/Leafuke/FolderRewind?style=flat) ![GitHub forks](https://img.shields.io/github/forks/Leafuke/FolderRewind)

</div>

<p align="center">
为文档、工程和游戏存档保留可恢复的版本。
<br />
<a href="https://folderrewind.top/docs/intro">使用文档</a> &nbsp;&bull;&nbsp;
<a href="https://apps.microsoft.com/detail/9nwsdgxdqws4">Microsoft Store</a> &nbsp;&bull;&nbsp;
<a href="https://github.com/Leafuke/FolderRewind/releases">GitHub 下载</a> &nbsp;&bull;&nbsp;
<a href="https://github.com/Leafuke/FolderRewind/issues">问题反馈</a>
</p>

<p align="center">
<a href="#简介">简介</a> &nbsp;&bull;&nbsp;
<a href="#亮点">亮点</a> &nbsp;&bull;&nbsp;
<a href="#安装">安装</a> &nbsp;&bull;&nbsp;
<a href="#使用">使用</a> &nbsp;&bull;&nbsp;
<a href="#插件与-minecraft">插件</a> &nbsp;&bull;&nbsp;
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
  - **条件备份**: 指定文件从占用状态变为解除占用时触发备份。
- **🔌 插件扩展**: 
  - **自动发现**: 智能扫描已知目录结构（如 Minecraft 存档），一键批量创建备份配置。
  - **一致性与还原协调**: 插件可为特定场景提供一致性来源、还原协调和玩家数据保留；实际支持取决于插件与运行环境。
  - **扩展能力**: Plugin System v3 支持 `.frplugin` 安装包、官方目录、插件设置、命令及自定义备份表示；备份与还原仍由宿主编排和校验。
- **⏳ 历史与分支**: 按备份源浏览可恢复版本，支持重要标记、分支检出及合并，提供文本差异比较与可视化冲突处理。
- **☁️ 云备份**: 通过 **rclone** 连接 WebDAV、FTP、SFTP、OneDrive、S3 等存储，也可通过 **OpenList** 桥接网盘。需先准备工具并配置连接；历史同步与归档上传分别执行。
- **🎨 现代设计**: 
  - 完美适配 Windows 11 的 **Mica**/**Acrylic** 材质与设计语言。
  - 支持深色/浅色主题切换。

## 安装

支持 Windows 10 1809 及以上版本和 Windows 11；当前发行架构为 **x64 / ARM64**。GitHub Setup 安装包自带 .NET 与 Windows App SDK 运行环境。

### Microsoft Store（推荐）

<a href="https://apps.microsoft.com/detail/9nwsdgxdqws4?referrer=appbadge&mode=direct">
	<img src="https://get.microsoft.com/images/en-us%20dark.svg" width="200" alt="从 Microsoft Store 下载" />
</a>

### GitHub Setup 安装包

1. 打开 [Release](https://github.com/Leafuke/FolderRewind/releases) 页面。
2. 下载与设备架构匹配的安装包及同名 `.sha256` 文件：`FolderRewind_{version}_Setup_x64.exe` 适用于绝大多数 Intel/AMD Windows 设备，`FolderRewind_{version}_Setup_arm64.exe` 适用于 Windows on ARM。
3. 使用 PowerShell 的 `Get-FileHash .\FolderRewind_{version}_Setup_x64.exe -Algorithm SHA256` 核对校验值（请替换版本号与架构），然后运行安装包。
4. 中英文安装向导默认选择当前用户安装，路径为 `%LocalAppData%\Programs\FolderRewind`；也可选择所有用户安装或其他本地路径。所有用户安装需要管理员权限，无需开启开发人员模式或导入证书。

当前 GitHub Release 仅发布 **Setup EXE 和 SHA-256 校验文件**，不再提供独立 MSI、MSIX 或侧载压缩包。安装包尚未进行生产代码签名，Windows 可能显示“未知发布者”或 SmartScreen 提示。请从本项目官方 Release 下载。旧版无法自动识别 Setup 更新时，可手动下载升级。

Store 更新由商店管理，版本可能与 GitHub 不同步。Setup 与 Store／旧 MSIX 使用独立数据目录，切换渠道不会自动迁移配置或插件；请先备份数据，并参阅[数据迁移指南](https://folderrewind.top/docs/guides/data-migration)。不要同时运行不同渠道的版本保护相同来源。

## 使用

1. 创建备份项目，添加备份源文件夹，并选择独立的备份存放目录。
2. 手动创建一次备份，在测试目录验证还原结果，再启用自动备份或云上传。
3. 在历史页面选择可恢复版本进行还原；使用分支和合并前，先查看范围与冲突预览。

| 接下来想做什么 | 文档入口 |
| --- | --- |
| 安装应用、完成首次备份与还原 | [快速开始](https://folderrewind.top/docs/intro) |
| 配置远程存储与云副本 | [云存档](https://folderrewind.top/docs/guides/cloud-archive) |
| 保护 Minecraft 世界 | [Minecraft 专题](https://folderrewind.top/docs/guides/minecraft/overview) |
| 换电脑或切换安装渠道 | [数据迁移](https://folderrewind.top/docs/guides/data-migration) |

## 插件与 Minecraft

| 插件名称               | 描述                                     | 作者          | 下载链接                                      |
|----------------------|----------------------------------------|-------------|-------------------------------------------|
| MineRewind | Minecraft Java / Bedrock 存档发现与备份；Java 提供地图、区域范围、无损增量表示及语义合并等扩展。Bedrock 使用普通文件备份，请先关闭游戏。 | Leafuke | [仓库与下载](https://github.com/Leafuke/FolderRewind-Plugin-Minecraft/releases) |

## 开发

### 从源码构建

- Windows 与 WinUI 开发工具链；使用 Visual Studio 时，安装 `.NET 桌面开发`、`WinUI 应用程序开发` 工作负载。
- .NET 10 SDK，具体版本由 [global.json](global.json) 指定。
- 首次准备压缩工具时，PATH 中需要已有 `7z.exe` 用于解压；准备脚本会下载并校验对应架构的 `7za.exe`。

从仓库根目录运行：

```powershell
$sevenZip = ./scripts/build/Stage-SevenZip.ps1 -Platform x64 | Select-Object -Last 1
dotnet restore ./FolderRewind.slnx -p:Platform=x64
dotnet build ./FolderRewind.slnx -c Debug -p:Platform=x64 -p:GenerateAppxPackageOnBuild=false "-p:SevenZipExecutable=$sevenZip" --no-restore
```

ARM64 构建需同时将工具准备、还原和构建的平台改为 `ARM64`。依赖版本见 [Directory.Packages.props](Directory.Packages.props)、[Directory.Build.props](Directory.Build.props) 与各项目声明，避免根据旧教程固定版本。

### 工程结构与验证

| 路径 | 职责 |
| --- | --- |
| `src/FolderRewind/` | WinUI 应用、界面、配置与生命周期适配 |
| `src/FolderRewind.Core/` | 不依赖 WinUI 的 Windows 核心实现 |
| `src/FolderRewind.Plugin.Abstractions/` | 公开、仅依赖 BCL 的插件契约 |
| `src/FolderRewind.Plugin.Runtime/` | 插件加载、能力路由、生命周期与受控 Artifact 操作 |
| `tests/` | Core、宿主适配、插件契约及运行时测试 |
| `scripts/`、`Installer/` | 构建、验证与发行工具 |
| `docs/` | 领域术语、有效 ADR 与协议／安全规范 |

```powershell
./scripts/test/Test-RepositoryLayout.ps1
./scripts/test/Invoke-SetupCoreTests.ps1
```

核心测试入口会准备固定哈希的测试工具，并依次验证插件契约、运行时、宿主及故障恢复；使用隔离目录和本地云后端。普通测试引用正式 Core；故障恢复验收通过 `FolderRewindFaultInjection=true` 使用隔离输出。

网站、MineRewind、Catalog 和模板分别维护，普通 Host 构建不需要其源码。完整说明见[开发与验证](https://folderrewind.top/docs/architecture/development)、[贡献约定](CONTRIBUTING.md)、[Host 工程约束](docs/README.md)、[安装载荷入口](Installer/README.md)及 [Setup 发布工作流](.github/workflows/release-setup.yml)。

### 插件开发

插件只引用独立的 `FolderRewind.Plugin.Abstractions` 契约，不引用应用、Core 或 Runtime，也不在 `.frplugin` 中捆绑宿主 Abstractions DLL。

宿主负责配置持久化、操作编排、历史、事务、完整性、加密、保留策略、云副本及目标目录写入；插件负责领域语义。参考[插件文档](https://folderrewind.top/docs/plugins/overview)和 [SDK 说明](src/FolderRewind.Plugin.Abstractions/README.md)。

## 交流讨论

欢迎通过 [Issues](https://github.com/Leafuke/FolderRewind/issues) 提交问题或功能建议，请附应用／系统版本、复现步骤和预期结果。也可以加入 QQ 群交流，或查看[支持项目与赞助说明](https://folderrewind.top/docs/support)。

<img src="src/FolderRewind/Assets/qq_group_light.jpg" width="240" alt="FolderRewind QQ 交流群二维码" />

## 许可

许可条款见 [LICENSE](LICENSE.txt)，另见[第三方声明](THIRD-PARTY-NOTICES.md)与[隐私说明](PRIVACY.md)。

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

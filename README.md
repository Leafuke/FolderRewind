**中文** | [English](README_en.md)

# 存档时光机 · FolderRewind

<img src="src/FolderRewind/Assets/StoreLogo.png" width="64" alt="FolderRewind" />

[![GitHub Release](https://img.shields.io/github/v/release/Leafuke/FolderRewind)](https://github.com/Leafuke/FolderRewind/releases)

基于 WinUI 3 与 .NET 10 的 Windows 备份管理工具。支持文件夹和游戏存档的版本化备份、历史还原、自动化、云副本，以及独立的插件与模板生态。

- [Microsoft Store](https://apps.microsoft.com/detail/9nwsdgxdqws4) · [GitHub Setup 下载](https://github.com/Leafuke/FolderRewind/releases)
- [使用文档](https://folderrewind.top/docs/intro) · [安装与升级](https://folderrewind.top/docs/getting-started/installation) · [数据迁移](https://folderrewind.top/docs/guides/data-migration)
- [插件使用与开发](https://folderrewind.top/docs/plugins/overview) · [MineRewind](https://github.com/Leafuke/FolderRewind-Plugin-Minecraft)
- [支持项目与赞助说明](https://folderrewind.top/docs/support) · [问题反馈](https://github.com/Leafuke/FolderRewind/issues)

## 从源码开发

需要 Windows 与 WinUI 开发工具链。SDK 以 `global.json` 为准，包版本由 `Directory.Packages.props` 和各项目覆盖声明。当前开发布局为 `src/`、`tests/`；网站描述了 [1.10 开发结构](https://folderrewind.top/docs/architecture/directory-structure)。

```powershell
$sevenZip = ./scripts/build/Stage-SevenZip.ps1 -Platform x64 | Select-Object -Last 1
dotnet restore ./FolderRewind.slnx -p:Platform=x64
dotnet build ./FolderRewind.slnx -c Debug -p:Platform=x64 -p:GenerateAppxPackageOnBuild=false "-p:SevenZipExecutable=$sevenZip" --no-restore
./scripts/test/Test-RepositoryLayout.ps1
./scripts/test/Invoke-SetupCoreTests.ps1
```

首次准备需要 PATH 中已有 `7z.exe`（用于解压）。构建前准备对应架构的 `7za.exe`，可通过 `scripts/build/Stage-SevenZip.ps1` 获取并用 `SevenZipExecutable` 指定路径。核心测试入口准备固定哈希的测试工具，在隔离目录执行真实归档、云本地后端与故障恢复验证。

详见[开发与验证](https://folderrewind.top/docs/architecture/development)、[贡献约定](CONTRIBUTING.md)、[Host 工程约束](docs/README.md)和[安装载荷入口](Installer/README.md)。网站、MineRewind、Catalog 和模板分别维护，普通 Host 构建不需要其源码。

## 许可与致谢

见 [LICENSE](LICENSE.txt)、[第三方声明](THIRD-PARTY-NOTICES.md)及[隐私说明](PRIVACY.md)。感谢贡献者、反馈者、赞助者，以及 .NET、WinUI、7-Zip/7-Zip-zstd、CommunityToolkit 和其他依赖项目。

<p align="center">
<img src="assets/StoreLogo.png" width="48px"/>
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

- **🛡️ 可靠备份**: 采用的 **7-Zip** 引擎，提供高效的压缩与加密能力，确保数据安全。
- **🤖 全自动运行**: 一次配置，自动执行：
  - **间隔备份**: 支持每隔 X 分钟自动备份。
  - **定时备份**: 设置每日固定时间（24小时制）执行任务。
  - **启动时备份**: 程序启动即刻捕捉变更。
- **🔌 插件扩展**: 
  - **自动发现**: 智能扫描已知目录结构（如 Minecraft 存档），一键批量创建备份配置。
  - **热备份支持**: 插件可在文件被占用时（如游戏运行中）介入，通过快照机制确保数据一致性。
- **⏳ 历史时间轴**: 清晰不仅是备份文件，更是一条其时间轴。随时将文件夹"回溯"到任意历史状态。
- **🎨 现代设计**: 
  - 完美适配 Windows 11 的 **Mica** 材质与设计语言。
  - 支持深色/浅色主题切换。
  - 界面简洁直观，操作流畅。

## 安装

### 商店下载（推荐）：

<a href="https://apps.microsoft.com/detail/9nwsdgxdqws4?referrer=appbadge&mode=direct">
	<img src="https://get.microsoft.com/images/en-us%20dark.svg" width="200"/>
</a>

### 侧载安装：

1. 打开系统设置，依次选择 `系统` -> `开发者选项`，打开 `开发人员模式`。滚动到页面底部，展开 `PowerShell` 区块，开启 `更改执行策略...` 选项
2. 打开 [Release](https://github.com/Leafuke/FolderRewind/releases) 页面
3. 找到应用包下载。命名格式为：`FolderRewind_{version}_{platform}.7z`
4. 下载应用包后解压，右键单击文件夹中的 `install.ps1` 脚本，选择 `使用 PowerShell 运行`

## 使用

建议查看官方网站的详细使用文档：https://folderrewind.top

![主界面](assets\screenshot1.png)

## 受认可的插件

| 插件名称               | 版本   | 描述                                     | 作者          | 下载链接                                      |
|----------------------|------|----------------------------------------|-------------|-------------------------------------------|
| MineRewind      | 1.4.1 | 专为 Minecraft 游戏存档设计的备份插件。               | Leafuke     | [仓库](https://github.com/Leafuke/FolderRewind-Plugin-Minecraft)

## 开发

**开发环境要求:**
- Visual Studio 2026
- .NET 10 SDK
- "Windows App SDK C# Templates" 工作负载

### 插件开发

如果你希望为 FolderRewind 开发插件以适配更多场景，可以参考 [插件开发文档](https://folderrewind.top/docs/plugins/overview)。


## 交流讨论

有兴趣一起交流的话，可以加 QQ 群。

<img src="./assets/qq_group_light.jpg" width="240px" />

## 鸣谢

- [Windows App SDK](https://github.com/microsoft/windowsappsdk)
- [WinUI](https://github.com/microsoft/microsoft-ui-xaml)
- [KnotLink](https://github.com/hxh230802/KnotLink)
- [7-Zip](https://www.7-zip.org/)
- [MineBackup - 前作](https://github.com/Leafuke/MineBackup)
- [Bili.Copilot - 代码参考](https://github.com/Richasy/Bili.Copilot)
- 以及其他在开发过程中提供过助力的小伙伴

---
*为您的数字世界留一份后悔药。*

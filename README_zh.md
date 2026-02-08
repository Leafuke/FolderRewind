# FolderRewind - 存档时光机

FolderRewind 是一款基于 **WinUI 3** 和 **.NET 8** 构建的现代化、高性能备份管理工具。它可以帮助您轻松地为重要数据（文档、工程文件、游戏存档等）创建自动化的版本控制备份。

作为 MineBackup 的精神续作，FolderRewind 增强其通用性的同时为不同需求的用户保留扩展性，内置强大的插件系统，插件作者们可以专为 **Minecraft 游戏存档** 等特殊场景优化，是游戏玩家和高级用户的理想选择。你可以加入群聊 1071542709 获取更多帮助和交流。

## ✨ 核心亮点

- **🛡️ 可靠备份**: 采用业界标准的 **7-Zip** 引擎，提供高效的压缩与加密能力，确保数据安全。
- **🤖 全自动运行**: 一次配置，自动执行：
  - **间隔备份**: 支持每隔 X 分钟自动备份。
  - **定时备份**: 设置每日固定时间（24小时制）执行任务。
  - **启动时备份**: 程序启动即刻捕捉变更。
- **🔌 插件扩展**: 
  - **自动发现**: 智能扫描已知目录结构（如 Minecraft 存档），一键批量创建备份配置。
  - **热备份支持**: 插件可在文件被占用时（如游戏运行中）介入，通过快照机制确保数据一致性。
- **⏳ 历史时间轴**: 清晰不仅是备份文件，更是一条其时间轴。随时将文件夹"回溯"（Rewind）到任意历史状态。
- **🎨 现代设计**: 
  - 完美适配 Windows 11 的 **Mica** 材质与设计语言。
  - 支持深色/浅色主题切换。
  - 界面简洁直观，操作流畅。
- **🌍 多语言支持**: 原生支持 **简体中文** 与 **English**。

## 🔗 下载与安装

<a href="https://apps.microsoft.com/detail/9nwsdgxdqws4?referrer=appbadge&mode=direct">
	<img src="https://get.microsoft.com/images/en-us%20dark.svg" width="200"/>
</a>


## ✨ 官方认可的插件

| 插件名称               | 版本   | 描述                                     | 作者          | 下载链接                                      |
|----------------------|------|----------------------------------------|-------------|-------------------------------------------|
| MineRewind      | 1.2.0 | 专为 Minecraft 游戏存档设计的备份插件。               | Leafuke     | [仓库](https://github.com/Leafuke/FolderRewind-Plugin-Minecraft)

## 🛠️ 源码构建

**开发环境要求:**
- Visual Studio 2026
- .NET 8 SDK
- "Windows App SDK C# Templates" 工作负载

## 🤝 参与贡献
我们非常欢迎任何形式的贡献！如果您有好的想法，欢迎提交 Pull Request 或者 Issue！

如果你希望为 FolderRewind 开发插件以适配更多场景，可以参考 [插件开发文档](./docs/PluginDevelopmentGuide.md)。


## 🔍 交流讨论

有兴趣一起交流的话，可以加 QQ 群。

<img src="./assets/qq_group_light.jpg" width="240px" />

## 💖 致谢

- [Windows App SDK](https://github.com/microsoft/windowsappsdk)
- [WinUI](https://github.com/microsoft/microsoft-ui-xaml)
- [KnotLink](https://github.com/hxh230802/KnotLink)
- [7-Zip](https://www.7-zip.org/)
- [MineBackup - 前作](https://github.com/Leafuke/MineBackup)
- [Bili.Copilot - 代码参考](https://github.com/Richasy/Bili.Copilot)
- 以及其他在开发过程中提供过助力的小伙伴

---
*为您的数字世界留一份后悔药。*

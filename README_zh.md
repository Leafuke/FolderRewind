# FolderRewind - 存档时光机

**让您的文件夹拥有"时光倒流"的能力。**

FolderRewind 是一款基于 **WinUI 3** 和 **.NET 8** 构建的现代化、高性能备份管理工具。它可以帮助您轻松地为重要数据（文档、工程文件、游戏存档等）创建自动化的版本控制备份。

作为 MineBackup 的精神续作，FolderRewind 增强其通用性的同时为不同需求的用户保留扩展性，内置强大的插件系统，插件作者们可以专为 **Minecraft 游戏存档** 等特殊场景优化，是游戏玩家和高级用户的理想选择。

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


## 🛠️ 源码构建

**开发环境要求:**
- Visual Studio 2026
- .NET 8 SDK
- "Windows App SDK C# Templates" 工作负载

**构建步骤:**
1. 克隆仓库:
   ```bash
   git clone https://github.com/Leafuke/FolderRewind.git
   ```
2. 在 Visual Studio 中打开 `FolderRewind.slnx`。
3. 还原 NuGet 包。
4. 构建解决方案 (建议目标平台选择 `x64`)。

## 🤝 参与贡献
我们非常欢迎任何形式的贡献！如果您有好的想法，请提交 Pull Request。

1. Fork 本项目
2. 创建您的特性分支 (`git checkout -b feature/AmazingFeature`)
3. 提交您的修改 (`git commit -m 'Add some AmazingFeature'`)
4. 推送到分支 (`git push origin feature/AmazingFeature`)
5. 提交 Pull Request

## 📄 开源协议
本项目采用 **GPL 许可证**。详情请参阅 `LICENSE.txt` 文件。

## 💖 致谢
- **WinUI 3**: 提供美观的 UI 框架。
- **7-Zip**: 提供核心压缩技术支持。
- **[KnotLink](https://github.com/hxh230802/KnotLink)**: 提供本地互联服务，基本继承MineBackup的联动功能。感谢@hxh230802。

---
*为您的数字世界留一份后悔药。*

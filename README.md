# FolderRewind

[![中文说明](https://img.shields.io/badge/README-中文-blue)](README_zh.md)
[![Issues](https://img.shields.io/github/issues/Leafuke/FolderRewind?style=flat-square)](https://github.com/Leafuke/FolderRewind/issues)

**Time travel for your folders.**

FolderRewind is a modern, powerful, and user-friendly backup manager built with **WinUI 3** and **.NET 8**. It allows you to protect your important data—documents, project files, or game saves—by creating automated, versioned backups with ease.

As the spiritual successor to MineBackup, FolderRewind enhances its versatility while retaining extensibility for users with diverse needs. Featuring a powerful built-in plugin system, it allows plugin developers to optimize for specific scenarios such as **Minecraft game saves**, making it an ideal choice for gamers and advanced users.

## ✨ Key Features

- **🛡️ Reliable Backups**: Uses the industry-standard **7-Zip** engine for high-performance compression and encryption.
- **🤖 Automation**: Set it and forget it. Support for:
  - **Interval-based** backups (e.g., every 30 minutes).
  - **Scheduled** daily tasks using a 24-hour clock.
  - **On Startup** events to capture changes as soon as you log in.
- **🔌 Plugin System**: 
  - **Auto-Discovery**: Automatically scans and configures backups for known folder structures (e.g., Minecraft saves).
  - **Hot Backups**: Plugins can intervene to create snapshots before backing up locked files.
- **⏳ History Timeline**: View a clear timeline of your backups. "Rewind" your folder to any previous state.
- **🎨 Modern Design**: 
  - Native **Windows 11** aesthetic with Mica material.
  - Light & Dark theme support.
  - Responsive and intuitive UI.
- **🌍 Localization**: Full support for **English** and **Simplified Chinese (简体中文)**.

## 🔗 Download & Install

<a href="https://apps.microsoft.com/detail/9nwsdgxdqws4?referrer=appbadge&mode=direct">
	<img src="https://get.microsoft.com/images/en-us%20dark.svg" width="200"/>
</a>

## ✨ Officially Recognized Plugins

| Name               | Version   | Description                                     | Author          | Download Link                                      |
|----------------------|------|----------------------------------------|-------------|-------------------------------------------|
| MineRewind      | 1.2.0 | A backup plugin specifically designed for Minecraft game saves.               | Leafuke     | [Repository](https://github.com/Leafuke/FolderRewind-Plugin-Minecraft)

## 🛠️ Build from Source

**Requirements:**
- Visual Studio 2026
- .NET 8 SDK
- "Windows App SDK C# Templates" workload

## 🤝 Contributing
Contributions are welcome! Please feel free to submit a Pull Request.

If you want to develop plugins for FolderRewind to support more scenarios, you can refer to the [Plugin Development Guide](./docs/PluginDevelopmentGuide.md).


## 🔍 Discussion

If you are interested in discussing, you can join the QQ group.

<img src="./assets/qq_group_light.jpg" width="240px" />

## 💖 Acknowledgments

- [Windows App SDK](https://github.com/microsoft/windowsappsdk)
- [WinUI](https://github.com/microsoft/microsoft-ui-xaml)
- [KnotLink](https://github.com/hxh230802/KnotLink)
- [7-Zip](https://www.7-zip.org/)
- [MineBackup - Spiritual Predecessor](https://github.com/Leafuke/MineBackup)
- [Bili.Copilot - Reference](https://github.com/Richasy/Bili.Copilot)
- And all the other friends who provided help during development.

---
*Back up your world, one folder at a time.*

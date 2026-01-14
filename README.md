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
| MineRewind      | 1.0.0 | A backup plugin specifically designed for Minecraft game saves.               | Leafuke     | [Repository](https://github.com/Leafuke/FolderRewind-Plugin-Minecraft)

## 🛠️ Build from Source

**Requirements:**
- Visual Studio 2026
- .NET 8 SDK
- "Windows App SDK C# Templates" workload

**Steps:**
1. Clone the repository:
   ```bash
   git clone https://github.com/Leafuke/FolderRewind.git
   ```
2. Open `FolderRewind.slnx` in Visual Studio.
3. Restore NuGet packages.
4. Build the solution (Target `x64` for best compatibility).

## 🤝 Contributing
Contributions are welcome! Please feel free to submit a Pull Request.

1. Fork the Project
2. Create your Feature Branch (`git checkout -b feature/AmazingFeature`)
3. Commit your Changes (`git commit -m 'Add some AmazingFeature'`)
4. Push to the Branch (`git push origin feature/AmazingFeature`)
5. Open a Pull Request

## 📄 License
Distributed under the **GPL License**. See `LICENSE.txt` for more information.

## 💖 Acknowledgments
- **WinUI 3** for the beautiful UI framework.
- **7-Zip** for the compression technology.
- **[KnotLink](https://github.com/hxh230802/KnotLink)**: Offers local interconnection services, essentially inheriting the collaborative features of MineBackup. Thanks to @hxh230802 .

---
*Back up your world, one folder at a time.*

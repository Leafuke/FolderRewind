<p align="center">
<img src="assets/StoreLogo.png" width="48px"/>
</p>

<div align="center">

# FolderRewind

[![中文说明](https://img.shields.io/badge/README-中文-blue)](README_zh.md) [![GitHub release (latest by date)](https://img.shields.io/github/v/release/Leafuke/FolderRewind)](https://github.com/Leafuke/FolderRewind/releases) ![GitHub Release Date](https://img.shields.io/github/release-date/Leafuke/FolderRewind) ![GitHub stars](https://img.shields.io/github/stars/Leafuke/FolderRewind?style=flat) ![GitHub forks](https://img.shields.io/github/forks/Leafuke/FolderRewind)

</div>
<p align="center">
<a href="#Introduction">Introduction</a> &nbsp;&bull;&nbsp;
<a href="#Features">Features</a> &nbsp;&bull;&nbsp;
<a href="#Download">Download & Install</a> &nbsp;&bull;&nbsp;
<a href="#Usage">Usage</a> &nbsp;&bull;&nbsp;
<a href="#Officially Recognized Plugins">Plugins</a> &nbsp;&bull;&nbsp;
<a href="#Development">Development</a> &nbsp;&bull;&nbsp;
<a href="#Discussion">Discussion</a> &nbsp;&bull;&nbsp;
<a href="#Acknowledgments">Acknowledgments</a>
</p>

## Introduction

FolderRewind is a modern, powerful, and user-friendly backup manager built with **WinUI 3** and **.NET 10**. It allows you to protect your important data—documents, project files, or game saves—by creating automated, versioned backups with ease.

As the spiritual successor to MineBackup, FolderRewind enhances its versatility while retaining extensibility for users with diverse needs. Featuring a powerful built-in plugin system, it allows plugin developers to optimize for specific scenarios such as **Minecraft game saves**, making it an ideal choice for gamers and advanced users.

## Features

- **🛡️ Reliable Backups**: Uses the **7-Zip** engine for high-performance compression and encryption.
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

## Download

### Download from Microsoft Store (Recommended)：

<a href="https://apps.microsoft.com/detail/9nwsdgxdqws4?referrer=appbadge&mode=direct">
	<img src="https://get.microsoft.com/images/en-us%20dark.svg" width="200"/>
</a>

### Side-loading Installation：

1. Open System Settings, navigate to `System` -> `Developer Options`, and enable `Developer Mode`. Scroll to the bottom of the page, expand the `PowerShell` section, and enable the `Change Execution Policy...` option.
2. Open the [Release](https://github.com/Leafuke/FolderRewind/releases) page.
3. Find the application package in the latest version's **Assets**. The naming format is: `FolderRewind_{version}_{platform}.zip`.
4. After downloading and extracting the package, right-click the `install.ps1` script in the folder and select `Run with PowerShell`.

## Usage

First, you should understand several key concepts:

1. **Configuration**: The main interface of the software can contain many **independent** configuration cards. Each configuration can be considered as an independent **workspace**. Configurations have different **types**. The general configuration type is **Default**, while **plugins** can define new configuration types to adapt to specific scenarios (such as game saves). Each configuration can independently set backup rules, schedules, and retention policies.

2. **Folder**: A folder is the direct object and **smallest unit** that the software operates on. Each **configuration** can include multiple folders, and these folders are said to **belong** to that configuration. By clicking on a configuration card on the main interface, you can enter the specific configuration to back up and manage these folders.

3. **Task**: A task is a backup/restore/script operation performed by the software. You can set independent **automated tasks** (such as interval backups, scheduled backups, etc.) for each configuration. When the conditions are met, the software will automatically perform backup operations for all folders under that configuration.

Generally, the workflow is as follows:

1. **Create a configuration**. You need to select a suitable configuration type (e.g., Default) and name it.
2. **Add one or more folders to the configuration**. Custom scanning features via plugins are supported.
3. When a backup is needed, click on the configuration card to enter the configuration details interface, then click the **Backup Now** button. Alternatively, set up automated tasks to let the software perform backups automatically.

As you delve deeper into using the software, you’ll discover ways to simplify the process:

- Use keyboard shortcuts directly for backups.
- Mark folders as favorites for quick operations on the main interface.
- Create a **mini floating window** for a specific folder to monitor its status in real time and perform instant backups while working.

![Main Interface](assets/screenshot1.png)

## Officially Recognized Plugins

| Name               | Version   | Description                                     | Author          | Download Link                                      |
|----------------------|------|----------------------------------------|-------------|-------------------------------------------|
| MineRewind      | 1.2.0 | A backup plugin specifically designed for Minecraft game saves.               | Leafuke     | [Repository](https://github.com/Leafuke/FolderRewind-Plugin-Minecraft)

## Development

**Requirements:**
- Visual Studio 2026
- .NET 10 SDK
- "Windows App SDK C# Templates" workload

### Plugin Development

If you want to develop plugins for FolderRewind to support more scenarios, you can refer to the [Plugin Development Guide](./docs/PluginDevelopmentGuide.md).


## Discussion

If you are interested in discussing, you can join the QQ group.

<img src="./assets/qq_group_light.jpg" width="240px" />

## Acknowledgments

- [Windows App SDK](https://github.com/microsoft/windowsappsdk)
- [WinUI](https://github.com/microsoft/microsoft-ui-xaml)
- [KnotLink](https://github.com/hxh230802/KnotLink)
- [7-Zip](https://www.7-zip.org/)
- [MineBackup - Spiritual Predecessor](https://github.com/Leafuke/MineBackup)
- [Bili.Copilot - Reference](https://github.com/Richasy/Bili.Copilot)
- And all the other friends who provided help during development.

---
*Back up your world, one folder at a time.*

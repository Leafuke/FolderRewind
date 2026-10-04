[中文](README.md) | **English**

<p align="center">
<img src="FolderRewind/Assets/StoreLogo.png" width="48px"/>
</p>

<div align="center">

# FolderRewind

[![GitHub release (latest by date)](https://img.shields.io/github/v/release/Leafuke/FolderRewind)](https://github.com/Leafuke/FolderRewind/releases) ![GitHub Release Date](https://img.shields.io/github/release-date/Leafuke/FolderRewind) ![GitHub stars](https://img.shields.io/github/stars/Leafuke/FolderRewind?style=flat) ![GitHub forks](https://img.shields.io/github/forks/Leafuke/FolderRewind)

</div>
<p align="center">
<a href="#introduction">Introduction</a> &nbsp;&bull;&nbsp;
<a href="#features">Features</a> &nbsp;&bull;&nbsp;
<a href="#download">Download & Install</a> &nbsp;&bull;&nbsp;
<a href="#usage">Usage</a> &nbsp;&bull;&nbsp;
<a href="#officially-recognized-plugins">Plugins</a> &nbsp;&bull;&nbsp;
<a href="#development">Development</a> &nbsp;&bull;&nbsp;
<a href="#discussion">Discussion</a> &nbsp;&bull;&nbsp;
<a href="#acknowledgments">Acknowledgments</a>
</p>

## Introduction

FolderRewind is a modern, powerful, and user-friendly backup manager built with **WinUI 3** and **.NET 10**. It allows you to protect your important data—documents, project files, or game saves—by creating automated, versioned backups with ease.

As the spiritual successor to MineBackup, FolderRewind enhances its versatility while retaining extensibility for users with diverse needs. Featuring a powerful built-in plugin system, it allows plugin developers to optimize for specific scenarios such as **Minecraft game saves**, making it an ideal choice for gamers and advanced users.

## Features

- **🛡️ Backup Modes**: Uses **7-Zip-zstd** for Full backups, Smart incremental backups, and Rolling backups that create independent new archives from a trusted baseline. Configure compression, threads, filters, and skipping unchanged data.
- **🤖 Automation**: While the app is running, configure:
  - **Interval-based** backups (e.g., every 30 minutes).
  - **Scheduled** backups by month, day, hour, and minute.
  - **On Startup** backups when the app starts.
  - **Conditional** backups when a specified file changes from locked to unlocked.
- **🔌 Plugin System**: 
  - **Auto-Discovery**: Automatically scans and configures backups for known folder structures (e.g., Minecraft saves).
  - **Consistency and Restore Coordination**: Plugins can provide consistency sources, restore coordination, and player-data preservation for supported scenarios. Availability depends on the plugin and environment.
  - **Extensions**: Plugin System v3 supports `.frplugin` packages, an official catalog, plugin settings, commands, and custom backup representations. The host orchestrates and validates backup and restore operations.
- **⏳ History and Branches**: Browse recoverable versions per source, mark important versions, check out branches, and merge files with text comparison and conflict resolution. Minecraft region, chunk, and NBT semantic merging is currently unavailable.
- **☁️ Cloud Backups**: Connect to WebDAV, FTP, SFTP, OneDrive, S3, and other storage through **rclone**, or bridge cloud drives through **OpenList**. Prepare the tools and configure a connection first; history synchronization and archive uploads are separate operations.
- **🎨 Modern Design**: 
  - Native **Windows 11** aesthetic with Mica material.
  - Light & Dark theme support.
  - Responsive and intuitive UI.

## Download

Supports Windows 10 version 1809 or later and Windows 11. Current distributions target **x64 / ARM64**. GitHub Setup installers include the .NET and Windows App SDK runtimes.

### Download from Microsoft Store (Recommended):

<a href="https://apps.microsoft.com/detail/9nwsdgxdqws4?referrer=appbadge&mode=direct">
	<img src="https://get.microsoft.com/images/en-us%20dark.svg" width="200"/>
</a>

### GitHub Setup EXE Installation:

1. Open the [Release](https://github.com/Leafuke/FolderRewind/releases) page.
2. Download the installer and matching `.sha256` file for your device: `FolderRewind_{version}_Setup_x64.exe` for most Intel/AMD Windows PCs, or `FolderRewind_{version}_Setup_arm64.exe` for Windows on ARM.
3. Verify the checksum with PowerShell: `Get-FileHash .\FolderRewind_{version}_Setup_x64.exe -Algorithm SHA256` (replace the version and architecture), then run the installer.
4. The Chinese/English wizard defaults to current-user installation in `%LocalAppData%\Programs\FolderRewind`. You can choose all-user installation or another local path. All-user installation requires administrator privileges; Developer Mode and certificate import are not required.

Current GitHub Releases provide **Setup EXE installers and SHA-256 checksums only**, without standalone MSI, MSIX, or sideload archives. Production code signing is not included, so Windows may show an unknown-publisher or SmartScreen prompt. Download from this project's official Release page. If an older version cannot detect Setup updates, download the installer manually.

Store updates are managed by Microsoft Store, and its version may differ from GitHub. Setup and Store/legacy MSIX use separate data directories; switching channels does not automatically migrate configurations or plugins. Back up your data and follow the [migration guide](https://folderrewind.top/en/docs/guides/data-migration). Do not run different channels against the same sources simultaneously.

### Version 1.9 upgrade notes

- The 1.9 series uses Plugin System v3; the current source targets Plugin API **3.6**. The app, plugins, and SDK have independent versions. Legacy v2 plugins cannot load as v3 plugins; old payloads move into a recoverable quarantine.
- Newly installed plugins default to disabled and run only after explicit enablement. Plugins execute in the app process, so enable trusted plugins only; package validation and service declarations are not a security sandbox.
- Legacy cloud configurations and archives do not automatically migrate to the new history system. Keep old archives and incremental dependencies, reconnect storage, and verify recovery; see the [cloud archive guide](https://folderrewind.top/en/docs/guides/cloud-archive).

## Usage

1. Create a backup project, add source folders, and select a separate backup destination.
2. Create a manual backup and verify restoration to a test directory before enabling automatic backups or cloud uploads.
3. Choose a recoverable version in History to restore. Review scope and conflicts before using branches and merges.

See the [official documentation](https://folderrewind.top/en/docs/intro) for detailed steps.

## Officially Recognized Plugins

| Name               | Description                                     | Author          | Download Link                                      |
|----------------------|----------------------------------------|-------------|-------------------------------------------|
| MineRewind | Minecraft Java / Bedrock save discovery and backup. Java adds consistency and restore coordination; Bedrock uses ordinary file backups with a close-game warning. | Leafuke | [Repository and Downloads](https://github.com/Leafuke/FolderRewind-Plugin-Minecraft/releases) |

Installers include a MineRewind v3 package. Check its version and enabled state in plugin management.

## Development

**Requirements:**

- Visual Studio 2026
- .NET 10 SDK
- `.NET Desktop Development` and `WinUI Application Development` workloads

Open `FolderRewind.slnx` and select x64 or ARM64. The current project uses Windows App SDK **2.5.1**. Prepare an architecture-matched `7za.exe` (using `.github/scripts/Stage-SevenZip.ps1`, for example) and specify its path with `SevenZipExecutable`. See the [Installer guide](Installer/README.md) and [build workflow](.github/workflows/build-github-sideload.yml) for Setup builds.

### Plugin Development

Plugins reference the independent `FolderRewind.Plugin.Abstractions` contracts rather than the app or UI projects. The current contract package is **3.6.0**, targeting API **3.6**. See the [Plugin Development Guide](https://folderrewind.top/en/docs/plugins/overview), [repository v3 guide](docs/plugin-v3/README.md), and [SDK guide](FolderRewind.Plugin.Abstractions/README.md).


## Discussion

If you are interested in discussing, you can join the QQ group.

<img src="./FolderRewind/Assets/qq_group_light.jpg" width="240px" />

## Acknowledgments

- [Windows App SDK](https://github.com/microsoft/windowsappsdk)
- [WinUI](https://github.com/microsoft/microsoft-ui-xaml)
- [Windows Community Toolkit](https://github.com/CommunityToolkit/Windows)
- [KnotLink](https://github.com/KnotLink-Protocol/KnotLink)
- [7-Zip](https://www.7-zip.org/)
- [7-Zip-zstd](https://github.com/mcmilk/7-Zip-zstd)
- [MineBackup - Spiritual Predecessor](https://github.com/Leafuke/MineBackup)
- [Bili.Copilot - Reference](https://github.com/Richasy/Bili.Copilot)
- And all the other friends who provided help during development.

---
*Back up your world, one folder at a time.*

[中文](README.md) | **English**

<p align="center">
<img src="src/FolderRewind/Assets/StoreLogo.png" width="64" alt="FolderRewind icon" />
</p>

<div align="center">

# FolderRewind

[![GitHub release (latest by date)](https://img.shields.io/github/v/release/Leafuke/FolderRewind)](https://github.com/Leafuke/FolderRewind/releases) ![GitHub Release Date](https://img.shields.io/github/release-date/Leafuke/FolderRewind) ![GitHub stars](https://img.shields.io/github/stars/Leafuke/FolderRewind?style=flat) ![GitHub forks](https://img.shields.io/github/forks/Leafuke/FolderRewind)

</div>

<p align="center">
Keep recoverable versions of your documents, projects, and game saves.
<br />
<a href="https://folderrewind.top/en/docs/intro">Documentation</a> &nbsp;&bull;&nbsp;
<a href="https://apps.microsoft.com/detail/9nwsdgxdqws4">Microsoft Store</a> &nbsp;&bull;&nbsp;
<a href="https://github.com/Leafuke/FolderRewind/releases">GitHub Downloads</a> &nbsp;&bull;&nbsp;
<a href="https://github.com/Leafuke/FolderRewind/issues">Report an Issue</a>
</p>

<p align="center">
<a href="#introduction">Introduction</a> &nbsp;&bull;&nbsp;
<a href="#features">Features</a> &nbsp;&bull;&nbsp;
<a href="#download">Download & Install</a> &nbsp;&bull;&nbsp;
<a href="#usage">Usage</a> &nbsp;&bull;&nbsp;
<a href="#plugins-and-minecraft">Plugins</a> &nbsp;&bull;&nbsp;
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
- **⏳ History and Branches**: Browse recoverable versions per source, mark important versions, check out branches, and merge files with text comparison and conflict resolution.
- **☁️ Cloud Backups**: Connect to WebDAV, FTP, SFTP, OneDrive, S3, and other storage through **rclone**, or bridge cloud drives through **OpenList**. Prepare the tools and configure a connection first; history synchronization and archive uploads are separate operations.
- **🎨 Modern Design**: 
  - Native **Windows 11** aesthetic with Mica material.
  - Light & Dark theme support.

## Download

Supports Windows 10 version 1809 or later and Windows 11. Current distributions target **x64 / ARM64**. GitHub Setup installers include the .NET and Windows App SDK runtimes.

### Microsoft Store (Recommended)

<a href="https://apps.microsoft.com/detail/9nwsdgxdqws4?referrer=appbadge&mode=direct">
	<img src="https://get.microsoft.com/images/en-us%20dark.svg" width="200" alt="Get it from Microsoft Store" />
</a>

### GitHub Setup Installer

1. Open the [Release](https://github.com/Leafuke/FolderRewind/releases) page.
2. Download the installer and matching `.sha256` file for your device: `FolderRewind_{version}_Setup_x64.exe` for most Intel/AMD Windows PCs, or `FolderRewind_{version}_Setup_arm64.exe` for Windows on ARM.
3. Verify the checksum with PowerShell: `Get-FileHash .\FolderRewind_{version}_Setup_x64.exe -Algorithm SHA256` (replace the version and architecture), then run the installer.
4. The Chinese/English wizard defaults to current-user installation in `%LocalAppData%\Programs\FolderRewind`. You can choose all-user installation or another local path. All-user installation requires administrator privileges; Developer Mode and certificate import are not required.

Current GitHub Releases provide **Setup EXE installers and SHA-256 checksums only**, without standalone MSI, MSIX, or sideload archives. Production code signing is not included, so Windows may show an unknown-publisher or SmartScreen prompt. Download from this project's official Release page. If an older version cannot detect Setup updates, download the installer manually.

Store updates are managed by Microsoft Store, and its version may differ from GitHub. Setup and Store/legacy MSIX use separate data directories; switching channels does not automatically migrate configurations or plugins. Back up your data and follow the [migration guide](https://folderrewind.top/en/docs/guides/data-migration). Do not run different channels against the same sources simultaneously.

## Usage

1. Create a backup project, add source folders, and select a separate backup destination.
2. Create a manual backup and verify restoration to a test directory before enabling automatic backups or cloud uploads.
3. Choose a recoverable version in History to restore. Review scope and conflicts before using branches and merges.

| What you want to do next | Documentation |
| --- | --- |
| Install the app and complete your first backup and restore | [Quick Start](https://folderrewind.top/en/docs/intro) |
| Configure remote storage and cloud copies | [Cloud Archives](https://folderrewind.top/en/docs/guides/cloud-archive) |
| Protect Minecraft worlds | [Minecraft Guide](https://folderrewind.top/en/docs/guides/minecraft/overview) |
| Move to another PC or installation channel | [Data Migration](https://folderrewind.top/en/docs/guides/data-migration) |

## Plugins and Minecraft

| Name               | Description                                     | Author          | Download Link                                      |
|----------------------|----------------------------------------|-------------|-------------------------------------------|
| MineRewind | Minecraft Java / Bedrock save discovery and backup. Java adds maps, region scopes, lossless incremental representations, and semantic merging. Bedrock uses ordinary file backups; close the game first. | Leafuke | [Repository and Downloads](https://github.com/Leafuke/FolderRewind-Plugin-Minecraft/releases) |

## Development

### Build from Source

- Windows and the WinUI development toolchain. For Visual Studio, install the `.NET Desktop Development` and `WinUI Application Development` workloads.
- .NET 10 SDK; [global.json](global.json) specifies the version.
- Initial compression-tool preparation requires `7z.exe` on PATH for extraction. The preparation script downloads and verifies an architecture-matched `7za.exe`.

Run from the repository root:

```powershell
$sevenZip = ./scripts/build/Stage-SevenZip.ps1 -Platform x64 | Select-Object -Last 1
dotnet restore ./FolderRewind.slnx -p:Platform=x64
dotnet build ./FolderRewind.slnx -c Debug -p:Platform=x64 -p:GenerateAppxPackageOnBuild=false "-p:SevenZipExecutable=$sevenZip" --no-restore
```

For ARM64, change the platform to `ARM64` in tool preparation, restore, and build commands. Dependency versions are maintained in [Directory.Packages.props](Directory.Packages.props), [Directory.Build.props](Directory.Build.props), and project declarations; avoid pinning versions from older tutorials.

### Repository Layout and Verification

| Path | Responsibility |
| --- | --- |
| `src/FolderRewind/` | WinUI app, UI, configuration, and lifecycle adapters |
| `src/FolderRewind.Core/` | Windows core implementation without WinUI dependencies |
| `src/FolderRewind.Plugin.Abstractions/` | Public, BCL-only plugin contracts |
| `src/FolderRewind.Plugin.Runtime/` | Plugin loading, capability routing, lifecycle, and controlled artifact operations |
| `tests/` | Core, host adapter, plugin contract, and runtime tests |
| `scripts/`, `Installer/` | Build, verification, and distribution tooling |
| `docs/` | Domain terminology, effective ADRs, and protocol/security specifications |

```powershell
./scripts/test/Test-RepositoryLayout.ps1
./scripts/test/Invoke-SetupCoreTests.ps1
```

The core test runner prepares pinned, hash-verified tools and sequentially checks plugin contracts, the runtime, host behavior, and fault recovery using isolated directories and local cloud backends. Normal tests reference production Core; recovery acceptance uses isolated outputs through `FolderRewindFaultInjection=true`.

The website, MineRewind, Catalog, and templates are maintained separately; ordinary Host builds do not require their sources. See [development and verification](https://folderrewind.top/en/docs/architecture/development), [contribution rules](CONTRIBUTING.md), [Host constraints](docs/README.md), [installer entry points](Installer/README.md), and the [Setup release workflow](.github/workflows/release-setup.yml).

### Plugin Development

Plugins reference only the independent `FolderRewind.Plugin.Abstractions` contracts, without references to the app, Core, or Runtime. Do not bundle the host Abstractions DLL in `.frplugin` packages.

The host owns configuration persistence, orchestration, history, transactions, integrity, encryption, retention, cloud copies, and target directory writes. Plugins own domain semantics. See the [plugin documentation](https://folderrewind.top/en/docs/plugins/overview) and [SDK guide](src/FolderRewind.Plugin.Abstractions/README.md).

## Discussion

Report bugs or request features through [Issues](https://github.com/Leafuke/FolderRewind/issues), including app/system versions, reproduction steps, and expected behavior. You can also join the QQ group or read about [supporting the project](https://folderrewind.top/en/docs/support).

<img src="src/FolderRewind/Assets/qq_group_light.jpg" width="240" alt="QR code for the FolderRewind QQ group" />

## License

See [LICENSE](LICENSE.txt), [third-party notices](THIRD-PARTY-NOTICES.md), and [privacy](PRIVACY.md).

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

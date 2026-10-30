[中文](README.md) | **English**

# FolderRewind

<img src="src/FolderRewind/Assets/StoreLogo.png" width="64" alt="FolderRewind" />

[![GitHub Release](https://img.shields.io/github/v/release/Leafuke/FolderRewind)](https://github.com/Leafuke/FolderRewind/releases)

A Windows backup manager built with WinUI 3 and .NET 10. Protect folders and game saves with versioned backups, history and restore, automation, cloud replicas, and independently maintained plugins and templates.

- [Microsoft Store](https://apps.microsoft.com/detail/9nwsdgxdqws4) · [GitHub Setup downloads](https://github.com/Leafuke/FolderRewind/releases)
- [User documentation](https://folderrewind.top/en/docs/intro) · [Installation](https://folderrewind.top/en/docs/getting-started/installation) · [Data migration](https://folderrewind.top/en/docs/guides/data-migration)
- [Plugin usage and development](https://folderrewind.top/en/docs/plugins/overview) · [MineRewind](https://github.com/Leafuke/FolderRewind-Plugin-Minecraft)
- [Support and Sponsor Edition](https://folderrewind.top/en/docs/support) · [Issues](https://github.com/Leafuke/FolderRewind/issues)

## Develop from source

Use Windows with the WinUI development toolchain. `global.json` selects the SDK; `Directory.Packages.props` and project overrides declare package versions. Production projects live in `src/`, tests in `tests/`. See the [1.10 development layout](https://folderrewind.top/en/docs/architecture/directory-structure).

```powershell
$sevenZip = ./scripts/build/Stage-SevenZip.ps1 -Platform x64 | Select-Object -Last 1
dotnet restore ./FolderRewind.slnx -p:Platform=x64
dotnet build ./FolderRewind.slnx -c Debug -p:Platform=x64 -p:GenerateAppxPackageOnBuild=false "-p:SevenZipExecutable=$sevenZip" --no-restore
./scripts/test/Test-RepositoryLayout.ps1
./scripts/test/Invoke-SetupCoreTests.ps1
```

The first preparation requires `7z.exe` on PATH for extraction. Prepare an architecture-matched `7za.exe` using `scripts/build/Stage-SevenZip.ps1` and pass its path as `SevenZipExecutable`. The core test runner prepares pinned, hash-verified test tools and uses isolated data for real archive, local cloud-backend and fault-recovery checks.

See [development and verification](https://folderrewind.top/en/docs/architecture/development), [contribution rules](CONTRIBUTING.md), [Host constraints](docs/README.md) and [installer entry points](Installer/README.md). The website, MineRewind, Catalog and templates are separate repositories; ordinary Host builds do not require their sources.

## License and acknowledgments

See [LICENSE](LICENSE.txt), [third-party notices](THIRD-PARTY-NOTICES.md) and [privacy](PRIVACY.md). Thanks to contributors, users and sponsors, and to .NET, WinUI, 7-Zip/7-Zip-zstd, CommunityToolkit and the other dependencies.

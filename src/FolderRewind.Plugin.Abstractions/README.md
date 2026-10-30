# FolderRewind Plugin Abstractions

Public, BCL-only contracts for FolderRewind Plugin API 3. Plugins reference this package, never the application, Core or Runtime projects.

The target framework is `net10.0`. The package version is declared by its project; assembly identity remains `FolderRewind.Plugin.Abstractions, Version=3.0.0.0` throughout API 3.x. A Host accepts a matching API major and a requested minor no newer than its own. Source development versions do not imply that a package is already publicly available.

Plugins register declared capabilities during activation. The Host owns configuration persistence, operations, history, transactions, integrity, encryption, retention, cloud ordering and target writes. In-process capability declarations gate formal Host services and disclose intent; they do not sandbox arbitrary .NET or OS access.

Distribute root-manifest `.frplugin` packages without the Host Abstractions DLL. See the [plugin guide](https://folderrewind.top/en/docs/plugins/overview) for capability, lifecycle, settings and packaging documentation. Public API changes must pass the contract baseline tests.

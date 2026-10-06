# Third-party installer components

FolderRewind is distributed under the repository's GNU GPLv3 license. Installer themes and native WiX utilities retain their separate Microsoft Reciprocal License (MS-RL).

- WiX Toolset SDK, WixStdBA, BootstrapperApplicationApi, BAFunctions API and DUtil: 7.0.0. Copyright .NET Foundation and contributors. License: MS-RL. Sources: https://github.com/wixtoolset/wix/tree/v7.0.0 . The native restore script records and verifies the upstream NuGet SHA-512 hashes. See LICENSE.WiX.txt.
- 7-Zip-zstd: v26.02-v1.5.7-R2. Sources and licensing: https://github.com/mcmilk/7-Zip-zstd/releases/tag/v26.02-v1.5.7-R2 . The staging script verifies the architecture-specific installer SHA-256 and records the standalone executable SHA-256. The upstream archive includes License.txt.
- .NET 10 and Windows App SDK dependencies retain the licenses supplied by their upstream packages. This distribution is self-contained; these components are included in the application payload.

The customized theme and localization retain the upstream copyright notices. The GPL license shown by setup is the complete repository license, with no placeholder text.

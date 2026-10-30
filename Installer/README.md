# 安装载荷与 Setup 入口

安装器源码位于本目录；可执行脚本位于 `scripts/build`、`scripts/test` 和 `scripts/release`。完整开发说明见[网站](https://folderrewind.top/docs/architecture/development#packaging)。

```powershell
$sevenzip = ./scripts/build/Stage-SevenZip.ps1 -Platform x64 | Select-Object -Last 1
dotnet publish src/FolderRewind/FolderRewind.csproj -c Release -p:Platform=x64 -p:FolderRewindDistributionChannel=Msi -p:CETCompat=false -p:DebugType=None -p:DebugSymbols=false -p:SevenZipExecutable="$sevenzip" -o artifacts/msi-publish/x64
./scripts/build/Normalize-MsiResourcePaths.ps1 -Directory artifacts/msi-publish/x64
./scripts/test/Test-MsiPayload.ps1 -Directory artifacts/msi-publish/x64
```

ARM64 使用对应平台和压缩器。`Prepare-MsiPackage.ps1` 接收显式的项目、载荷、版本、架构及输出目录；版本必须符合 MSI 约束并与来源一致。正式发布入口仍为 Release Setup 工作流，它会校验项目与包清单版本、测试结果和实际产物哈希。发布说明只读取 `.github/release-notes/v<版本>.md`；缺失时保留既有 GitHub 自动生成行为。

公开发布物为 Setup EXE 与 SHA-256；MSI 是内部载荷。构建和检查不等于发布，也不等于安装体验验收。安装测试使用 `TestIdentity` 及独立数据根，不得针对用户现有安装执行隔离测试或清理。

保留 [WiX 许可](LICENSE.WiX.txt)、[7-Zip 许可](LICENSE.7-Zip.txt)与[第三方声明](THIRD-PARTY-NOTICES.md)。历史命令的兼容入口只转发到现有实现。

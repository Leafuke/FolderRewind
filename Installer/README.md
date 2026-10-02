# MSI 和离线 Setup 构建

本轮修复版本为 1.9.1.0。Windows Installer 版本的前三段必须递增，范围为 255.255.65535，第四段固定为 0。MSI UpgradeCode 保持原值。

**完整发行验收尚未通过。** 本机故障升级/卸载的 MSI 注册回滚失败，正常卸载的自启动清理补测失败，可见 Burn 安装位置为空。构建成功和本机部署成功不能代替这些门禁；详见 [最终验收报告](../docs/release/msi-implementation-acceptance-2026-10-02.md)。不要发行隔离测试夹具或 99.x 测试版本。

## 本地构建

```powershell
.github/scripts/Generate-InstallerArtwork.ps1
$sevenzip = .github/scripts/Stage-SevenZip.ps1 -Platform x64 | Select-Object -Last 1
dotnet publish FolderRewind/FolderRewind.csproj -c Release -p:Platform=x64 -p:FolderRewindDistributionChannel=Msi -p:CETCompat=false -p:DebugType=None -p:DebugSymbols=false -p:SevenZipExecutable="$sevenzip" -o artifacts/msi-publish/x64 /warnaserror
.github/scripts/Prepare-MsiPackage.ps1 -ProjectPath Installer/FolderRewind.Installer.wixproj -PublishDirectory artifacts/msi-publish/x64 -Version 1.9.1.0 -Platform x64 -OutputDirectory artifacts/release/x64
```

ARM64 使用同名平台参数和对应 7za.exe。打包之前校验所有 PRI 文件引用；第三方 SDK 的文件名大小写在暂存目录规范化。公开插件契约保持 AssemblyVersion 3.0.0.0，应用版本参数不会传播到该项目。

安装入口是 `FolderRewind_1.9.1.0_Setup_<arch>.exe`。界面可以选择简体中文、English；支持 `-lang 1033`、`-lang 2052` 和相应语言名称。当前用户是新装默认范围；所有用户范围使用提升后的 Windows Installer 引擎。应用以登录用户的普通权限启动。`/quiet` 和 `/passive` 不选择语言、不自动启动应用。

独立 MSI 使用 `TRANSFORMS=:zh-CN.mst` 选择内嵌中文转换。英文、中文保持同一 ProductCode 和组件载荷。同版本维护沿用 Windows Installer 注册的转换。跨范围安装必须先卸载，用户配置、插件和备份保留。

运行中的 FolderRewind 必须通过托盘“退出”正常关闭后才能安装、升级、维修或卸载。关闭流程停止自动化，等待当前任务，保存配置并排空写入队列，再释放实例锁。安全模式重启使用同一流程。

## 验证和隔离

`Test-MsiInstallation.ps1` 生成独立的测试 UpgradeCode、ProductCode、注册表位置和快捷方式名称。应用通过子进程环境变量 `FOLDERREWIND_TEST_DATA_ROOT` 使用独立配置；正式启动不设置该变量。脚本在执行任何 MSI 前核对测试身份，拒绝生产身份。测试夹具及故障注入不能作为发行资产。

每轮测试还使用独立的注册表后缀和 ProductCode，避免复用失败回滚留下的测试标记。默认执行故障回滚门禁；`-SkipRollbackProbes` 仅用于继续收集其他场景的证据，结果会明确记录未执行，不能作为完整发行验收通过。

```powershell
.github/scripts/Test-MsiInstallation.ps1 -MsiPath artifacts/release/x64/FolderRewind_1.9.1.0_x64.msi -PublishDirectory artifacts/msi-publish/x64 -ResultDirectory artifacts/msi-validation
```

发布构建恢复 MSI 验证。初次绑定中的 PE 语言元数据在构建后规范化，再执行完整 ICE03/ICE60 等验证。ICE57 无法静态描述 HKMU 和标准快捷方式目录的双范围重定向；ICE105 不识别机器卸载动作的 ALLUSERS 条件。这两项单独排除，机器动作的执行类型和确切条件由构建脚本额外核对，未全局关闭验证。

## 既有任务栏入口

此版本使用稳定 MSI AUMID `Leafuke.FolderRewind.Msi`。旧版固定入口可能仍缓存旧图标或缺少身份。取消固定旧的 FolderRewind 快捷方式，再从新开始菜单入口重新固定。应用检测到旧入口时显示一次迁移说明，不修改用户固定项。

安装器不改变应用语言，也不默认启用自启动。系统禁用的启动项应在 Windows“启动应用”设置中重新启用。卸载仅清理指向本安装目录的 Run 项和入口，不删除用户数据。

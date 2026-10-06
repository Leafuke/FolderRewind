# MSI 和离线 Setup 构建

当前发行版本为 1.9.4.0。Windows Installer 版本的前三段必须递增，范围为 255.255.65535，第四段固定为 0。MSI UpgradeCode 保持原值。

GitHub 正式发布使用 **Release Setup**：选择分支后点击一次 **Run workflow**，自动执行核心测试、x64/ARM64 构建、检查和公开 Latest。无需填写版本、运行 ID 或人工验收理由。版本来自 manifest，并与应用项目核对；更新说明优先读取 `docs/release/FolderRewind-<三段版本>-notes.md`，其次读取 `.github/release-notes/v<三段版本>.md`，缺失则由 GitHub 自动生成。

手动触发表示执行正式发布；安装体验和历史故障诊断不作为每次发行的人工门禁，也不改写为自动通过。新版本 2 报告只记录本次实际检查，并标记安装体验 `not-run`。历史版本 1 验收工具继续可用，状态见 [1.9.2.0 阶段验收报告](../docs/release/exe-repair-1.9.2-acceptance-2026-10-02.md)。不要发行隔离测试夹具或 99.x 测试版本。

## 本地构建

```powershell
.github/scripts/Generate-InstallerArtwork.ps1
$sevenzip = .github/scripts/Stage-SevenZip.ps1 -Platform x64 | Select-Object -Last 1
dotnet publish FolderRewind/FolderRewind.csproj -c Release -p:Platform=x64 -p:FolderRewindDistributionChannel=Msi -p:CETCompat=false -p:DebugType=None -p:DebugSymbols=false -p:SevenZipExecutable="$sevenzip" -o artifacts/msi-publish/x64 /warnaserror
.github/scripts/Prepare-MsiPackage.ps1 -ProjectPath Installer/FolderRewind.Installer.wixproj -PublishDirectory artifacts/msi-publish/x64 -Version 1.9.4.0 -Platform x64 -OutputDirectory artifacts/installer-packages/x64
.github/scripts/Prepare-SetupReleaseAssets.ps1 -SourceDirectory artifacts/installer-packages/x64 -Version 1.9.4.0 -Platform x64 -OutputDirectory artifacts/release/x64
```

ARM64 使用同名平台参数和对应 7za.exe。打包之前校验所有 PRI 文件引用；第三方 SDK 的文件名大小写在暂存目录规范化。公开插件契约保持 AssemblyVersion 3.0.0.0，应用版本参数不会传播到该项目。

安装入口是 `FolderRewind_1.9.2.0_Setup_<arch>.exe`。界面可以选择简体中文、English；支持 `-lang 1033`、`-lang 2052` 和相应语言名称。当前用户是新装默认范围；所有用户范围使用提升后的 Windows Installer 引擎。应用以登录用户的普通权限启动。`/quiet` 和 `/passive` 不选择语言、不自动启动应用。

GitHub Release **仅发布 Setup EXE 安装包和对应 SHA-256**，不发布独立 MSI、MSIX 或侧载压缩包。新客户端仅选择匹配架构的 Setup EXE；没有匹配 EXE 时打开 Release 页面，不回退下载 MSI。旧客户端的 MSI 自动下载兼容入口已按此发行政策取消。

MSI 仅为 Burn 内嵌载荷及构建/验收中间产物，放在 `artifacts/installer-packages/`，不进入公开暂存目录。内部 MSI 的 `TRANSFORMS=:zh-CN.mst` 用于选择内嵌中文转换。英文、中文保持同一 ProductCode 和组件载荷。同版本维护沿用 Windows Installer 注册的转换。跨范围安装必须先卸载，用户配置、插件和备份保留。

运行中的 FolderRewind 必须通过托盘“退出”正常关闭后才能安装、升级、维修或卸载。关闭流程停止自动化，等待当前任务，保存配置并排空写入队列，再释放实例锁。安全模式重启使用同一流程。

## 验证和隔离

`Test-MsiInstallation.ps1` 生成独立的测试 UpgradeCode、ProductCode、注册表位置、快捷方式名称和快捷方式 AUMID。测试快捷方式使用对应 `PackageIdentifier`，避免失败测试留下的无效入口与正式版共用任务栏身份。应用通过子进程环境变量 `FOLDERREWIND_TEST_DATA_ROOT` 使用独立配置；正式启动不设置该变量。脚本在执行任何 MSI 前核对测试身份，拒绝生产身份。测试夹具及故障注入不能作为发行资产。

每轮测试还使用独立的注册表后缀和 ProductCode，避免复用失败回滚留下的测试标记。默认执行故障回滚门禁；`-SkipRollbackProbes` 仅用于继续收集其他场景的证据，结果会明确记录未执行，不能作为完整发行验收通过。

```powershell
.github/scripts/Test-MsiInstallation.ps1 -MsiPath artifacts/installer-packages/x64/FolderRewind_1.9.2.0_x64.msi -PublishDirectory artifacts/msi-publish/x64 -ResultDirectory artifacts/msi-validation
```

发布构建恢复 MSI 验证。初次绑定中的 PE 语言元数据在构建后规范化，再执行完整 ICE03/ICE60 等验证。ICE57 无法静态描述 HKMU 和标准快捷方式目录的双范围重定向；ICE105 不识别机器卸载动作的 ALLUSERS 条件。这两项单独排除，机器动作的执行类型和确切条件由构建脚本额外核对，未全局关闭验证。

## 既有任务栏入口

1.9.3.0 使用稳定 MSI AUMID `Leafuke.FolderRewind.Msi`，窗口和快捷方式带有重新启动命令、图标和名称。快捷方式的普通图标使用安装目录中的 EXE 索引 0；任务栏 `RelaunchIconResource` 使用 `Assets\MsiApp.ico,0`，避免依赖旧版 Installer 图标缓存。该属性引用 EXE/DLL 时要求负数资源 ID，不能使用普通快捷方式的 `exe,0` 索引格式，否则运行中的任务栏按钮会显示空白文件图标。启动时仅修复目标路径精确匹配当前 EXE 的固定入口，包括旧的错误资源格式，保留参数和固定顺序；Store 或其他安装路径的入口保持原样，仍可从新开始菜单入口手动重新固定。

安装器不改变应用语言，也不默认启用自启动。系统禁用的启动项应在 Windows“启动应用”设置中重新启用。卸载仅清理指向本安装目录的 Run 项和入口，默认保留用户数据。

### 卸载时同时清除设置

Setup 维护页提供默认不勾选的“卸载时同时清除当前用户的应用设置（备份保留）”。仅在整个卸载成功后，删除运行 Setup 的当前账户 `%LOCALAPPDATA%\FolderRewind` 根目录中的 `config.json`、`config.json.bak` 和 `config.json.recovery.*.json`。不递归删除目录，保留备份、历史、插件及其他文件；其他用户的数据不清理。维修、取消或失败不执行清理，直接调用内部 MSI 也不执行该选项。仍有当前会话的应用实例运行或路径无法安全核实时，保留设置并记录错误。

静默卸载需显式指定 `Setup.exe /uninstall /quiet ClearSettingsChosen=1`。省略此参数会保留设置。删除设置后，重新安装需重新配置已有备份路径，但备份文件仍在原处。

隔离验证可运行 `Test-MsiBundleInstallation.ps1 -FixtureMsi <测试MSI> -ResultDirectory <证据目录> -ClearSettings`；不加开关验证默认保留设置。两者均使用新建的合成数据目录。

## 1.9.2.0 隔离诊断与冻结候选包

每轮 TestIdentitySuffix 派生独立 MSI/Bundle family，夹具产品名、组件注册位置、目录和配置独立。生产包不含故障动作，生产原生 DLL 不导出 FailForTesting。故障输入为 cleanup/files/registration；仅测试 Bundle 能将 FolderRewindTestFail 传入测试 MSI。

生命周期测试使用增删改载荷，验证完整文件集合、原 ProductCode、版本、安装上下文、Feature 和缓存包。不能只检查退出码或 EXE 哈希。故障后的 advertised 记录必须核对具体身份；不得删除 Installer 注册表或放宽 ACL。

```powershell
.github/scripts/Test-InstallerNative.ps1 -ResultDirectory artifacts/native-validation
.github/scripts/Test-InstallerRollbackControl.ps1 -ResultDirectory artifacts/rollback-control-fresh
.github/scripts/Test-MsiBundleInstallation.ps1 -FixtureMsi <本轮测试MSI> -ResultDirectory artifacts/bundle-smoke
.github/scripts/Test-MsiBundleInstallation.ps1 -FixtureMsi <本轮测试MSI> -ResultDirectory artifacts/bundle-foreign -StartupProbeMode Foreign
.github/scripts/Test-MsiBundleInstallation.ps1 -FixtureMsi <本轮测试MSI> -ResultDirectory artifacts/bundle-fault -IncludeFailureProbe
```

默认 Bundle 脚本验证正常生命周期；**Installer Diagnostics** 手动工作流执行两架构未签名 MSIX 回归、Setup 检查，以及 x64 隔离生命周期、边界和故障探针。该工作流不发布 Release，也不阻塞 Release Setup。SkipRollbackProbes 只用于诊断，生命周期脚本末尾仍因必测项目未执行而失败。本机隔离身份测试不等于干净 VM 验收。

当前用户 Setup 在 MSI 卸载前核对已登记产品及精确 Run 命令，只在整个 Burn 卸载成功后清理仍匹配的用户启动值；失败或取消时不执行这次额外清理。InstallLocation 同样在成功提交后由 Burn 写入自己的用户卸载项。机器范围保留有所有权校验、日志及回滚的提升 MSI 动作。这用于处理本机 MSI 动作读不到 Burn/调用者新写用户注册值的现象，不表示 Installer 注册回滚已修复。

Release Setup 固定使用触发时的提交。预检查之后，三个核心测试项目与两架构安装包构建并行；正式链路每架构仅执行一次自包含 publish，不构建 MSIX，不等待完整 CI 的压力测试或其他架构回归。NuGet 包和按脚本内容区分的 WiX 原生依赖可缓存，bin/obj 和安装包不缓存。两架构使用预检查解析的同一个 7-Zip 上游版本。

汇总任务下载本次运行冻结的 EXE 和证据，生成 `setup-release-report`，检查版本、干净提交、运行 ID、测试结果和包 SHA-256 后创建草稿；上传四个文件、核验远程 digest 后自动公开为 Latest。已有文件内容不同、标签指向其他提交、混入其他附件或无法校验时拒绝发布；不使用 `--clobber`。上传中断保留草稿，使用 GitHub 的 **Re-run failed jobs** 复用冻结文件继续上传。不要重新运行全部构建来替换已有草稿中的不同字节。

同一仓库的正式发布串行运行。相同版本、提交已公开且四个附件与校验文件可验证时，预检查直接成功结束，不再构建。发布摘要包含提交、附件哈希、阶段耗时和 Release 链接。部署工作流时需同步默认分支与需要发布的版本分支。

```powershell
.github/scripts/Invoke-SetupCoreTests.ps1
.github/scripts/Build-SetupCandidate.ps1 -Platform x64 -Version 1.9.4.0 -SevenZipRelease <上游版本标签>
# ARM64 在独立的干净工作区用相同 SevenZipRelease 构建。
```

候选生成器的 `IdentityOnly` 模式保留 ProductCode、BundleCode、签名状态、内嵌 MSI 哈希和源码快照，只输出身份文件，不生成旧式待人工填写清单。历史调用不加此开关时仍生成版本 1 验收文件。公开目录只有两架构 Setup EXE 及各自校验文件。未提交工作区可交付诊断候选包，但 `sourceDirty=true`，不能通过远程发行门禁。

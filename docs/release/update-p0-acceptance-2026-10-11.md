# 更新系统 P0 验收记录

日期：2026-10-11；分支：1.9.x。修复范围为主程序更新链接边界和 Setup 资源选择；不包含下载验真、重试状态、自动安装或数据迁移。

## 已完成的行为

- `AppUpdatePolicy` 精确解析三段正式标签和四段 `.0` 安装包版本；只有合法、非草稿、非预发行且高于当前版本的元数据产生通知。
- Setup 只选择对应 OS 架构的完整 EXE 文件名，不以进程架构、资源后缀或“只有一个资源”猜测安装包。
- 官方 Release 页面和安装包链接必须与固定仓库、标签和文件名一致；危险输入降级到固定 Releases 地址。Shell 只使用不可变目标中的 URI。
- 镜像可以参与发现和选包，但不向浏览器传递镜像或元数据的原始下载 URL。来源判断同时检查请求和最终响应地址。
- Store 只打开内置商店入口；Sideload、Developer、Unknown 只提供官方页面人工升级引导。`Setup=4` 与 `OpenSetupDownload=3` 保留原操作身份数值，构建 `Msi` 标记、数据目录和安装身份不变。
- 删除主程序旧 `.7z` 下载、解压和脚本引导，保留共享下载源设置和其他工具/插件的下载机制。

## 自动化验证

| 检查 | 结果 |
| --- | --- |
| 更新策略测试（固定 v1.9.6 JSON） | 136 通过 |
| 更新与本地化定向检查 | 140 通过 |
| Plugin.Abstractions 核心套件 | 15 通过 |
| Plugin.Runtime 核心套件 | 146 通过 |
| 宿主核心套件（包含更新测试） | 943 通过 |
| Setup x64 编译 | 0 警告、0 错误 |
| Setup ARM64 编译 | 0 警告、0 错误 |
| 打包应用 x64 编译 | 0 警告、0 错误 |
| 独立 UI 夹具 x64 编译 | 0 警告、0 错误 |

核心套件合计 **1,104 通过、0 失败、0 跳过**。夹具来自官方 v1.9.6 API 的字段快照；测试期间无网络依赖。覆盖仿冒域名、协议、仓库/标签/文件名错配、用户信息、端口、查询/片段、编码分隔符、路径穿越、缺包、重复资源、未知架构、版本边界、缺失发布状态和来源重定向。共享解析器另有 KnotLink 元数据回归检查。

复现（从仓库根目录执行）：

```powershell
dotnet test FolderRewind.Tests/FolderRewind.Tests.csproj -c Release --filter 'FullyQualifiedName~AppUpdatePolicyTests|FullyQualifiedName~LocalizationQualityTests'
./.github/scripts/Invoke-SetupCoreTests.ps1 -ResultsDirectory artifacts/update-p0/core-tests-new
dotnet build FolderRewind/FolderRewind.csproj -c Release -p:Platform=x64 -p:FolderRewindDistributionChannel=Msi -p:CETCompat=false -p:GenerateAppxPackageOnBuild=false -warnaserror
dotnet build FolderRewind/FolderRewind.csproj -c Release -p:Platform=ARM64 -p:FolderRewindDistributionChannel=Msi -p:CETCompat=false -p:GenerateAppxPackageOnBuild=false -warnaserror
dotnet build FolderRewind/FolderRewind.csproj -c Release -p:Platform=x64 -p:FolderRewindDistributionChannel=Packaged -p:CETCompat=false -p:GenerateAppxPackageOnBuild=false -p:AppxPackageSigningEnabled=false -warnaserror
```

本轮报告位于 `artifacts/update-p0/targeted/` 和 `artifacts/update-p0/core-tests/`。三种应用构建均使用仓库现有 WinUI analyzer，无新增警告抑制。

## UI 夹具与视觉复核

通过 `winapp ui` 在本地独立窗口执行 **12 个场景，全部通过**：zh-CN / en-US 各自覆盖 Setup、人工迁移、Store、危险链接、缺包、未知架构。

每个场景验证来源提示、渠道说明、按钮文案、“稍后”关闭与再次查看，并捕获主按钮的目标 URI。全部 12 张截图已人工逐张检查：本机 125% 缩放、默认深色主题下，文字和按钮没有裁切、重叠或意外省略。

```powershell
dotnet build tools/AppUpdateUiProbe/AppUpdateUiProbe.csproj -c Release -p:Platform=x64 -warnaserror
./scripts/tests/Test-AppUpdateUi.ps1 -ResultsDirectory artifacts/update-p0/ui-new
```

工具只链接生产校验策略、元数据解析器、文案投影和两种语言的资源，使用固定旧版本及注入元数据。它沿用现有 InfoBar、TextBlock/ScrollViewer、ContentDialog 的结构，**捕获目标而不调用浏览器或安装器，不加载或保存真实应用配置**。未给生产程序增加调试更新源或版本绕过开关。

报告和截图：`artifacts/update-p0/ui/test-results.json`、`*-tree.json`、`*.png`。脚本初次生成的 `visualReview=pending` 必须在实际查看全部截图后更新；UIA 断言通过不代表视觉复核通过。此机未检测到可用 Windows Sandbox，故使用数据隔离的本地测试窗口；测试窗口已关闭。

## 人工验收边界与清单

- [x] 固定元数据注入后的中英文提示、按钮和渠道分流。
- [x] 恶意 URL 不出现在操作目标中，降级入口为固定官方 Releases。
- [x] Setup 正常退出说明、其他安装形态的人工迁移提示、Store 发行延迟提示。
- [x] 独立窗口目标捕获与 12 张截图的视觉复核。
- [ ] 实际主程序中的浏览器/商店调用 smoke：发现新版后点主按钮，核对地址及启动失败时的日志；本轮以夹具目标捕获验证，不启动实际下载。
- [ ] 实际安装升级、1.8.2 数据接管及 ARM64 实机行为；这些不由本轮编译或夹具结果推断为已通过。

没有修改真实安装版本、用户配置、插件或备份数据，没有运行安装器。下一次发行说明使用 `update-p0-next-release-notes.md` 的中英文片段。镜像仍可虚报版本/说明；本轮不宣称独立元数据验真或客户端安装包哈希/签名校验。

# P2 开发验收记录（2026-10-07）

## 交付结论

P2 Full 语义表示与 Native History 闭环完成开发验收。正常用户入口继续关闭，不进入 P3。
公共 Abstractions 保持 3.9；未更新发行版本、内置包或安装态插件；未推送、发布。
Host 基线 a320b2b，MineRewind 基线 e7d780e，分支均为 `codex/native-artifact-full`。
用户原有 AGENTS.md 修改保持未提交。

实现提交：Host `a72be0e`（封存/闭包）、`6135e1d`（双表示/恢复）、`5a43017`（Retention/Cloud）、
`17087cf`（隔离验收）、`f357ded`（primary 解压预算/配置隔离）；MineRewind `f38f285`（Full v1/capability/格式测试）。本记录以各仓库当前 HEAD 为准。

候选包：`artifacts/native-artifact-p2/MineRewind-full-final.frplugin`，版本仍为开发基线 1.9.8。

```text
SHA256 a189e6dd06be568b300f00a1f8869df615835635247f5c4b1044492b271ebd31
```

包经真实 `PluginPackageInstaller`、`PluginAssemblyLoader`、Runtime activation/capability lease 进入生产 `BackupService`。
世界、玩家数据、配置、原始运行目录及候选二进制均未纳入 Git。

## Gate

| Gate | 结果与证据 |
|---|---|
| 真实插件独立恢复 | 通过。生产 Full 捕获生成一个 Version/两份表示；验收恢复期间移走 Core，明确使用 Plugin handler，成功后恢复 Core 对照。 |
| 同次提交/重启 | 通过。双表示联合提交测试；Prepared、PayloadsSealed、ArtifactGraphCommitted、HistoryPackCommitted、LocalStateApplied、Complete 各前后强制结束进程，共 12 个中断点。提交前恢复为 0 Version，提交后为 1 Version/2 Representation/1 BranchUpdate/2 local replicas，并重新物化插件表示。 |
| History 消费者 | 通过。真实事务 Restore、Checkout；演示流程调用生产 SafetySnapshot 捕获与通用三方 Merge Apply，输入来自 Plugin，输出为 CoreFull，独立改动均保留。 |
| Retention/生命周期 | 通过。保留版本的配对保护；真实清理释放 base root，保留 Branch/SafetySnapshot roots；两节点和共享依赖 DAG、read/GC 交错、共享 Destination 配置隔离；插件停用后 Core 默认可用，明确 Plugin 选择失败；更新 guard 在 drain 后校验。 |
| 加密/Cloud | 通过。真实 7z header 加密；正确/缺失/错误密码及密文损坏；真实 rclone 本地 remote 上传后删除隔离环境本地 payload，重启下载恢复。上传中断没有宣布 Replica，重试保留 ID；损坏下载不改变 catalog；缺依赖/损坏导入被拒绝；导入再导出保持传输字节稳定。 |
| 字节保真/默认关闭 | 通过。四个世界及独立 `.mcc` 语料逐文件路径、长度、SHA-256 与 Core 捕获视图一致；零字节 region 原样重建且保留语义诊断。默认策略阻断及 ConfigId scope 隔离测试通过。 |

主脚本得到 **30 个成功结果**：16 个真实样本捕获/重启结果、传输故障及完整工作流各 1 个、崩溃恢复 12 个。
`.mcc` 专项脚本另有 **6 个成功结果**，包含明文/加密、重启、传输故障和完整工作流。
容量收尾后另有 bounded-primary smoke **6 个成功结果**；共 42 个脱敏结果见同目录 `Native_Artifact_Full_P2_Results.json`。

## 真实世界

输入是已授权的 P1 冻结副本。制作验收副本前后核对文件树摘要；这些检查不构成原子世界快照。
捕获边界沿用 Host 过滤：三个真实存档的 `session.lock` 被排除，故 P2 是 190 文件、192,857,728 字节；没有扩大边界读取它们。

| 样本 | 捕获文件 | 原始字节 | 零字节 MCA | 明文验证段秒 | 加密及上传验证段秒 | 云端重启验证段秒 | Core 密文大小 | Plugin 密文大小 | Host 峰值 MiB |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 地形演示 | 7 | 4,964,453 | 0 | 7.12 | 11.28 | 5.33 | 213,856 | 348,640 | 139.84 |
| 26.1.2 Fabric | 127 | 161,899,956 | 25 | 101.56 | 133.61 | 75.51 | 115,472,433 | 118,359,698 | 185.84 |
| 1.21.8 NeoForge | 29 | 8,829,470 | 8 | 7.28 | 13.49 | 6.89 | 167,984 | 384,160 | 154.50 |
| 1.21.1 NeoForge | 27 | 17,163,849 | 2 | 17.89 | 24.44 | 17.73 | 10,266,609 | 10,615,825 | 142.43 |

计时来自入口主验证段，包含多次验证/物化，不能当成纯备份速度；尾部停用回退/Retention 检查另行执行。
工作集和 I/O 为 Host 进程指标，不包含 7z/rclone 子进程。主脚本最大磁盘采样值 1,787,591,661 字节，
以 1 秒间隔采样，包含源副本、备份、还原/Checkout/control 输出及本地 remote，不能当成生产存储增量。
每次运行的读写字节和磁盘采样峰值保存在脱敏 JSON 中。

独立语料由 `tools/New-NativeArtifactCorpus.ps1` 写入字面 region bytes，覆盖 gzip/zlib/raw 的内置/外置 payload、
未知 compression、非零空闲/padding/tail、孤立 `.mcc`、零字节 region 与非 region 文件；共 16 文件、131,369 字节。
3 个 opaque region 仍为 Exact 文件恢复，语义覆盖告警保持存在。另有手写 Full v1 JSON fixture 检验长期 reader 与 P1 scratch 的区分。

## 自动化与构建

| 工程 | 通过 |
|---|---:|
| FolderRewind.Tests | 811 |
| FolderRewind.Plugin.Runtime.Tests | 153 |
| FolderRewind.Plugin.Abstractions.Tests | 15 |
| MineRewind.World.Tests | 59 |
| MineRewind.Tests（含预览） | 93 |
| 合计 | 1,131 |

Host 普通构建与显式开发验收构建均成功，0 warning / 0 error。项目按次序构建；插件只引用独立 Abstractions 包与 World 项目。
插件复用了 `artifacts/semantic-p1` 下隔离的 3.9 feed/cache，没有替换机器既有的 Abstractions NuGet 缓存。

实际验证命令（仓库根目录）：

```powershell
$env:FOLDERREWIND_TEST_7Z = (Resolve-Path ./7za.exe).Path
dotnet test FolderRewind.Tests/FolderRewind.Tests.csproj -c Release --no-restore
dotnet test FolderRewind.Plugin.Runtime.Tests/FolderRewind.Plugin.Runtime.Tests.csproj -c Release --no-restore
dotnet test FolderRewind.Plugin.Abstractions.Tests/FolderRewind.Plugin.Abstractions.Tests.csproj -c Release --no-restore
dotnet test FolderRewind-Plugin-Minecraft/MineRewind.World.Tests/MineRewind.World.Tests.csproj -c Release --no-restore
dotnet test FolderRewind-Plugin-Minecraft/MineRewind.Tests/MineRewind.Tests.csproj -c Release --no-restore

dotnet build FolderRewind/FolderRewind.csproj -c Debug -p:Platform=x64 -p:GenerateAppxPackageOnBuild=false
dotnet build FolderRewind/FolderRewind.csproj -c Debug -p:Platform=x64 -p:FolderRewindDistributionChannel=Msi -p:NativeArtifactAcceptance=true -p:GenerateAppxPackageOnBuild=false

./tools/Test-NativeArtifactFull.ps1 `
  -AppPath ./FolderRewind/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/FolderRewind.exe `
  -PackagePath ./artifacts/native-artifact-p2/MineRewind-full-final.frplugin `
  -SevenZipPath ./7za.exe -RclonePath D:/Programs/Software-Test/rclone/rclone.exe `
  -SamplesJson ./artifacts/native-artifact-p2/frozen-samples.json `
  -OutputRoot ./artifacts/native-artifact-p2/final-run-02 -CrashMatrix
```

重跑必须换新的 OutputRoot。样本 JSON 是私有本机输入，不随仓库提供；字段为 Path、SourceId、Label。
验收入口仅在显式编译开关启用时存在，候选命令不会走普通应用实例或安装路径。

## 剩余限制

- CoreFull 与 PluginArtifact 双份保留，不提供存储节省、delta、Capture API、语义 Merge 或地图交互。
- Full v1 只保证捕获边界的文件字节；opaque 不等于可语义分析，不保证 Minecraft 世界逻辑健康。
- 使用单个不可变 closure bundle 实现依赖先行/整体验证；不按每个 DAG 节点单独创建 Cloud Replica。
- 提交前进程崩溃可能留下未登记的 Core archive。恢复保留 orphan 审计记录，不猜测路径自动删除；它们不成为可见成功 Version。
- deep probe/物化有重复解封和临时 I/O 成本；保留现有配额，超限依失败策略处理。磁盘耗尽通过异常/限额测试覆盖，没有填满用户磁盘。
- 默认用户路径仍关闭；后续 P3 需单独实施和验收。公共 Merge/Capture 协议尚未冻结。

后继方案：[P3 实施计划](Native_Artifact_Incremental_P3.md) 与 [P3 独立验收记录](Native_Artifact_Incremental_P3_Acceptance.md)。上述 P2 历史验收及范围保持原义，不代表 P3 Gate 已通过。

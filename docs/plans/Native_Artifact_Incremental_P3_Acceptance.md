# P3 开发验收记录（未通过发布准入）

日期：2026-10-07。Host 起点 `4ee0efd`，MineRewind 起点 `b0812d3`；两仓库开发分支 `codex/native-artifact-incremental`。

**结论：已实现并验证语义增量的核心开发闭环；整份 P3 计划尚未完成，正常用户入口保持阻断。** 本记录不把 P2 结果重复计为 P3 验收，也不把小样本 smoke 当作性能 Gate。

## 已执行的验证

| 验证 | 结果 | 范围 |
|---|---|---|
| Abstractions tests | 15 / 15 | API snapshot 兼容加法、assembly identity、BCL-only |
| Runtime tests | 156 / 156 | DAG/staging、lazy dependency、已知损坏阻断、Cloud v2 缺对象/取消/损坏与恢复 |
| Host tests | 814 / 814 | 既有 History/事务/Restore/GC 回归；新增 candidate 字节验证、非法继承、遗漏/多余路径 |
| MineRewind.World tests | 69 / 69 | 含 10 项 incremental 测试及原 P1 回归 |
| MineRewind tests | 93 / 93 | 插件适配和既有发现/快照/预览回归 |
| WinUI Debug x64 普通构建 | 成功，0 warning / 0 error | 含现有 WinUIAnalyzer；未修改 XAML |
| 显式 MSI acceptance 构建 | 成功，0 warning / 0 error | 仅开发编译开关包含验收命令 |
| 真实插件 8 版本 | 通过 | 单语义表示、逐版本独立物化、纯删除、周期 checkpoint、NoChanges |
| 真实插件 30 版本 | 通过功能 smoke | 每次物化逐文件树摘要一致；包含周期 checkpoint；不是 Retention/性能完整周期 Gate |
| 加密 + rclone local remote | 通过 | 3 版本、v2 对象上传、descriptor 下载和整个闭包导入 |
| 14 个进程中断点 | 全部通过 | 6 个 P2 捕获阶段 × before/after，加 PrimaryCleanup before/after；每次重启断言 History 和恢复字节 |
| 真实生命周期 | 通过 | Safety Snapshot、分支、Checkout 后继续捕获、通用三方 Merge；输出 CoreFull |

测试总计 1,147 项。它们不是大世界性能样本次数。

本机原始记录（不纳入 Git）：`artifacts/incremental-p3/smoke-02`、`sequence-30`、`encrypted-01`、`crash-matrix-02/results.json`、`lifecycle-01`。测试使用 P1 冻结输入的隔离副本，没有提交世界或应用私有数据。

8 版本 smoke 的第 6 次变化触发 checkpoint：新增物理存储约 366,763 bytes，其余辅助文件增删样本约 2–3 KiB。该样本是辅助文件变化，**不是“大世界中 3 个 chunk 变化”性能证据**。30 版本 Host 峰值工作集约 153.6 MB；未计入 7z/rclone 子进程，因此不能据此判定资源 Gate 通过。

已测试的插件包 SHA-256 随具体 request 固定；`lifecycle-01` 使用 `2F6B5DA17D6A1017D8B6EBFD39617DAEE6738524B17D23980F66B772313D3E3D`。后续构建必须记录自己的 package hash，不能复用旧验收结论冒充同一产物。

## 尚未完成的准入工作

1. **性能 Gate 尚未测齐**：20 次暖缓存、10 次可证明冷缓存、独立 ≥4 GiB 世界、10 倍规模工作集、同条件 Core Smart/Full 对照均未完成。现有结果全部 `ColdCacheControlled=false`、`PerformanceGatePassed=false`。没有将第一次运行冒称冷缓存。
2. **完整计量尚未落地**：分阶段 I/O、子进程、临时空间、真实上传量、Cloud 活跃/共享/退役未回收分类未形成完整报告。30 版本 smoke 不包含 Retention 后的完整物理成本对照。
3. **物理成本策略仍有缺口**：现已实现 70% 新原始字节、自包含 checkpoint，以及完整 pack DAG 的逻辑保留放大 >2× 触发。逻辑字节不是压缩存储成本；与等价 Core 压缩产物比较的 1.25× 存储上限及其单次成本回退还未实现。
4. **安全矩阵未全覆盖**：首个提交的 14 个崩溃点通过，但统计 witness 发布、Cloud descriptor 发布、已有 delta 基线下的全部故障点、插件禁用/缺失、格式迁移、缓存和实现态 witness 同时丢失、全部 mcc/重压缩 fixture 组合仍需补齐。
5. **Cloud UI/生命周期**：v2 对象协议已接线，共享 blob 不自动删除；未提供退役未回收空间的完整用户呈现，也未完成真实多设备副本退役验收。v2 下载目前借用 v1 临时 bundle 导入，有额外恢复 I/O。
6. **正常显式启用入口未开放**：保留 `NativeHistoryArtifactTransformPolicy` 的配置级开发验收门。不能把手工改 JSON 当作已验收产品入口；既有配置不迁移。

这些是明确未完成项，不是“以后优化”的已知可接受代价。下一轮按批准的阈值完成计量和安全矩阵；若 Smart transport 往返仍明显退化，使用同一变化计划的 Host 受控 changes-only 视图。不得退回日常 Full 扫描，也不得跳过必要验证来通过 Gate。只有所有准入项通过后才能加入并开放本地化用户选项。

## 重现开发构建与故障矩阵

从 Host 根目录执行：

```powershell
./scripts/Build-NativeArtifactP3.ps1
dotnet build FolderRewind/FolderRewind.csproj -c Debug -p:Platform=x64 -p:FolderRewindDistributionChannel=Msi -p:NativeArtifactAcceptance=true -p:GenerateAppxPackageOnBuild=false
./tools/Invoke-NativeArtifactP3CrashMatrix.ps1 -Executable <explicit-acceptance-exe> -TemplateRequest <private-request.json> -OutputDirectory <new-output-directory>
```

构建脚本使用按合同源码 hash 命名的 prerelease、本地隔离 feed/cache，独立构建插件并核验包内没有 Abstractions DLL。`build.json` 记录精确版本和包 SHA。没有向 NuGet 或插件商店发布。

request 字段沿用 P2 的 Workspace/Phase/Package/PackageSha256/SevenZip/Source/SourceId/Encrypted，可选 Rclone；P3 设置 `Incremental=true`，`Iterations` 控制辅助文件序列。`Lifecycle=true` 要求从一个 Version 开始，执行真实分支/恢复/Merge 工作流。所有 Workspace 必须是新的、与 Source 不重叠的隔离目录。退出成功不能替代本记录列出的未完成 Gate。

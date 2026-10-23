# 还原性能对比与服务验收

本工具对应 `docs/plans/Restore_Performance_Optimization_2026-10-23.md`。所有还原目标必须是隔离目录。原始输出、归档、TRX 和失败样本保留在 `artifacts/performance`，该目录不纳入 Git；本 README、测量源码和 runner 应纳入版本控制。

## 冻结双方产物

基线为 `4c857a13f90b53c6d23bf96140d06d5141489304`。用 `git archive` 导出到独立源码目录；不要切换或重置当前工作区。将以下测量适配复制到基线，不复制生产优化：

- `FolderRewind.Tests/RestorePerformanceOptimizationBenchmarkTests.cs`
- `tools/performance/RestoreServiceAnalysisProbe.cs`
- `tools/BackupPerformanceHost/Program.cs`、`BackupPerformanceHost.csproj`
- 根目录的同一份 `7za.exe`（本地忽略文件，不随 archive 导出）。

分别在两份源码根构建，并将**完整输出目录**复制到不同的冻结目录；不可只复制 DLL。两份源码使用各自的 `bin/obj`，构建串行进行。历史导出的 Host 使用 `FolderRewindBenchmarkOnly=true`，避免引用未包含在基线中的本地验收辅助文件。

```powershell
dotnet build ./FolderRewind.Tests/FolderRewind.Tests.csproj -c Release
dotnet build ./tools/BackupPerformanceHost/BackupPerformanceHost.csproj -c Release `
  -p:Platform=x64 -p:FolderRewindDistributionChannel=Msi -p:FolderRewindBenchmarkOnly=true `
  -p:GenerateAppxPackageOnBuild=false -p:BuildInParallel=false -m:1
```

测试输出为 `FolderRewind.Tests/bin/Release/net10.0`；Host 输出为 `tools/BackupPerformanceHost/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64`。保存提交、源码 zip、全部程序集、7-Zip 和插件 SHA256。当前机器结果的完整位置见报告。

## Core 固定语料

```powershell
./tools/performance/Run-RestorePerformance.ps1 `
  -BaselineDll <基线冻结目录/FolderRewind.Tests.dll> `
  -CandidateDll <候选冻结目录/FolderRewind.Tests.dll> `
  -SevenZip ./7za.exe -DatasetRoot <共享语料目录> -OutputRoot <新的结果目录>
python ./tools/performance/Summarize-RestorePerformance.py <结果目录> --output <汇总.json>
```

默认运行 1k、5k、10k、20k owner 的扁平/嵌套矩阵，以及 20k 六层 Smart Clean（扁平、嵌套）、5k Overwrite、1k Full、5k Smart 嵌套、10k Full 嵌套。归档在计时外预生成；双方读取相同载荷并核对摘要。owner 检查最终所有者，完整还原检查文件内容、时间、属性与目录集合。正式门禁使用每轮五次，禁止降低 `Repeats` 后宣称正式通过。

## 受保护真实语料

仅对已获授权的来源执行下列步骤。已有快照不能覆盖；不能获得稳定读取保护时记录未执行。

```powershell
$env:FOLDERREWIND_BENCHMARK_SOURCE = '<已授权原始世界目录>'
$env:FOLDERREWIND_BENCHMARK_SNAPSHOT = '<不存在的隔离快照目录>'
dotnet test ./FolderRewind.Tests/FolderRewind.Tests.csproj -c Release --no-build `
  --filter 'FullyQualifiedName~CreateReadProtectedWorldSnapshot'
```

该测试使用现有读取保护，核对来源与副本摘要，并写出相邻的 `.metadata.json`。排除 `session.lock`、voxy 和 DistantHorizons 临时数据库，详见测试中的规则。正式 Native 语料为现代世界快照；较大 Core 补充语料为旧世界快照。

```powershell
./tools/performance/Run-RestoreServiceComparison.ps1 `
  -BaselineExe <基线冻结目录/BackupPerformanceHost.exe> `
  -CandidateExe <候选冻结目录/BackupPerformanceHost.exe> `
  -SevenZip ./7za.exe -Plugin ./FolderRewind/Assets/Plugins/MineRewind-1.9.13.frplugin `
  -Snapshot <现代世界快照目录> -OutputRoot <新的Native结果目录> -Scenario native
# 大体量 Core 使用 -Scenario core-world 和旧世界快照，另选全新 OutputRoot。
python ./tools/performance/Summarize-RestorePerformance.py <结果目录> --output <汇总.json>
```

Host 初始化调度队列但不创建窗口。Native 通过随附 `.frplugin` 捕获六层链；Core 捕获 Full。测试准备采用 zstd 1、单线程，不修改生产默认值。测试保存 ownership 标记与载荷 SHA256，拒绝对不属于本次测试的配置目录执行还原。普通测速调用生产 `NativeHistoryApplicationService.RestoreVersionAsync`，包含编排、事务和 baseline 同步，并等待延迟清理。每次增加一个应被 Clean 删除的文件，计时后核对还原摘要及备份载荷未改变。

## 独立 Native 资源验收

以下测试不要与正式测速同时运行。选用另一个由 `native-prepare` 创建的隔离根；多 Source 验收会新增第二个 Source，因此不能再用于单 Source 测速。

```powershell
$env:FOLDERREWIND_TEST_7Z = (Resolve-Path './7za.exe').Path
$env:FOLDERREWIND_TEST_PLUGIN = (Resolve-Path './FolderRewind/Assets/Plugins/MineRewind-1.9.13.frplugin').Path
$env:FOLDERREWIND_RESTORE_SNAPSHOT = '<受保护现代世界快照>'
$env:FOLDERREWIND_RESTORE_SERVICE_PROBE = 'native-prepare'
& <候选Host.exe> <新的隔离根> 8 Balanced
$env:FOLDERREWIND_RESTORE_SERVICE_PROBE = 'native-restore'
$env:FOLDERREWIND_RESTORE_SERVICE_RESULT = '<新的结果.json>'
$env:FOLDERREWIND_RESTORE_MULTISOURCE = '1'
& <候选Host.exe> <刚创建的隔离根> 8 Balanced
$env:FOLDERREWIND_RESTORE_MULTISOURCE = $null
```

此入口通过生产批量还原服务连续还原同 store 的两个 Native Source，两次均要求每 Source 一次完整 closure 打开、两个目标摘要正确、清理完成且无租约死锁。

另一个独立的单 Source 根可设置 `FOLDERREWIND_RESTORE_SAFETY=1`、`FOLDERREWIND_BENCHMARK_REPEATS=1`，再运行 `native-restore`；要求实际保护快照阶段执行、新快照产生、旧载荷保持不变、目标摘要正确。结束后清空开关，禁止用该根继续正式无快照测速。

## 统计口径

每组串行运行两轮五次，第二轮交换基线/候选顺序。测速期间不并行构建或运行其他测试。`fileReadyMs` 是普通 API 返回时间；`totalMs` 包含延迟清理和日志排空；`allocatedBytes` 是期间进程累计托管分配，非存活堆或峰值。

diagnostic 的 `InclusivePhases` 及 Activity 阶段可能嵌套、并行，不能相加。例如 `prepare-source` 包含 closure、解包、logical/hash 与物化；解包子任务累计时长可能大于整体 wall time。缺失计数保持缺失；inventory 不重复哈希的结论需同时验证 listener 启用、logical 活动存在以及对应 Runtime 测试通过。

缓存未控制，保留首个样本、中位数和最小/最大值，不声称冷启动、可靠 P95 或物理磁盘 I/O。正式门禁：预指定主要场景两轮中位 API 耗时均下降至少 20%；10k owner 分配下降至少 95%；其他已测场景不能在两轮同时退化超过 10% 且 20 ms。所有正式组必须各有五个正确样本。汇总脚本输出持续回归列表，结果需结合内容/恢复测试共同验收。

可使用 `python ./tools/performance/Plot-RestorePerformance.py --core <Core汇总.json> --native <Native汇总.json> --world <大世界Core汇总.json> --output <图表.png>` 导出两个预先指定主要场景的图表及同名完整汇总 JSON；需要 matplotlib。

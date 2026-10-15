# 1.9.x 备份与还原服务基准

在仓库根目录串行执行。所有目标和应用状态使用隔离目录；不要将用户唯一世界作为还原目标。基准目录、归档、TRX 均保存在忽略的 Reference/TestResults，汇总脚本只读诊断 JSON，不读取用户配置或凭据。

```powershell
$env:FOLDERREWIND_TEST_7Z = (Resolve-Path './7za.exe').Path
$env:FOLDERREWIND_TEST_RCLONE = 'D:/Programs/Software-Test/rclone/rclone.exe'
dotnet build ./tools/RestoreProtocolProbe/RestoreProtocolProbe.csproj -c Release
$env:FOLDERREWIND_RESTORE_PROTOCOL_PROBE = (Resolve-Path './tools/RestoreProtocolProbe/bin/Release/net10.0/RestoreProtocolProbe.dll').Path
dotnet test ./FolderRewind.Tests/FolderRewind.Tests.csproj -c Release --filter 'TestCategory!=PerformanceBenchmark'
```

候选服务基准（Balanced/Strict 分别运行，输出目录不得混用）：

```powershell
$env:FOLDERREWIND_BENCHMARK_CANDIDATE = '1'
$env:FOLDERREWIND_BENCHMARK_MIB = '2048'
$env:FOLDERREWIND_BENCHMARK_POLICY = 'Balanced' # 下一组改为 Strict
$env:FOLDERREWIND_BENCHMARK_OUTPUT = "$PWD/Reference/performance/candidate-balanced-complete"
$env:FOLDERREWIND_BENCHMARK_SNAPSHOT = "$PWD/Reference/performance/datasets/real-world-a"
dotnet test ./FolderRewind.Tests/FolderRewind.Tests.csproj -c Release --filter 'FullyQualifiedName~MeasureFullRestoreComponents|FullyQualifiedName~MeasureSmartFilteredAndQuickComponents|FullyQualifiedName~MeasureQuickSnapshotAndReuseFlow|FullyQualifiedName~MeasureRealWorldFilteredRestore'
```

`real-world-a` 是本次已授权、受读取保护并经内容摘要核对的未扩大世界副本，不在 Git 中。换机器需另选已授权源，通过 `CreateReadProtectedWorldSnapshot` 创建新副本：显式设置 `FOLDERREWIND_BENCHMARK_SOURCE`、不存在的 `FOLDERREWIND_BENCHMARK_SNAPSHOT`，运行该单个测试。缺少快照时不得将真实世界场景填作通过。

实际 Host 工具初始化 WinUI 资源/调度队列并调用生产服务，不创建窗口，不作为 UI 验收。构建全图必须统一 MSI 属性，避免同一依赖出现两套属性同时写入 bin/obj：

```powershell
dotnet build ./tools/BackupPerformanceHost/BackupPerformanceHost.csproj -c Release -p:Platform=x64 -p:FolderRewindDistributionChannel=Msi -p:GenerateAppxPackageOnBuild=false -p:BuildInParallel=false -m:1
$hostExe = './tools/BackupPerformanceHost/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/BackupPerformanceHost.exe'
& $hostExe "$PWD/Reference/performance/host-candidate-native-64" 64 Balanced quick
& $hostExe "$PWD/Reference/performance/host-candidate-native-large" 2048 Balanced
& $hostExe "$PWD/Reference/performance/host-candidate-strict-64" 64 Strict quick
& $hostExe "$PWD/Reference/performance/host-candidate-strict-large" 2048 Strict
```

工具拒绝已存在的状态目录，先验证 ConfigService 路径在隔离根内才初始化状态。每次 Full 三个独立项目；Smart 三条独立六层链；Quick 每个开关/变化组合三次。普通恢复点和 Quick 开关在旧代码没有独立对应项时记录 `quickPolicySupported=false`，不伪造关闭效果。

历史对照源码保存在 `Reference/performance/v1.9.6` 和 `baseline-head`，分别由精确 Git commit 导出。仅加入诊断计数与隔离测试入口，归档、验证、还原、Snapshot 行为保持原版。Host 构建时指定 `-p:FolderRewindHostProject=<独立源码的 FolderRewind.csproj 绝对路径>`；执行前核对产物，勿覆盖正在运行的 Host 或并行构建。完整原始结果路径、源码版本、7-Zip/插件 hash 在验收报告中。

完成固定输入的全部组后：

```powershell
./tools/performance/Summarize-BackupRestore.ps1
```

汇总保留三次样本、首轮、中位数和最小/最大值，以及阶段计数、逻辑输出/日志字节、卷与策略观察。子进程/Host 内存为被观测的进程生命周期峰值；没有重置进程高水位。所有缓存均未控制，不能声称冷启动或 P95。inclusive 阶段可能嵌套，禁止加总成耗时。staging/rollback 的逻辑载荷峰值由固定输入和计划估算，在报告单独注明；未测 NTFS 实际占用/内核 I/O。

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

工具拒绝已存在的状态目录，先验证 ConfigService 路径在隔离根内才初始化状态。当前默认 Full 五个独立项目、Smart 五条独立六层链、Quick 每个开关/变化组合五次；可用 `FOLDERREWIND_BENCHMARK_REPEATS` 显式设置次数。历史 1.9.7 报告使用三次，其原始数据不变。普通恢复点和 Quick 开关在旧代码没有独立对应项时记录 `quickPolicySupported=false`，不伪造关闭效果。

历史对照源码保存在 `Reference/performance/v1.9.6` 和 `baseline-head`，分别由精确 Git commit 导出。仅加入诊断计数与隔离测试入口，归档、验证、还原、Snapshot 行为保持原版。Host 构建时指定 `-p:FolderRewindHostProject=<独立源码的 FolderRewind.csproj 绝对路径>`；执行前核对产物，勿覆盖正在运行的 Host 或并行构建。完整原始结果路径、源码版本、7-Zip/插件 hash 在验收报告中。

完成固定输入的全部组后：

```powershell
./tools/performance/Summarize-BackupRestore.ps1
```

汇总保留三次样本、首轮、中位数和最小/最大值，以及阶段计数、逻辑输出/日志字节、卷与策略观察。子进程/Host 内存为被观测的进程生命周期峰值；没有重置进程高水位。所有缓存均未控制，不能声称冷启动或 P95。inclusive 阶段可能嵌套，禁止加总成耗时。staging/rollback 的逻辑载荷峰值由固定输入和计划估算，在报告单独注明；未测 NTFS 实际占用/内核 I/O。

## 1.9.8 第二阶段

为了取得有效的旧版本对照，双方在计时外都等待前次操作完成的 UTC 秒结束，再开始下一项操作，避开基线的秒级归档撞名窗口；该等待不属于文件可用或总成本。无等待的同秒创建缺陷由功能验收单独覆盖。原始 JSON 记录此采样间隔，不能把它当成生产性能优化。

先完成 `stage2` 无窗口生产服务接线验收；它调用实际 BackupService、普通还原、Checkout 和多 Source 还原，验证快照内容，不能用夹具回调替代。参数示例：

```powershell
& $hostExe "$PWD/artifacts/1.9.8/stage-2/host-acceptance" 8 Balanced stage2
```

对照源码固定为 `3bfc761`，只加入同一份进程 I/O 诊断适配。先冻结候选工具全部输出，再使用同一工具源码和 `FolderRewindHostProject` 构建基线，避免覆盖候选。两个工具的输入都放在同一 D 卷；按轮次交替执行，两种策略均为五次，不与构建、测试并行。原始结果目录命名为 `baseline-balanced-r1`、`candidate-balanced-r1`、`baseline-strict-r1`、`candidate-strict-r1` 等。

```powershell
$env:FOLDERREWIND_BENCHMARK_REPEATS = '5'
& $frozenBaselineExe "$PWD/artifacts/1.9.8/stage-2/performance/baseline-balanced-r1" 64 Balanced quick
& $frozenCandidateExe "$PWD/artifacts/1.9.8/stage-2/performance/candidate-balanced-r1" 64 Balanced quick
# Strict 及第二轮同样使用独立目录，按顺序执行。
./tools/performance/Summarize-StageTwo.ps1 -InputRoot ./artifacts/1.9.8/stage-2/performance -Output ./artifacts/1.9.8/stage-2/performance-summary.json
```

也可使用 `Run-StageTwoComparison.ps1 -BaselineExe <冻结的基线 EXE> -CandidateExe <冻结的候选 EXE> -SevenZipExe <7za.exe> -OutputRoot <尚不存在的目录> -MiB 64` 串行执行完整两轮。它第二轮交换先后顺序，保存每组退出码及时间；退出码 2 的内容错误保留原始数据并使最终验收失败。

结果分别保存 API 文件可用时间、等待清理及日志排空后的总时间、清理/日志排空耗时、日志字节、物化/补丁/归档调用及读写计数。Host OS I/O 为进程级 inclusive 逻辑字节，不能与其嵌套 scope 重复相加，也不能当成物理磁盘流量。载荷 SHA 检查在计时之外，按表示身份记住首次摘要，避免基线缺少存储摘要时漏掉归档撞名。工具退出码 2 表示矩阵完成但有内容/结果错误；错误组不能作为有效性能对照。连续两轮中位数退化同时超过 10% 和 20 ms 需处理。

## 1.9.8 第三阶段：复用现有场景比较正式基线

正式对照为 `v1.9.7 / 91ed4ffd` 和原规划起点 `53975eb4`。使用同一份当前工具源码与各自 Host 项目，构建时加 `-p:FolderRewindBenchmarkOnly=true`，只排除引用阶段二专用 API 的既有 `StageTwoAcceptance.cs`；Full/Smart/Quick 采样逻辑不变。该构建拒绝 `stage2` 模式，接线验收使用正常构建。不同 Host 构建后立即冻结完整输出，不能保留同一工具目录供后续覆盖。

旧 Host 仅应用阶段二已有的 `baseline-diagnostics.patch`（23 行进程 I/O 适配），记录精确补丁/源码/工具摘要，不移植业务实现。缺少的指标在各组 counters 中缺省，不应在跨版本报告中填零。旧 Host 无后台清理 API 时，清理已包含在 API 耗时，`cleanupDrainMs=0` 只表示 API 后无额外排空。

`Run-StageTwoComparison.ps1` 增加可选 `-Rounds 1`（默认仍为 2）和 `-BackupOnly`，复用已有 2 GiB Full/Smart 场景，不运行大型 Quick 交叉组合。对应汇总也传 `-BackupOnly`。小型 64 MiB 使用原默认完整组合；大型先一轮，命中回归则同条件再测，原始轮次和根目录分别保留。所有组每次五个样本，判定不变；参数不会新增测试场景。

需要单独复测第二轮时，使用新的 OutputRoot 并传 `-FirstRound 2 -Rounds 2`，保留 r2 标记和交换后的执行顺序。把两份报告按相同场景/策略/指标组合检查连续回归，不修改原始 r1 JSON。

MSI 属性的无窗口工具是服务性能证据，不是 x64 MSIX 安装、启动或 UI 验收。第三阶段不新增 UI 脚本，交接规则见 `docs/plans/FolderRewind_1.9.8_Automated_Acceptance.md`。

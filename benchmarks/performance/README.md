# 性能运行入口

性能运行与汇总脚本位于本目录，独立执行程序位于 `tools/BackupPerformanceHost`。方法、数据安全边界及比较步骤见[开发指南](https://folderrewind.top/docs/architecture/development#performance)。

- `Run-CoreSmartBenchmark.ps1`：固定合成数据的 Full/Smart 与扫描分析。
- `Run-RestorePerformance.ps1`、`Run-RestoreServiceComparison.ps1`：隔离还原比较。
- `Run-StageTwoComparison.ps1`：服务阶段比较。
- `Run-UiPerformance.ps1`、`Summarize-UiPerformance.ps1`：隔离 UI 负载与调度延迟汇总。
- `Run-ManagerUiAcceptance.ps1`：独立的管理页 Reset、同 ID 模型替换、选择和滚动回归。
- `Run-UiProfiling.ps1`：60 秒同步热点诊断与自然排空检查，不替代十分钟基准。
- `Run-SpatialWorkspaceUiAcceptance.ps1`：实际空间页的受控扫描、筛选、取消和生命周期验收。
- `Invoke-UiPostRunGc.ps1`：计时结束后的可选弱引用和强制 GC 检查。
- `Summarize-*.ps1`、`Summarize-*.py`、`Plot-RestorePerformance.py`：结果校验与汇总。

```powershell
./benchmarks/performance/Run-CoreSmartBenchmark.ps1 -HostExe <冻结的基准程序绝对路径> -OutputRoot <尚不存在的隔离输出目录>
```

双方使用相同输入、7-Zip、测量代码和参数，保存完整输出与哈希并交替执行。输出、TRX、原始性能数据和失败样本放入 `artifacts/performance`；服务基准不替代 UI 或真实远端验收。不得用真实存档目录作为写入目标。

UI 验收使用自包含 x64 MSI 通道和 `NativeArtifactAcceptance=true` 编译的隔离程序。基线和候选须使用相同的十分钟夹具，前两分钟暖机；采集的是调度队列延迟而非帧率，任务状态与地图提供程序是合成的。下面的输出目录必须尚不存在：

```powershell
./benchmarks/performance/Run-UiPerformance.ps1 -Executable <程序绝对路径> -RunDirectory <artifacts 下的新目录>
```

完成后检查结构化结果和截图，并正常退出隔离进程。可选后测需额外以 `PerformancePostRunAcceptance=true` 编译，运行时向上面的脚本传 `-EnablePostGc`；十分钟结果完成后，另行执行 `Invoke-UiPostRunGc.ps1 -RunDirectory <同一目录>`。后测强制 GC，只统计夹具记录的去重弱引用，并不代表整个堆的存活对象。

管理页的独立验收需以 `FolderManagerUiAcceptance=true` 和 `NativeArtifactAcceptance=true` 编译，再执行 `Run-ManagerUiAcceptance.ps1 -Executable <程序绝对路径> -RunDirectory <artifacts 下的新目录>`。检查两张截图及结果后正常退出进程。此开关不用于普通十分钟基准。

同步热点诊断使用 `NativeArtifactAcceptance=true`、`PerformanceUiProfiling=true`，执行 `Run-UiProfiling.ps1 -Executable <程序绝对路径> -RunDirectory <新目录>`。采样先缓存在内存，60 秒后后台写出，夹具自然排空后正常退出。空间页验收使用 `NativeArtifactAcceptance=true`、`SpatialWorkspaceUiAcceptance=true`，执行 `Run-SpatialWorkspaceUiAcceptance.ps1` 并传入相同的可执行文件/新目录参数；它在实际页面中使用 Host 拥有的固定小树与 fake provider，覆盖 10 万条扫描、快速筛选、在途取消及关闭，非真实历史物化或备份还原端到端验证。`-LifetimeProbe` 可单独观察小规模页面生命周期，不替代完整扫描。两类开关均不用于普通十分钟比较，截图、原始失败及诊断结果保留在隔离输出中。

元数据回放入口：`dotnet run --project ./tools/HistoryMetadataPerformanceHost/HistoryMetadataPerformanceHost.csproj -c Release -- <隔离输出目录> 5000 <原基线的只读 repository 目录>`；省略第三参数沿用合成数据模式，回放前后 SHA-256 与逐字节一致性记录在新 GUID 运行目录的 `replay-integrity.json`。

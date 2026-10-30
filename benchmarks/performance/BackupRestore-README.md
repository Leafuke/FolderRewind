# 性能运行入口

性能运行与汇总脚本位于本目录，独立执行程序位于 `tools/BackupPerformanceHost`。方法、数据安全边界及比较步骤见[开发指南](https://folderrewind.top/docs/architecture/development#performance)。

- `Run-CoreSmartBenchmark.ps1`：固定合成数据的 Full/Smart 与扫描分析。
- `Run-RestorePerformance.ps1`、`Run-RestoreServiceComparison.ps1`：隔离还原比较。
- `Run-StageTwoComparison.ps1`：服务阶段比较。
- `Summarize-*.ps1`、`Summarize-*.py`、`Plot-RestorePerformance.py`：结果校验与汇总。

```powershell
./benchmarks/performance/Run-CoreSmartBenchmark.ps1 -HostExe <冻结的基准程序绝对路径> -OutputRoot <尚不存在的隔离输出目录>
```

双方使用相同输入、7-Zip、测量代码和参数，保存完整输出与哈希并交替执行。输出、TRX、原始性能数据和失败样本放入 `artifacts/performance`；服务基准不替代 UI 或真实远端验收。不得用真实存档目录作为写入目标。

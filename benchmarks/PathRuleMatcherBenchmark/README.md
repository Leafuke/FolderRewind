# 路径规则匹配基准

该基准模拟 MineRewind 在三个数据目录和多个维度中生成大量精确区域规则的场景。它使用 36,900 条规则匹配 10,000 个候选路径，比较旧式逐规则线性扫描与一次构建的 `PathRuleMatcher`。

运行：

```powershell
dotnet run --project benchmarks\PathRuleMatcherBenchmark\PathRuleMatcherBenchmark.csproj -c Release
```

2026-07-29 本机结果：

| 项目 | 结果 |
|---|---:|
| 规则数 | 36,900 |
| 候选路径数 | 10,000 |
| 每种实现迭代数 | 5 |
| 线性扫描中位数 | 686.50 ms |
| 预编译匹配器中位数 | 42.71 ms |
| 提升 | 16.07× |

环境：.NET SDK 10.0.302、Windows NT 10.0.26200.0、Intel64 Family 6 Model 204。结果只统计匹配阶段；构建匹配器在每次备份/还原操作中执行一次。

该项目不加入主解决方案，也不包含时序断言，避免把机器负载波动带入 CI。

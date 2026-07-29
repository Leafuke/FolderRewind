# 路径规则匹配基准

该基准模拟 MineRewind 在三个数据目录和多个维度中生成大量精确区域规则的场景。它使用 36,900 条规则匹配 10,000 个候选路径，分别记录 `PathRuleMatcher` 的构建耗时、匹配耗时和匹配阶段线程分配量。

运行：

```powershell
dotnet run --project benchmarks\PathRuleMatcherBenchmark\PathRuleMatcherBenchmark.csproj -c Release
```

本机基线结果记录在每轮相关改动的验证报告中。基准输出使用稳定的键值行，便于前后对比，但不在 CI 中设置时序断言。

2026-07-29 本轮验证基线（36,900 条规则、10,000 个候选路径、5 次迭代）：

```text
build_median_ms=3.48
match_median_ms=67.73
match_allocated_bytes_median=142965816
```

该项目不加入主解决方案，也不包含时序断言，避免把机器负载波动带入 CI。

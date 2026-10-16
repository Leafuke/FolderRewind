# P3 验收记录（实验性交付；原性能门未全部通过）

> 当前状态：用户已批准有限的**实验性交付**，提供显式启用与手动 checkpoint 入口；原严格性能 Gate 仍未通过。最新入口验证与人工验收步骤见文末。本轮未进行 UI 自动测试。

> 下方原始开发记录保留。当前结果见文末「2026-10-07 性能优化复核」；周期 checkpoint、测试规模等后续决策以该节及修订计划为准。

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

## 2026-10-07 性能优化复核

**结论：优化与本轮安全回归完成，P3 仍未通过整体准入。** 日常稀疏增量与全部恢复场景通过缩减性能阈值；密集变化最大耗时超限约 79ms，显式自包含 checkpoint 明显超限。没有放宽阈值、牺牲 Exact 或开放正常入口。

### 精确产物与测量边界

- 起点：Host `d7691a8`、MineRewind `64ef042`，在此前未提交调优上继续；未纳入用户的 `AGENTS.md` 修改。
- 本轮代码提交：Host `0370cd4`（Runtime/验证）、`07462b6`（变化视图/策略/测量）；MineRewind `19e11a7`（流式验证/布局/codec 复用）。
- 独立插件包 SHA-256：`6A614E3A63D4C83C70FA8F0676B3CE175DD6069B25B5BADFFB5806AE9A0B40AC`。
- 验收 Host DLL SHA-256：`B7196D963FB56AF472030F38B04CDC6AE5123CC2B546263FFD61DA43A6CF7919`。本地留存于 `artifacts/incremental-p3/optimization/FolderRewind.acceptance.dll`，与随后普通构建区分。
- 同一提供的真实世界，829 个源文件、约 563 MB；操作隔离副本，未改原世界。有效范围排除 `session.lock`。Core/P3 相同输入序列、稳定快照、默认压缩条件；每个对应版本恢复树摘要相等。
- 普通场景一次预热、三次测量；表中取最大值，checkpoint 一次。未控制 OS 冷缓存，不称为 p95。没有并行运行基准、构建或测试。
- 结果：`artifacts/incremental-p3/optimization/{core-final,semantic-final}/benchmark-results.json`；比较：`performance-gate.json`。这些私有语料/运行结果不提交 Git。
- `profile-01`、`profile-02`、`semantic-03` 是中间诊断，不挑选其中较快值替代最终结果。profile-01 的 `logical.hash` 标记误放在分配步骤，最终已修正，不能把该中间字段当作逻辑摘要计时。

### 缩减性能矩阵

| 场景 | Core 备份 ms | P3 备份 ms | P3 允许 ms | Core 恢复 ms | P3 恢复 ms | 结果 |
|---|---:|---:|---:|---:|---:|---|
| 无变化 | 1139 | 1141 | 1503 | 5176 | 5758 | 通过 |
| 稀疏 3 chunk | 1958 | 2111 | 2850 | 7208 | 5874 | 通过 |
| 同 region 密集变化 | 1725 | 3235 | 3156 | 9607 | 6026 | 备份耗时未过；恢复通过 |
| 纯删除 | 1415 | 1516 | 2768 | 10885 | 6040 | 耗时通过 |
| 显式 checkpoint | 13091 | 24624 | 17364 | 5585 | 5837 | 备份耗时未过；恢复通过 |

稀疏新增存储/密文上传：Core 12,385,529 B，P3 275,639 B，约减少 97.8%。密集变化分别为 12,576,232 / 10,821,122 B；checkpoint 为 424,639,372 / 434,876,804 B（P3 多约 2.4%）。相应存储阈值均通过。

纯删除仍有完整目录 root 的固定开销：Core 230 B，P3 98,384 B。此场景原本没有比例阈值，不能将「StoragePassed」解释成空间优于 Core。

上传数字是本地对象 transport 实际复制并校验的密文 payload，不含 descriptor、receipt、协议流量；真实 rclone local 闭包上传/下载另行验证。未伪称远端网络性能。

### 瓶颈证据与资源成本

最终 checkpoint 的墙钟阶段：旧闭包打开/验证约 3.74s、语义重打包约 2.66s、新 envelope 封存约 14.47s、候选完整字节验证约 1.16s。封存包含压缩和真实往返解包验证。并行 worker 的 inclusive 耗时存在重叠，不可相加冒充端到端耗时。

对象顺序读取不再先完整 hash 后倒回重复消费；流池改为 32 项 LRU 后，完整恢复从此前约 12–13s 降至约 5.8–6.0s。稀疏候选验证不再为一个 region 解包混装的整个 64 MiB data pack。新布局增加了 pack 数量，最终 checkpoint 闭包为 57 个新 Artifact，仍须计入节点和长期保留成本。

Host 峰值工作集 211,144,704 B；checkpoint 中同时运行的已观测 7z 子进程工作集，25ms 采样峰值 598,949,888 B。二者是分别测得的峰值，不声称同一时刻相加即精确整树峰值。原计划的插件缓冲限制不能被偷换成忽略子进程内存。子进程最多四个 envelope worker，不随世界大小增加。

checkpoint 的 Host 读/写约 4.82 GB / 1.27 GB（不含 7z，另列 stage counters），仍有显著 I/O 放大。无变化源快照仍读/写约 563 MB；不能宣称整条路径是 O(changes)。初始基线中 region payload 约 291.5 MB、Exact residual 约 182.2 MB；尚未隔离其压缩后独立成本，不能由原始体积推断放弃 Exact 的收益。

### 实际安全验证

| 验证 | 最新结果 | 证据/范围 |
|---|---|---|
| Host 单元/集成测试 | 816 通过 | 显式指定仓库 7za，完整 suite |
| Runtime 测试 | 159 通过 | 含并行上限、解码失败等待全部 worker、候选验证复用、受控路径 |
| Abstractions 测试 | 15 通过 | 兼容契约回归 |
| MineRewind domain | 74 通过 | 含 mcc/opaque/容器保真、显式 checkpoint、顺序流失败/取消/截断/部分消费 |
| MineRewind 插件 | 93 通过 | 独立 API feed 构建 |
| 首次提交故障 | 12/12 通过 | `optimization/crash-initial/results.json` |
| 已有基线提交故障 | 4/4 通过 | `optimization/crash-delta/results.json` |
| 分支/恢复/通用 Merge | 通过 | `optimization/lifecycle/p3-capture-result.json`；5 versions |
| 连续 30 版本 | 通过 | `optimization/thirty-versions/p3-capture-result.json`；每版 Exact 恢复和 NoChanges |
| 缓存丢失 / 插件缺失 | 通过 | 明确基线修复；缺插件不推进 History |
| 加密 + rclone local | 通过 | 3 个单语义表示，实际对象闭包上传/下载 |
| WinUI 构建 | 通过 | 验收构建及普通 x64 构建均 0 警告、0 错误；未修改 UI |

直接变化视图不产生临时 Core archive，因此 PrimaryCleanup 两切点不再存在于新正常路径；旧 transport 清理代码、既有 14/6 点历史证据保留。工具 `-Transport` 可针对旧 transport 构建继续验证，不能把不存在的切点声称成新路径注入成功。

30 版本测试是小语料生命周期 smoke，**不是**大世界完整周期/Retention 后物理成本 Gate。全部 mcc 组合由领域 fixtures 覆盖；提供的大世界本身没有 mcc。没有将单元测试包装成真实世界独立样本。

### 保留的限制与处理决定

- 普通入口继续关闭，既有配置不迁移。P3 的整体 `PerformanceGatePassed=false`。
- 语义路径不再按次数、70% 变化或局部放大自动全量重打包；Core Smart 策略未改变。首次/失效重建与显式自包含仍保留。不得用降低触发频率把必要 checkpoint 的失败隐藏掉。
- 自动 compaction 暂不启用。下一步应针对 envelope 封存/解封的剩余成本，以及 Host 可证明的全局回收收益开展独立工作；不能通过关闭摘要、路径、预算、candidate 字节验证或偷偷改变 fidelity 来达标。
- 没有实现新的随机访问加密封装、Normalized fidelity 或局部 compaction。若扩大到这些存储设计，需要相应的新格式及故障矩阵，不能在本轮验收中默认为已完成。
- 完整临时空间峰值、30 版本大世界 Retention 成本、Cloud 退役未回收空间 UI、所有新增 witness/descriptor 故障切点仍不是本轮已验证事实。原有发布准入缺口不会因性能改善而自动消失。

## 显式实验入口与手动 checkpoint 交付

用户在接受上述性能限制后授权实验性交付，并明确不运行 UI 自动测试。这里记录产品范围的批准，不把此前 `PerformanceGatePassed=false` 改成 true。

### 已实现

配置设置的「备份策略」新增实验卡片：默认关闭的启用开关、兼容性提示及刷新、失败回退开关、保存并创建 checkpoint、取消与结果状态。新文本同步中英文。兼容性来自插件 manifest 与活动 capability；不将 Java/region 类型引入 Host Core。

`ExperimentalEnabled` 默认 false，旧配置不自动启用，配置复制/保存保留用户选择。Rolling、选区/子集、无来源及不兼容插件阻止启用/保存；运行时独立复检。关闭后普通备份先建立 CoreFull，已有语义历史不删除。

手动 checkpoint 是一次有取消令牌的普通 Host 备份事务，仍经过配置操作门、固定基线、candidate 字节验证和原子 History 提交。无文件变化也创建新的自包含 Artifact 闭包。失败时不会提交 CoreFull 冒充 checkpoint；下一次普通备份不会继承强制标志。界面说明额外空间/耗时、恢复插件依赖及云端共享数据可能未回收。

checkpoint 写回备份时间后，仅重基设置草稿中的该项运行时事实；其他并发配置变更仍触发 stale-draft 保护，避免覆盖用户或其他操作的修改。

### 本轮实际验证

- 普通 WinUI x64 构建、专用 headless 验收构建成功，0 警告、0 错误；最终普通构建不包含验收入口。
- Host 测试 **819/819 通过**，包括默认不启用、明确 opt-in、非法策略拒绝、Rolling/partial 拒绝，以及单次 checkpoint 标志复制及不泄漏。
- 真实插件包仍为 `6A614E3A63D4C83C70FA8F0676B3CE175DD6069B25B5BADFFB5806AE9A0B40AC`，本轮没有修改 MineRewind 或公开 API。
- 非 UI 服务集成分别在未加密和加密配置通过，**没有调用 EnterAcceptance 绕过产品门**。验证默认阻断、显式启用、NoChanges、无变化手动 checkpoint、自包含闭包不引用旧 Artifact、Exact 恢复、草稿操作后仍可保存、RequireTransform 失败、真实转换失败后的 CoreFull warning 回退、checkpoint 禁止回退、关闭后 CoreFull、缺插件阻断、预取消及 Rolling 拒绝。
- 注入转换失败使用独立构造的合法容器/超解码预算压缩 payload；第一次 fixture 位于未识别维度而被正确作为 raw 保存，未满足注入目的。修正为合法 namespaced dimension 后完成失败/回退测试，没有修改 opaque 策略来迎合测试。
- 测试产物位于 `artifacts/incremental-p3/experimental-delivery-02/experimental-delivery-result.json` 与 `experimental-delivery-encrypted/experimental-delivery-result.json`；均 `UiAutomated=false`。专用 Host DLL 留存 `experimental-delivery-tested.dll`，SHA-256 `8E1E4DC32D98A8B4BA61AF005F397F772797E10134D1B22F038A0F623E95275E`。
- 对应日志：`experimental-full-tests.log`、`experimental-delivery-build.log`、`experimental-production-build.log`。28 条新中英文资源项已检查对应存在。
- **UI 的显示、交互与视觉效果未自动验证，留给以下人工验收。** 没有执行 winapp UI 脚本或宣称其通过。

### 人工验收步骤

先使用本轮 FolderRewind 构建，安装并启用本地 `artifacts/incremental-p3/build/MineRewind-incremental.frplugin`（API 3.10 兼容包）。建议选择世界副本及独立备份目标。若从工程启动，可手动运行：

```powershell
winapp run .\FolderRewind\FolderRewind.csproj --arch x64 --detach
```

1. **默认关闭与保存**：打开一个 Java 存档配置 → 配置设置 → 备份策略 →「语义备份（实验性）」。确认默认关闭。选择智能模式、完整来源，点击刷新；启用后保存，离开再打开，确认状态保留。先启用后放弃未保存修改，确认不改变实际配置。
2. **正常备份**：执行第一次备份，检查历史新增版本；再执行无变化备份，应不新增。修改世界副本中的少量内容后备份，确认产生新版本且可恢复。历史语义版本恢复需要保持兼容插件启用。
3. **无变化 checkpoint**：不改文件，点击「创建 checkpoint」。有未保存设置时应显示「保存并创建 checkpoint」。检查按钮防重复、运行指示及取消入口；完成后历史新增带「手动语义 checkpoint」备注的版本，恢复该版本验证内容。随后普通备份应重新按无变化跳过。
4. **草稿与取消**：checkpoint 完成后继续编辑并保存设置，不应仅因备份时间变化而提示草稿过期。创建另一个 checkpoint 并在运行中取消，查看最终状态与历史：取消前已 durable 提交的版本可以存在，不能根据点击取消就推断已提交版本被删除。取消不会撤销已保存设置。
5. **兼容性与禁用**：尝试滚动模式或区域/子集范围，确认提示不兼容，不能在该状态保存启用的实验策略。禁用插件并刷新，确认入口报告不可用、checkpoint 不可执行；重新启用插件再刷新恢复可用。不要删除插件包后期待语义历史能无插件恢复。
6. **关闭实验**：关闭并保存。下一次普通备份应建立 CoreFull，之后恢复普通 Smart 行为；已有语义版本仍在。不要自动删除旧包或原历史作为切换的一部分。
7. **失败设置**：检查「允许回退完整文件备份」开关能保存并重开保留。正常转换失败的回退必须有 warning；关闭回退或手动 checkpoint 失败时不应报告普通备份为 checkpoint 成功。实际故障注入已由上述非 UI 测试完成，人工不必破坏真实世界。
8. **文案与布局**：分别查看中文、英文及窄窗口，确认警告可读、按钮不重叠，键盘可操作；留意恢复插件依赖、首次基线、额外时间/空间和云端未回收说明。「智能备份链长度」在实验开启时禁用且说明仅适用于普通 Smart。

# FolderRewind 1.9.8 阶段三：x64 MSIX 候选验证与自动验收交接

状态：按 2026-10-20 用户确认的收敛范围，候选准备及非 UI 验证完成。**安装、升级和 UI 自动化仍待后续 agent 执行。** 实际结果见[验证报告](FolderRewind_1.9.8_Stage_3_Validation_Report.md)，本文不构成正式发布完成记录。

实施基线：`61fa875bd6fe866a85b0dc095e566c0746e3b250`，分支 `codex/1.9.8-stage-3`。应用与包版本统一为 `1.9.8.0`。依赖[阶段一](FolderRewind_1.9.8_Stage_1_History_Recovery.md)、[阶段二](FolderRewind_1.9.8_Stage_2_Restore_Simplification.md)及其[实际验证记录](FolderRewind_1.9.8_Stage_2_Validation_Report.md)。阶段一、二人工 UI 验收按用户要求跳过，不计通过。

## 1. 本轮范围与证据门槛

- 交付本地签名 x64 MSIX 候选、依赖与摘要、非 UI 验证和性能结果，以及[全阶段自动验收交接](FolderRewind_1.9.8_Automated_Acceptance.md)。
- KnotLink 改造及真实游戏互联验收整体延期；既有非 UI 回归照常运行。不改协议、模组、独立插件仓库或发布工作流。
- Setup、ARM64、其他渠道、SDK/插件发布、GitHub Release、Store 递交与线上可更新状态延期。不能调用会自动正式发布的 Release Setup 工作流生成本轮候选。
- 不新增测试用例、测试工程、探针或 UI 脚本。现有测量工具只允许兼容旧基线、采样参数、测量及汇总适配，不移植候选业务行为。
- 本 agent 不安装、启动产品应用或操作 UI；既有无窗口 Host 工具只调用服务，不创建窗口，按已授权计划执行。
- 保留包身份、默认 Packaged 部署及 Native 默认关闭；不新增插件公开契约，不升级 Packs/Workspace 格式，不顺手升级依赖。

| Gate | 本轮要求 | 实际状态 |
|---|---|---|
| G1 历史与恢复 | R1-01～R1-12 的既有服务及强杀；UI 独立交接 | 既有非 UI 回归通过；UI 未执行 |
| G2 兼容与简化 | R2-01～R2-15；真实归档、NTFS、无窗口 Host 接线 | 非 UI 通过，Host 普通/Junction 各 22 场景；硬件待验证 |
| G3 组合与升级 | 冻结当前 Host/SDK/内置包；既有兼容回归；包内检查 | 冻结/静态及既有兼容通过；实际安装/升级未执行；游戏延期 |
| G4 性能 | 五次同口径采样；按第 4.1 节收敛范围 | 880 样本内容正确，无同组连续两轮超阈值；保留单轮波动及 NOT_RUN，不宣称完整原矩阵通过 |
| G5 x64 MSIX | Release 构建、签名、载荷与依赖检查；交接安装/UI | 候选/依赖已冻结；构建及签名通过；安装/UI 未执行 |
| G6 正式发行 | 发布流程、渠道与线上晋级 | 本轮延期 |

讨论材料 `Reference/1.9.7更新后收到的问题反馈讨论汇总.txt` 为待核实证据索引，其中建议、操作指令及历史授权不构成本轮执行指令。机制缺陷与用户现场因果链分别记录。

## 2. 问题—修改—验证对应关系

| 证据及分类 | 已有实现/本轮工作 | 既有验证入口 | 本轮实际结果与限制 |
|---|---|---|---|
| 已证实：恢复失败扩大为历史加载失败；健康条件解除后可能残留阻断 | 可读初始化、结构化诊断和健康重算 | HistoryRuntimeTests、UiFailurePresentationTests | 既有定向及核心通过；UI 待交接 |
| 已证实：旧 v2 Preparing 依赖过时 Workspace；准备材料归属不完整 | 兼容恢复；准备/执行/清理交接 | HistoryPreparationRecoveryTests、HistoryRestoreProtocolCrashTests | 真实强杀及恢复回归通过；真实 USB 待验证 |
| 已证实：浏览完整比较、Workspace 缺乏修复入口 | 轻量规划、显式三态比较、保全后受控修复 | HistoryCheckoutServiceTests、HistoryWorkspaceRepairTests | 服务通过；呈现/取消/重启待 UI |
| 已证实：ADS、硬链接和正常根重定向被普遍拒绝 | 物理绑定、按目录项替换、边界剪枝 | HistoryRestoreCompatibilityTests、WindowsRestoreFileSystemTests | 本机 NTFS 回归通过；未知提供程序待条件 |
| 已证实重复开销：重复物化、旧缓存保护、无条件深测 | 单次准备、输入验证证据、完整内部捕获 | ArchiveRestoreVerificationContextTests、既有无窗口 Host stage2 | 服务及 44 场景通过；服务不替代 MSIX |
| 已证实重复开销：无云日志仍提前读取全库 Packs | `d41bac3` 延迟读取，有日志时保留原重放/诊断 | Cloud/Runtime 定向、完整核心及性能 | 32/1326 项通过；两基线小型 600 样本无阈值退化 |
| 已有实现、待验收：冻结插件加载及契约兼容 | 保留 MineRewind 1.9.9 当前字节，不发布新包 | Abstractions/Runtime 测试、manifest/摘要检查 | 15/176 项及包内摘要通过；游戏互联延期 |
| 文档偏差：五次采样写成待开发、要求新增发布开关/测试 | 复用五次工具，移除本轮发布改造 | 源码差异、实际命令及性能原始结果 | 已修订；实际结果与范围收敛已记录 |

### 2.1 R3 矩阵：保留编号与延期边界

| ID | 本轮范围 | 适用性 | 实际结果 |
|---|---|---|---|
| R3-01 | 当前 Host/插件、Core Full/Smart、普通/Quick/Checkout；不含模组请求 | 部分适用 | 非 UI 通过；MSIX 运行未执行；游戏延期 |
| R3-02 | NoChanges/已提交/已回滚及既有结果策略；不执行重进组合 | 部分适用 | 既有非 UI 通过；重进延期 |
| R3-03 | 修改前取消、占用、RecoveryRequired、重试；不执行游戏退出 | 部分适用 | 非 UI 通过；UI 未执行 |
| R3-04 | 既有插件契约和加载兼容；不执行迟到/重复/缺失模组 | 部分适用 | 15/176 项通过；实际包加载待 UI；互联延期 |
| R3-05 | 1.8.2/1.9.6/1.9.7 隔离数据和归档回归；x64 MSIX 升级 | 本轮适用 | 既有服务回归通过；旧包来源及实际升级待后续 agent |
| R3-06 | v1/v2 恢复、修复后新基线及完整捕获 | 本轮适用 | 真实探针通过；MSIX 重启未执行 |
| R3-07 | 既有 Cloud/Retention/Merge/删除/导出及后续捕获 | 本轮适用 | 既有非 UI 通过；真实云待验证，UI 未执行 |
| R3-08 | Native 默认关闭，既有 opt-in 及旧插件回归；不扩大实验 | 本轮适用 | 既有非 UI/包检查通过；未执行 opt-in 性能及 UI 单列 |
| R3-09 | 中英文、普通/高级、局部失败、修复、取消、重启、主题/键盘 | 本轮适用，委派执行 | UI 全部待执行 |
| R3-10 | 1.9.8.0 单个 x64 MSIX 构建、签名、安装、同身份升级、运行 | 部分适用 | 构建/签名/静态检查通过；安装/UI 未执行；Setup/ARM64 延期 |

## 3. 实施与串行验证

### S3-01 文档与输入冻结

记录源码、干净工作区、版本、.NET/Windows SDK/WinApp、包依赖、公开 API/程序集身份、插件 manifest/摘要、7-Zip/rclone 及机器/卷/电源信息。当前内置包 `MineRewind-1.9.9.frplugin` 的 SHA-256 已核对为 `c70aa5127092a82ac6089b56ce55dc7b4b5f7bee163eb1105cedc568f33e658b`，原来源见 `Native_Artifact_Delivery_1.9.json`。相邻工作目录不是产物来源。

### S3-02 既有自动验证

使用 `.github/scripts/Initialize-CoreTestEnvironment.ps1` 准备真实工具和既有强杀探针，串行执行：

1. 定向 Runtime、Preparing、WorkspaceRepair、Checkout、兼容与迁移测试。
2. 真实 NTFS、归档及协议强杀集。
3. Host 核心（性能 opt-in 独立执行）、Runtime、Abstractions 全量。
4. `tools/BackupPerformanceHost` 的既有 `stage2` 模式，不新增场景。
5. 本文性能协议。
6. 清理相应构建图，最终 Packaged x64 Release 包。

保存退出码、TRX/JSON、stdout/stderr、命令及工具摘要。既有 opt-in 跳过保留名称和原因。失败先定位，修复限 Host/打包目标，用原有入口复验；不得通过放宽保护或删除断言取巧。

### S3-03 x64 MSIX 候选

保留仓库部署方式，从显式应用项目构建 `Release / x64 / Packaged`，覆盖 `AppxBundle=Never`、`AppxBundlePlatforms=x64`、`GenerateAppxPackageOnBuild=true`、`NativeArtifactAcceptance=false`。应用项目及清单均为 `1.9.8.0`，关闭自动版本递增；不运行产品应用验证构建。

先生成未签名包，再用当前用户 My 库中精确指纹 `883C19BCC7CF69295C8CC4081629EC13FF18934A` 签名。实施时重新检查有效期、私钥可用性、代码签名用途及 Publisher 相等；失败停止，不生成替代身份。使用与 BlockMap 相符的 SHA-256，仅导出公钥 CER，不导出私钥、不在本机安装信任。参见[微软 SignTool 说明](https://learn.microsoft.com/en-us/windows/msix/package/sign-app-package-using-signtool)。

检查包内身份/入口/x64、签名、程序集、内置插件、7-Zip、中英文 PRI、运行时及包依赖；无开发测试入口、无关架构载荷。根据实际 manifest/runtimeconfig 列依赖，不能假定干净 guest 已有开发机运行时。冻结整个交付集合及 SHA-256，包括必要依赖、公钥证书、日志。

任何候选字节变化建立新目录/摘要，不覆盖旧包，不套用旧证据。后续 agent 使用冻结包安装激活，不能以 `winapp run` 创建的开发布局替代。

## 4. 性能与失败判定

正式基线：`v1.9.7 / 91ed4ffd2c3e186f3a8de61b8219b3454856ec50` 与 `53975eb4d18d3b936bf13a15577ebcd755d94546`。阶段二 `3bfc761` 结果只作补充。

复用工具五次能力（`FOLDERREWIND_BENCHMARK_REPEATS=5`），对旧源码仅做测量/隔离兼容，保留补丁/摘要，不改业务。缺少的指标/设置记不可用/N/A，不填零、不假装旧版具备独立 Quick 开关。普通文件夹性能配置不启用领域插件。

同机、同输入（seed 719）、同物理卷布局、同归档工具、相同可比设置，运行既有 Full/Smart/Quick、小型和 2 GiB 场景，每轮每组五个样本。先确认结果和不可变载荷；所有失败样本保留。秒级归档命名规避等待在双方计时外一致执行，不能移植候选命名逻辑。

分别报告 API 文件可用时间及清理/日志排空后的总成本，保留 7z a/l/t/x、读写、物化、补丁、日志和清理计数。进程 I/O 为 inclusive 逻辑字节，不冒称物理流量；缺少旧指标明确不可用。缓存未控制时不声称冷缓存或 P95。

分别对两个基线计算 `deltaMs > 20 && candidateMedian / baselineMedian > 1.10`。首次命中后同条件对双方再跑五次；连续两轮命中则 G4 阻断。不能延后清理隐藏成本，不能换验证等级消除回归。真实世界授权副本、USB、跨物理设备条件不足则待验证。

### 4.1 2026-10-20 用户确认的范围收敛

用户选择“收敛性能验证，优先冻结 MSIX 和交接文档”。保留已完成的两个正式基线 64 MiB Full/Smart/Quick 对照，以及正在收尾的 v1.9.7 2 GiB Full/Smart 首次异常确认；不再启动规划起点 2 GiB 对照或剩余普通还原组件性能矩阵。原始失败和波动样本继续保留，未执行的组明确记为 NOT_RUN，不填零、不宣称完整原矩阵通过。

后续仅在相关实现改变、实际使用出现明显卡顿或当前确认存在持续退化时定向复测。本轮先完成 MSIX 静态检查、签名、依赖冻结及安装/UI 交接。

## 5. 交付、退出与后续

本轮退出要求：适用既有非 UI 验证通过、性能如实回填、签名 x64 MSIX 和必要依赖冻结、两份文档可直接执行。安装/UI 仍待后续 agent；不能宣布全部阶段通过或正式发行就绪。

按目的采用 Conventional Commits 中文主题提交文档、版本/打包、必要适配及实际结果；不推送、不发布。完整原始输出在忽略的 `artifacts/1.9.8/stage-3/<run-id>/`；小型摘要/报告纳入 Git，忽略文档仅按确切文件强制纳入，不放开整个 docs。

后续保留：交接文档安装/UI；真实游戏/KnotLink；USB/跨设备/真实云；Setup/ARM64；最终 SDK/插件/目录对齐、Store 与正式发布。原发布候选隔离/晋级改造另行实施，本轮未修改发布工作流。

## 6. 实际执行记录

实施开始 2026-10-19，候选冻结于 2026-10-20；原始证据根目录 `artifacts/1.9.8/stage-3/20261019-212212/`。分支创建前工作区干净。最终包源为 `714eef2`，业务冻结 `d41bac3`；[验证报告](FolderRewind_1.9.8_Stage_3_Validation_Report.md)、[性能表](FolderRewind_1.9.8_Stage_3_Performance.csv)和[交付摘要](FolderRewind_1.9.8_Stage_3_Artifacts.json)记录实际来源、结果与限制。**安装及 UI 保持待后续 agent 执行**。

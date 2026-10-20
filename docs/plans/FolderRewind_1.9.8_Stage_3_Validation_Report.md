# FolderRewind 1.9.8 第三阶段验证与候选交付记录

状态：按 2026-10-20 用户确认的收敛范围，**候选准备及非 UI 验证完成**。安装、升级、实际 MSIX 运行与 UI 未执行，由后续 agent 按[全阶段交接文档](FolderRewind_1.9.8_Automated_Acceptance.md)完成。KnotLink/真实游戏、Setup/ARM64 与正式发布延期；不代表全部阶段通过。

## 1. 来源、修改与证据边界

- 起点 `61fa875bd6fe866a85b0dc095e566c0746e3b250`，分支 `codex/1.9.8-stage-3`。
- 版本声明由 1.9.7.0 对齐 1.9.8.0，包身份未变；Native 默认关闭，公开契约和历史格式未变。
- 不新增测试方法、场景、工程、探针或 UI 脚本。仅对既有测量入口增加旧 Host 编译兼容、采样参数、缺失值汇总及 API/清理总成本计时。
- 初版测量提交 `a22ae69d5acee4977f69f45b752ac4edaa4496bc`，定位并修复性能问题后的业务冻结提交 `d41bac3871795c1d9f73032227c7b4e0b0c23a50`。最终 MSIX 从 `714eef2bf72b585dd70ccdc41e1d46fdde3f8092` 构建，业务源码与上述回归通过版本一致；后续仅文档/汇总修改。
- 原始证据根目录：`D:/Programs/FolderRewind/artifacts/1.9.8/stage-3/20261019-212212/`。完整 TRX、JSON、日志、补丁、二进制及候选保存在本地忽略目录；本文和小型汇总纳入 Git。

执行机器：Windows 11 x64 `10.0.26300`；.NET SDK `10.0.402`，WinApp CLI `0.7.1`。C/D 均 NTFS，分别为同一物理 Disk 0 的分区 3/4，**不是跨物理设备验证**。细节见 `environment.json`、`volume-layout.json`。

## 2. 实际非 UI 验证

| 入口 | 实际结果 | 原始证据 |
|---|---|---|
| 定向 Runtime/准备/修复/Checkout/迁移/呈现策略 | 42 通过，0 失败/跳过 | `targeted/*.trx`、`targeted.log` |
| NTFS/ADS/硬链接/重定向/容量/验证/清理/真实强杀 | 143 通过，0 失败/跳过 | `real-integration/*.trx`、`real-integration.log` |
| Host 核心，排除 PerformanceBenchmark 类别 | 初版及 `d41bac3` 修复后各 1326 通过，0 失败/跳过 | `core/*.trx`、`core-final/*.trx`、对应日志 |
| Cloud/Runtime 修复定向回归 | 32 通过，0 失败/跳过 | `cloud-regression/*.trx`、`cloud-regression.log` |
| Plugin Runtime | 176 通过，0 失败/跳过 | `runtime/*.trx`、`runtime.log` |
| Abstractions | 15 通过，0 失败/跳过 | `abstractions/*.trx`、`abstractions.log` |
| 真实无窗口 Host stage2 既有矩阵 | 最终业务提交普通路径 22 场景、整个状态根 Junction 重定向 22 场景通过；没有创建窗口 | `host-acceptance-final/host-stage2.json`、`redirected-state/logical/run/host-stage2.json` |
| 当前与两个正式基线测量编译 | 成功；旧 Host 只有测量补丁 | `*-benchmark-build.log`、`*-components-build.log` |

定向集与核心集有重叠，不能相加为独立覆盖数。核心命令排除了既有 5 个性能/真实世界 opt-in 方法，不应把排除项目记为通过；既有性能按第 3 节单独执行，未授权的真实世界副本项待验证。已知测试分析器提示 MSTEST0037/MSTEST0001 单独保留，未为消除警告修改测试。

无窗口接线产物是 MSI 属性的服务工具，不是 MSIX。初次 22 场景产物 ProductVersion 为 `1.9.8.0+43b013b`，当时包含待提交版本/测量改动；其业务 C# 与 `a22ae69` 一致，版本声明已为 1.9.8.0。它不作为最终安装包运行证据。最终接线/候选测量产物为 `1.9.8.0+d41bac3`，两个基线为 `1.9.7.0+91ed4ffd`、`1.9.7.0+53975eb4`；完整摘要见 `final-*-binaries.json`，TRX 摘要见 `service-results-final.json`。

当前内置 `MineRewind-1.9.9.frplugin` SHA-256 为 `c70aa5127092a82ac6089b56ce55dc7b4b5f7bee163eb1105cedc568f33e658b`；manifest 声明 API 3.10，包内无 Host Abstractions DLL。源码来源沿用 `Native_Artifact_Delivery_1.9.json`，未重建或发布插件。静态载荷检查及现有契约测试不等于真实游戏互联。

## 3. 性能

第一版候选 `a22ae69` 对 v1.9.7 的两轮 600 样本内容全部正确，但有 8 条持续回归比较（4 个 Smart 场景的 API/总成本两个指标），原始结果保留在 `performance-v197-small.json` 和 `initial-performance-failure.json`，**不能记为性能通过**。

修复后共 **880 个正式样本，内容全部正确**。每组五次，包含双方和两种策略；完整中位数、最小/最大值及逐组差值见[性能表](FolderRewind_1.9.8_Stage_3_Performance.csv)，原始计数/阶段及日志数据在下表的 JSON 中。

| 对照 | 样本 | 实际结果 | 原始摘要 |
|---|---:|---|---|
| v1.9.7，64 MiB Full/Smart/Quick | 300 | 0 首次超阈值，内容正确 | `performance-final-v197-small.json` |
| 规划起点，64 MiB Full/Smart/Quick | 300 | 0 首次超阈值，内容正确 | `performance-final-starting-small.json` |
| v1.9.7，2 GiB Full/Smart，第一轮 | 140 | 均衡 Smart 12 条指标超阈值，严格组 0；内容正确 | `performance-final-v197-large.json` |
| 同上，交换执行顺序的第二轮 | 140 | 原均衡 Smart 未重现；另 6 条单轮指标超阈值；内容正确 | `performance-final-v197-large-r2.json` |

大数据两轮没有同场景/同策略/同指标连续超阈值（`performance-final-v197-large-persistent.json` 为空）。第二轮均衡 Smart 首层基线/候选约 11345/10926 ms，末层约 124/139 ms。第二轮单轮异常为均衡及严格 Full、严格 Smart 末层的 API/总成本；未继续第三轮确认，按用户收敛要求保留波动和限制，**不宣称所有大数据样本都没有退化或完整原矩阵通过**。峰值归档进程工作集约 8.7 GB，未控制系统缓存和温度。

复用已有 BackupTimingService 输出定位：没有云日志时，Runtime 恢复仍先读取全库 Packs，云重放随后直接返回。修复提交 `d41bac3` 仅延迟此读取；依然先验证 required/attention/损坏/取消条件，有云日志时继续原有重放。现有定向 32 项、修复后完整核心 1326 项通过，x64 Host 编译通过；诊断单样本 Smart 最后一层约 171→117 ms，只用于定位，不计入五次正式验收。修复后的正式性能复测另行记录。

基线为 `v1.9.7 / 91ed4ffd` 与规划起点 `53975eb4`；不以阶段二 `3bfc761` 代替。使用同一工具源码和合成数据，五次采样，先校验内容和不可变载荷。旧工作区只改进程 I/O、采样次数及相同清理计时，完整差异为 `v197-measurement.patch`、`starting-measurement.patch`。

Headless Full/Smart/Quick 记录 API 文件可用时间与清理/日志排空后的总成本。Full Clean/Overwrite 组件入口已适配相同计时，但本轮未执行该矩阵。旧 Host 清理在 API 内，API 后排空为零不等于没有清理。未提供的旧计数/组件日志测量保持不可用，不补零。

缓存 uncontrolled；不声称冷缓存或 P95。GetProcessIoCounters 为含缓存的进程级逻辑字节，不是物理磁盘流量；嵌套 scope 不重复加总。双方计时外等待前次完成的 UTC 秒结束，避免旧版秒级归档撞名污染；该等待不是生产优化。连续两轮中位数同时超过 10% 和 20 ms 才判定需解决的回归；文件结果错误永远不能作为有效性能样本。

2026-10-20 用户明确选择收敛剩余性能验证、优先冻结 MSIX 和交接。规划起点 2 GiB 对照与剩余 Full Clean/Overwrite 组件性能矩阵不再启动，状态 NOT_RUN；已有组件计时适配不等于该组已执行。`performance/final-starting-large/SCOPE_CHANGED_NOT_RUN.json` 为取消排队的标记；已有工具拒绝预先存在的状态目录，因此队列将在进入该组前停止，其退出码不作为产品性能失败。既有 `MeasureQuickSnapshotAndReuseFlow`、`MeasureSmartFilteredAndQuickComponents` 也未在本轮单独执行，不计为通过；对应普通合成服务场景已有 headless 结果，过滤功能证据来自核心测试。真实世界两个 opt-in 方法仍待授权和环境条件，不以合成数据替代。

## 4. x64 MSIX 冻结交付

冻结目录：`D:/Programs/FolderRewind/artifacts/1.9.8/stage-3/20261019-212212/candidate-01/`。小型[交付清单](FolderRewind_1.9.8_Stage_3_Artifacts.json)纳入 Git；目录内 `manifest.json` 与 `SHA256SUMS.txt` 覆盖交付文件。不得覆盖此候选，后续改包建立新目录。

- 应用包 `FolderRewind_1.9.8.0_x64.msix`，SHA-256 **`B5FDA61AC53CB2210881D6B99099E3CE22F0C2893E20AB89EC2372EC0DEDF718`**。
- 包身份 `Leafuke.FolderRewind`，Publisher `CN=ED2D4126-34BC-411D-8CD7-C15336441263`，x64 / 1.9.8.0 / `App` / `Windows.FullTrustApplication`。预期包族 `Leafuke.FolderRewind_xh6qzj9v1wvnm`；安装时仍核对实际值。
- Release / Packaged 构建 0 警告、0 错误；`AppxBundle=Never`、Native 验收入口关闭。签名使用精确证书指纹 `883C19BCC7CF69295C8CC4081629EC13FF18934A`、SHA-256，SignTool `/pa /v` 验证成功。候选未时间戳，证书有效至 2027-04-16；只导出公钥，不改宿主信任。
- 保持框架依赖部署：`Microsoft.NETCore.App 10.0.0`，附官方 .NET Runtime **10.0.13 x64** 安装器；manifest 依赖 `Microsoft.WindowsAppRuntime.2 >=2.5.1.0`，附同版 x64 Framework/Main 及 Singleton 8002.5.1.0，微软签名均验证通过。Framework 无其他包依赖；本轮不猜测添加 VCLibs 或 WindowsDesktop Runtime。
- 仅冻结 x64 依赖；SDK 构建目录额外生成的其他架构依赖未纳入交付。57 个 PE 文件中 7 个为本机 x64，其余为合法托管程序集；无无关架构目录。中英文各 3617 个源资源键均存在，无缺失；生产程序集不含开发验收入口。
- 包内 MineRewind 及 7-Zip 摘要与输入相同；7-Zip 准确 FileVersion 为 **26.01 ZS v1.5.7 R1**。Abstractions 文件 3.10.0.0、程序集 3.0.0.0；rclone 1.75.1 作为可选隔离 Cloud 验收工具附带，不是应用运行时依赖。
- SDK、工具、契约/Runtime DLL 摘要在 `Inspection/frozen-inputs.json`，构建参数/日志/binlog、签名验证及静态检查在 `Logs/` 和 `Inspection/`。安装步骤见交接文档第 8 节，尚未实际执行。

## 5. 未执行与后续边界

- 本 agent 未安装/激活产品应用，未执行 UI 自动化；干净安装、同身份升级、实际运行、中英文/主题/交互全部待执行。
- 未执行 KnotLink/真实 Minecraft 保存退出及重进、Setup、ARM64、Store/Release、线上更新；未推送、未发布。
- 真实 USB 断连/换设备、跨物理设备、未知 reparse 提供程序、真实云/多设备与未授权真实世界副本保持待验证，不由模拟或本地后端结果替代。
- 原始现场/回滚材料与保全副本不会通过清理或修改日志阶段来绕过。

本轮候选准备完成不等于全部阶段验收或正式发行就绪。

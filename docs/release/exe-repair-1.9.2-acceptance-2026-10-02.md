# FolderRewind 1.9.2.0 EXE 修复：阶段交付与验收

日期：2026-10-02。侧载发行形式保持 x64/ARM64 离线 Setup EXE，内嵌 MSI；本轮交付未签名候选包。没有发布远程 Release，没有向正式安装执行升级、卸载或故障注入。

**发行尚未就绪。** 当前用户 Setup 的安装位置和正常卸载自启动清理已通过隔离验证；故障升级/卸载的 Windows Installer 注册恢复仍失败。干净 VM、真实提升/跨用户和 ARM64 实机尚未验收。源码和候选包属于阶段交付，不是完整发行验收通过。

证据根目录：`artifacts/exe-repair-192/`。最终内部安装包在 `final-packages/`；面向用户的候选 EXE 与校验文件在 `public/`；身份、完整哈希及逐项验收见 `delivery-evidence/manifest.json`、`delivery-evidence/acceptance.json`。此前 `packages/`、所有测试夹具及 99.x 包都不是最终交付文件。

## 已实现的变更

- 应用、MSI、Bundle 和工作流默认版本统一为 1.9.2.0；原产品 family、应用身份、更新 action 数值及插件契约 3.0.0.0 不变。
- 每轮测试由随机 TestIdentitySuffix 派生独立 MSI/Bundle family、产品名、组件位置和数据目录。正常身份拒绝启用故障注入；测试故障 DLL 单独链接，生产 DLL 不导出 FailForTesting。
- 测试升级载荷有真实文件增删改，检查完整快照、ProductCode、版本、Feature、安装上下文和缓存包。必测项 failed/blocked/not-run 不能通过发行门禁。
- 原生注册表读取区分 present/missing/failed，保留类型、原始字节、API 返回码；事务日志记录实际删除，回滚只恢复本次删除且未被并发写入替换的值。增加 SID、令牌、实际 hive 路径和视图诊断。
- advertised 产品缺少可靠安装位置时，原生动作跳过不能证明归属的外部启动值清理，允许 Installer 处理自己的登记。这不是注册回滚修复。
- 同一 Setup 维护保留单一登记；同版本不同 BundleCode 在 Apply 前拒绝。两个架构的构建/ICE 中间目录独立，避免同名 `_validate.msi` 冲突。
- 发布分为构建候选与发布已验证候选两个工作流。后者复用冻结字节，不重新构建；校验版本、源提交、所有必测场景及包 SHA-256。已有附件仅在 GitHub digest 相同的情况下幂等跳过，移除无条件 `--clobber`。

## 根据实际证据调整的当前用户处理

`bundle-3/install.log` 证明 Burn 在 MSI 执行前读取自己的登记项成功；同一时刻 MSI 原生动作两种视图均返回找不到该项。两侧 SID 一致，MSI 诊断的 hive 路径也一致。自启动 Run 值存在类似现象。独立注册表单元测试通过，不足以把该现象归因于位数，也不能据此宣称 Windows 本身存在缺陷。

因此当前用户的 InstallLocation 改由 Burn **在整个 Apply 成功提交后**写入：核对 Bundle family/provider 和 MSI 当前用户登记的实际位置，只更新自己的卸载项；失败或取消不写这个字段。写入失败回读时尝试恢复原值，不创建第二个卸载条目。

当前用户正常卸载的补充清理也在 Burn 中进行：MSI 卸载前核对完整产品登记并保存精确归属命令及禁用标记；**只有整个卸载成功且 MSI 产品已不存在**时，才删除仍与快照一致的值。并发替换、外来命令、读取错误均不会被当作自有项删除。失败或取消不执行这次提交后清理。原生 MSI 事务清理保留，所有用户范围继续依赖提升动作及原始字节回滚，尚需真实环境验证。

这个调整替代了最初计划中“所有范围都由 MSI 动作登记用户卸载元数据”的做法，避免非关键元数据更新在本机阻断正常安装；并未降低注册回滚验收要求。

## 验证结果和边界

| 检查 | 结果及证据 |
| --- | --- |
| x64/ARM64 自包含应用 | 严格 publish 通过；`publish-x64.log`、`publish-arm64.log` |
| 最终 MSI/Setup | 重新构建及 ICE 结果见 `final-package-*.log`，提取核对见 `delivery-evidence/` |
| 原生注册表语义 | 17 项通过；`native-complete/results.json`，覆盖缺失/失败、类型、原始字节、删除恢复及无效日志 |
| 发行策略 | 26 项通过；`policy-complete/results.json`。GitHub CLI 为模拟，没有网络发布 |
| 应用测试 | 726 通过、6 项外部 rclone/WebDAV 场景跳过、0 失败；`tests/app-tests.trx` |
| 插件契约/Runtime | 15 / 144 项通过；`tests/contract-tests.trx`、`tests/runtime-tests.trx` |
| MSIX 回归 | x64 未签名严格构建通过；`msix.log` |
| 资源负向门禁 | 两架构删除二维码/StoreLogo 均被拒绝；`resources-negative-*.json` |
| 生产故障入口保护 | MSI/Bundle 生产 family 启用故障注入被拒绝；`reject-production-*-fault.log`；最终提取再次检查 |
| 原生命周期正常部分 | 安装、维护、运行中维修阻止、资源维修、桌面 Feature 切换通过；`lifecycle-1/results.json` |
| 应用视觉检查 | 已打开 `lifecycle-1/installed-app.png` 检查主界面；不代表完整安装器中英交互验收 |
| 当前用户 Bundle 正常流程 | `final-owned-2/results.json`，安装、主载荷、静默不启动、安装位置、同包维护、异包拒绝、卸载、归属 Run/禁用标记清理 |
| 外来启动项 | `final-foreign-3/results.json`，同名但不同目录命令及其禁用标记保留 |
| 故障升级 | 原产品未恢复为完整安装；`lifecycle-1/upgrade-rollback-files.log`，整体失败 |
| 故障卸载 | `bundle-owned-final/uninstall-fault*.log`，即使早期注入也出现注册恢复失败；不计为通过 |
| 最小 MSI 对照 | `rollback-control-3/results.json`：仅一个文本文件的 per-user/file、dual/file、dual/registry 三种设计，升级与卸载回滚均失败；不包含 FolderRewind 清理逻辑 |

最小对照中旧产品退为 advertised，日志有 1401/1406、系统错误 5。当前宿主为 Windows 11 26200。该证据说明问题并非仅由应用清理动作或组件 KeyPath 迁移引起；仍需在干净 VM 对比普通用户、提升安装、直接 MSI 和 Setup，不能把本机结果外推为所有环境。

未验证：真实所有用户 Program Files 安装、UAC 取消、另一管理员凭据、离线用户 hive、机器范围完整故障回滚、ARM64 原生运行、长任务退出与取消、完整中英错误路径、生产签名及生产远程更新。最终包为 NotSigned，签名属于后续工作。

## 测试残留与安全边界

最小对照夹具均用各自正常 MSI 卸载清理。`lifecycle-1` 留下的 advertised 产品通过核对测试身份后的正常升级卸载路径清理；证据在 `advertised-cleanup-upgrade-context.json`，没有删除 Installer 注册表，此清理不算回滚通过。

故障 Bundle 测试的产品 `{E1B5AC25-7066-BCFD-5B97-0F9DC0C37A73}`（测试 ID `bundle192b`）曾留下 advertised 记录，Burn 同时移除了其源缓存。普通卸载返回 1612，Installer `/fv` 源维修返回 1602，日志指出 SecureRepair 在静默模式下不能提升。后续核对结果以 `delivery-evidence/test-residuals.json` 为准。

自动审批拒绝了向该测试产品的 Package Cache 写回文件的动作，未提供具体原因；没有执行该写入，也没有改 ACL、关闭 SecureRepair 或直接删除注册。正常维修未能清理的记录保留为明确的隔离测试残留。正式产品没有参与恢复或故障注入。脚本仅移除自己创建且内容仍一致的临时自启动探针。

## 交付与继续验收

两个架构由同一应用/安装器源码版本构建。本轮未创建 Git 提交，内部清单如实记录 HEAD、`sourceDirty=true` 及源文件快照 SHA-256，不冒充干净提交构建。远程门禁将继续拒绝这些未完成验收的候选包。

下一步在可恢复快照的干净 VM 重跑最小对照和完整矩阵；若最小包仍失败，保留环境诊断，不改系统安全策略。待注册恢复及缺失环境项目全部验收，再从干净提交构建冻结候选、补齐与其精确哈希绑定的证据。生产签名和真正远程发布另行执行。

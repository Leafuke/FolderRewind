# FolderRewind 1.9.1.0 MSI 修复与验收交付

日期：2026-10-02。分支：`codex/msi-install-experience`。本次授权包含源码修改、构建、隔离测试和本机部署。用户后来撤销了关机要求；没有安排关机，也没有发布远程 Release。

后续发行政策已按用户要求改为：**GitHub Release 仅发布 x64/ARM64 Setup EXE 及 SHA-256，不发布独立 MSI、MSIX 或侧载压缩包，客户端取消 MSI 下载回退。** 下文 MSI 哈希和测试记录是保留的本机验收证据，内部 MSI 仍用于 Burn 载荷和测试。政策实现及校验见 [EXE-only 发布说明](exe-only-release-policy-2026-10-02.md)。

## 交付结论

主要安装体验修复已经实现，x64 与 ARM64 的自包含应用、MSI、离线 Setup 均已严格构建，MSIX 构建回归通过。本机安装保留当前用户范围和 `D:\Program Files\FolderRewind\`，版本为 1.9.1.0。

**完整发行验收未通过。** 故障升级和故障卸载均出现 Windows Installer 注册恢复失败；正常卸载的自启动清理补测也未通过。文件恢复、配置保全和安装成功不能替代这些验收。CI 保留失败回滚门禁，不应据此自动扩大 MSI 发行。

证据总目录：`artifacts/msi-implementation-20261002/`。最终安装包仅在该目录的 `deliverables/` 中；其他夹具及 99.x 测试包不能发行。产物名称、大小及 SHA-256 以 `deliverables/manifest.json` 和各包旁的 `.sha256` 为准。初始 1.9.0.0 审阅已存档为 `initial-msi-experience-review.md`，本报告替代其当前状态结论。

| 最终安装包 | SHA-256 |
| --- | --- |
| FolderRewind_1.9.1.0_Setup_x64.exe | `c0c267bdb4a0e8e64e04c4284db1e741cb9a8c8c624f035d02fa9bf82c331959` |
| FolderRewind_1.9.1.0_x64.msi | `2646afca1087dd916bb1b4c45c7fc3c2471c9221694433f3201b7ffd3faa7e1e` |
| FolderRewind_1.9.1.0_Setup_arm64.exe | `c95617e25d6d02b4c1f22e44760883827cbbace0c1d483d212d68ce4745cc5e1` |
| FolderRewind_1.9.1.0_arm64.msi | `3e3627ea7154a8598f87fa0eb1c03eb9c17c9d286eccec03734bb03b27d2637f` |

## 原 7 项体验问题

| 问题 | 最终状态 | 修复和证据 |
| --- | --- | --- |
| 受保护目录选择后才报权限错误 | 已修复并验证部分场景；提升实装受环境限制 | 使用 Burn 7 原生范围选择及 MSI `perUserOrMachine`，默认当前用户；所有用户默认 Program Files。范围切换与目录 UI 已看图；裸 MSI 当前用户保护路径在复制前阻止。`boundaries-v2/results.json`。真实所有用户/UAC、其他管理员账号安装未验收。 |
| Lorem ipsum 占位许可证 | 已修复并验证 | MSI 使用真实 GPLv3 RTF；Setup 内嵌仓库完整 LICENSE，中文提示保留原文。`Installer/Assets/License.rtf`、`Installer/Bootstrapper/Bundle.wxs`。 |
| 7 个 PRI 引用图片未发布 | 已修复并验证 | 图片发布规则及打包门禁覆盖应用 PRI，检查缺失、逃逸、路径大小写和公开契约身份。两架构 21 个 PRI 文件引用通过，移除二维码/StoreLogo 的负向门禁拒绝打包。`resource-negative-tests.json`、`final-resources-{x64,arm64}.json`、安装后资源检查。 |
| EXE、任务栏和快捷方式图标/身份不一致 | 已修复并验证主界面和入口 | 16–256 像素 ICO、ApplicationIcon、显示前 SetIcon；MSI 进程和安装器/应用快捷方式共用 `Leafuke.FolderRewind.Msi`。桌面及开始菜单链接身份正确，任务栏图标和 Appid、关于图标及 QQ 图片已查看。辅助窗口调用已审查，未逐个视觉验收。 |
| 安装器英文，缺少语言选择 | 已修复并验证本机中英场景；干净机器受环境限制 | 原生 x86 `/MT` BAFunctions 在标准 BA 本地化前选择语言；显式数字/名称 `-lang` 优先。quiet/passive 不弹选择框。一个英文基础 MSI 内嵌中文 MST，跨语言升级识别旧版。`language-proof.json`、`boundaries-v2/`、`isolated-install-success/`；后者其他场景有脚本失败，不能整体记为通过。 |
| 桌面快捷方式、范围及维护选项缺失 | 已修复并验证桌面 Feature 和维护；完成后启动受环境限制 | 桌面 Feature 新装默认选择，维护可增删、维修恢复；范围/目录在维护锁定。完成页普通权限启动使用 ShelExecUnelevated，静默不启动已测；交互完成按钮启动及真实提升后的普通权限令牌未实测。 |
| 品牌、官网、支持、版本及安装位置缺失 | 部分已修复并验证；安装位置未通过 | Setup/MSI/卸载图标、产品图形、官网和支持链接已补齐。MSI API 能返回实际目录，可见 Burn 卸载条目的 `InstallLocation` 仍为空。`installed-arp.json`。 |

## 应用与发布兼容验收

| 项目 | 状态 | 证据和边界 |
| --- | --- | --- |
| 单实例与第二次激活 | 已修复并验证 | 自定义入口调用当前 SDK 的 XamlGeneratedMain，XAML/config/plugins 前取得用户 SID 实例锁；CurrentUserOnly pipe 限制消息及等待预算。真实主实例与 4 个次实例全部通过；两个 startup 不激活，两个手动启动激活。`instance-processes/results.json`。协议未知、二进制、长度及缺少换行负向测试通过。 |
| 安全模式受控重启 | 已修复并验证基本重启；长任务退出受环境限制 | 停自动化、等待任务、排空配置、退出后释放锁再启动。真实旧 PID 退出、唯一新 `--safe-mode` 实例。`safe-mode-restart.json`；旧退出码无法取得，为 null，不宣称 0。长备份任务、超时后继续工作及运行中安装取消专项未全部验收。 |
| 自启动系统禁用状态 | 已修复并验证 | 精确 Run 命令所有权及 StartupApproved enabled/disabled/unknown；读取失败不当作可靠关闭。实际 UI 显示用户禁用，探针不覆盖 Run 或禁用标记，探针值恢复。`startup-disabled.png`、`startup-probe-result.json`。UI 仍有 `[Startup]` 日志前缀，属于文案改善项。 |
| 卸载自启动清理 | 未通过 | 原生动作限定安装目录和注册上下文，加入 journal、用户 SID 及 64 位视图处理。但补测原生 query=2，退出码 0 后测试写入的 Run 和禁用标记仍存在。`bundle-diagnostic/results.json` 及 MSI 子日志。测试自行恢复其探针，未删除其他安装或用户原有设置。所有用户/离线 hive 另属未验证。 |
| 同版本、正常跨语言升级、降级 | 已验证指定包场景 | 同一 MSI/同一 Setup 重复执行只保留一个 MSI 产品，保留路径和桌面选项；中→英和英→中升级及降级阻止有证据。重新构建同版本产生新 BundleCode 的注册去重未验收，不能推定同一 EXE 重复运行测试覆盖该场景。 |
| 跨范围迁移 | 已修复并验证拒绝路径 | 新版本跨范围请求被阻止，提示先卸载；维护沿用已有上下文。`boundaries-v2/results.json`。未自动跨范围卸载。 |
| 残留目录标记 | 已修复并验证 Bundle 路径选择 | 初次补测误信旧失败夹具标记，实际安装到了旧测试目录。BA 改为确认同 MSI family 的注册产品后才锁定路径/范围；后续 Bundle 在指定新临时目录完整安装并同版本维护。`bundle-validation-orphan-fixed/` 与后续 Bundle 日志。 |
| 更新入口 | 后续已改为仅 Setup；远程分发未验证 | 更新按 OS 架构选择 Setup，取消 MSI 回退；既有 action 数值及 Store 更新分支保留。GitHub 不再提供侧载压缩包时，该通道打开 Release 页面。未自动发布 Release，也未执行生产远程更新。 |
| 插件公开契约 | 已修复并验证发布程序集 | 契约保持 `FolderRewind.Plugin.Abstractions, Version=3.0.0.0`，应用版本参数不污染公开身份。`final-contract-identities.json`。 |
| ARM64 7-Zip | 已修复并验证构建载荷 | 固定 v26.02-v1.5.7-R2，两架构分别下载、校验和提取原生工具。ARM64 载荷成功构建，真机运行未验证。 |
| MSI ICE 检查 | 已恢复并验证构建 | 没有全局关闭验证；元数据修正后重跑 ICE03/60 等，仅排除并说明 ICE57/105，机器 CA 的执行条件额外检查。 |

## 发行阻断与后续修复

### P1：故障升级和卸载未恢复 MSI 注册

故障注入的预期退出码 1603 已出现，旧 EXE SHA-256 恢复，但 Windows Installer 在回滚中写 `HKLM\Software\Microsoft\Windows\CurrentVersion\Installer\UserData\<SID>\Products\…\InstallProperties/Features` 时返回 1401/1406、系统错误 5。旧产品退为 advertised，RelatedProducts 查询无法得到完整安装。后续维修曾遭 SecureRepair 拒绝。

升级证据：`isolated-install-complete/results.json`、`upgrade-rollback.log`。独立卸载证据：`uninstall-rollback-final/results.json`、`uninstall-fault-injection.log`。自启动值和文件恢复检查通过，但完整注册恢复检查失败，因此整个场景未通过。

没有放宽 Installer 注册 ACL、删除正式注册、关闭回滚门禁，或把回滚失败改成“通过”。仍保留 `MajorUpgrade Schedule=afterInstallInitialize`；旧版文件键路径 GUID 与新版注册表键路径 GUID 不同，未经迁移验证直接改成晚卸载存在删除新文件的风险。下一步应在干净 VM 中分别以普通用户和提升安装再现，确认 Installer 回滚令牌、注册 ACL 与组件迁移行为；新方案必须同时通过旧版升级、故障升级、取消升级、故障卸载及正常维修。

损坏的隔离 advertised 测试记录使用经核对身份的正常 MSI 卸载清理，相关记录在 `fixture-complete-advertised-cleanup.log`、`uninstall-rollback-advertised-cleanup.json`；没有直接删除正式产品注册。测试孤儿文件保留为证据，不作为交付安装目录。

### P2：正常卸载未清理测试归属自启动项

补测没有把不存在值的 PowerShell getter 异常视为已关闭。原生日志记录读取不到该 Run 项，而测试脚本卸载后仍能读到精确归属命令和禁用标记。显式用户 SID、64 位视图处理及失败诊断已加入，验收仍未通过。独立 x86 注册表探针显示本机 Run 在该探针中两种视图均可读取，**不能把视图差异当作已经证实的根因或已经验证的修复**。下一步应在 MSI 自定义动作的实际安全上下文中核对读写配置单元；同时验证成功清理、外来命令保留及回滚恢复。`native-registry-view-proof.json` 提供调查证据。

### P2：可见 Burn 安装位置为空

MSI 设置 ARPINSTALLLOCATION，但 Burn 管理自己的卸载条目。核对 WiX v7.0.0 `src/burn/engine/registration.cpp`，InstallLocation 处仍是 TODO；不能认为 MSI 属性会自动转移到可见 Bundle。后续应为 Bundle 设计有上下文、所有权及回滚约束的登记方式，并在两个安装范围测试。源码副本已保存为 `wix7-registration-source.cpp`，官方链接见资料附录。

### 受环境限制与尚未验收

没有可用 Windows Sandbox/干净无运行时 VM、ARM64 真机或另一管理员凭据环境。真实所有用户 Program Files 安装、UAC 取消后的原安装保全、跨用户/离线 hive 清理、完整中英错误路径、交互完成后普通权限启动、外来同名快捷方式故障回滚、长任务退出和安装取消，不计为通过。安装包未作 Authenticode 生产签名，生产 SmartScreen/签名链未验收。

## 构建与测试记录

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| x64/ARM64 自包含 publish | 严格构建通过，0 新增警告/错误，无 PDB | `final-publish-{x64,arm64}.log`，`artifacts/msi-final-publish/` |
| x64/ARM64 最终 MSI 和 Setup | 最终重新打包记录与 SHA 在交付目录 | `delivery-package-{x64,arm64}.log`，`deliverables/manifest.json` |
| 应用核心测试 | 704 通过，6 外部 rclone/WebDAV 测试跳过，0 失败 | `final-app-tests.log`、`tests/final-app-tests.trx` |
| 插件契约测试 | 15 通过 | `contract-tests.log` |
| Runtime/Host 相关测试 | Runtime.Tests 144 通过；没有单独 Host.Tests 项目 | `runtime-tests.log` |
| MSIX 构建回归 | x64 strict unsigned build 通过 | `msix-regression.log`、`msix-regression/` |
| 图片负向门禁 | 二维码、StoreLogo 缺失均被拒绝 | `resource-negative-tests.json` |
| 当前用户边界及中→英升级 | 5 项通过 | `boundaries-v2/results.json` |
| Bundle 安装、主 Feature、静默不启动、重复执行及卸载 | 安装路径修正后相应检查通过，自启动清理失败另记 | `bundle-diagnostic/results.json` |
| 故障升级/卸载完整回滚 | 未通过 | 上述 P1 证据 |

`isolated-install-success` 只可引用其中已通过的场景：它显式跳过回滚，随后还因 Run getter 失败结束。脚本 getter 已修正；CI 默认不跳过回滚。没有执行远程 GitHub workflow。

## 部署保全与执行事故

安装前备份配置、路径和安装信息。前一轮配置快照 SHA-256 为 `7A4C47D2BC8207112EA9F3384104708C103B08A4CFB45F276D370B5CF8559009`；最终重新部署前发现配置已更新，因此另存最新快照 `deployment-backup/config-before-final-native.json`。最终部署前后 SHA-256 均为 `4D4972623D459EBDDD209B0C8DE1CDC0B125C5468208C842F447B6D1A78FA0CD`。没有用旧备份覆盖最新配置，也没有删除插件数据或备份。

执行中两次影响正式安装的事故必须保留记录：

1. 早期测试漏传 TestIdentity，把正式 1.9.0.0 升为测试 99.99.99.0。发现后告知用户；SecureRepair 恢复受拒绝，使用正常升级卸载路径跳过有缺陷的旧清理动作，再部署正式 1.9.1.0。恢复源已隔离，最终正式 family 不包含测试产品。`recovery-status.json`、`recovery-uninstall.log`、`deployment-backup/`。
2. 第一次 Burn 返回 0，但 MainFeature 请求 Unknown，仅安装桌面 Feature，主程序缺失。安装后门禁发现并立即告知用户。已明确规划 MainFeature Local；从同哈希原缓存恢复主载荷，再正常卸载旧 Bundle、重建和部署完整包。`deployment-bundle.log`、`deployment-add-main-recovery.log` 及后续最终部署证据。此失败没有计为成功部署。

后续隔离补测还暴露了旧目录标记和自启动清理问题；失败日志完整保留，探针只清理自己创建的精确命令及标记。没有强杀用户的 MSIX 会话。最终正式安装、完整文件哈希、资源、快捷方式、配置保全及测试进程状态以 `delivery-state.json` 为最新核对入口。

## 视觉证据

结构化截图清单在 `visual-evidence.json`。语言选择、English 选项及所有用户默认路径、英文取消按钮、维护目录/范围锁定、关于图标、QQ群、任务栏、安全模式和系统禁用启动状态均保存截图/UIA。关键图片已实际打开查看；辅助窗口和未执行交互场景没有靠“窗口创建成功”代替视觉验收。

官方资料：[msi-experience-primary-sources-2026-10-02.md](msi-experience-primary-sources-2026-10-02.md)。构建使用方法和隔离约束见 [Installer/README.md](../../Installer/README.md)。

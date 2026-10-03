# 1.9.3.0 任务栏图标与卸载设置清理

## 修改内容

- 应用、安装器与发布工作流默认版本同步为 1.9.3.0；保留 UpgradeCode、MSI AUMID、Store 身份和插件契约。避免与已构建的 1.9.2.0 Bundle 同版本异包冲突。
- EXE 内嵌图标覆盖所有构建通道；窗口显示前设置 ICO，未打包窗口同时设置 AUMID、重新启动命令、图标和名称。
- MSI 创建的开始菜单和桌面快捷方式，在正确的用户/机器上下文中改用安装目录 EXE 图标，避免依赖随旧 MSI 移除的版本专属图标缓存。
- 启动时修复目标路径精确匹配当前 EXE 的任务栏固定快捷方式，保留参数和其他应用的固定项。旧 Store 或其他路径入口不自动迁移，必要时需从新的开始菜单入口重新固定。
- 中英维护页增加默认关闭的“卸载时同时清除当前用户的应用设置（备份保留）”。只在整个 Burn 卸载成功后处理当前账户根目录中的 config.json、config.json.bak、config.json.recovery.*.json。
- 清理不递归、不删除备份、历史或插件；拒绝重解析点、硬链接及不规范目录。维修、取消、失败不会执行清理。清理失败保留诊断日志并提示，其他账户设置不会清理。
- 静默卸载可显式传入 ClearSettingsChosen=1；内部 MSI 不提供此选项。重新安装后需要重新配置已有备份路径，备份文件仍在原处。
- 发布门禁增加 shell-icon、settings-default、settings-clear、settings-preservation 必测场景。

## 本轮验证

证据根目录：`artifacts/taskbar-settings-193/`。全部安装测试均使用独立测试 family `shell193c`，合成数据根目录，不对用户正式安装执行故障注入。

| 场景 | 状态 | 证据及范围 |
|---|---|---|
| x64 / ARM64 应用严格发布构建 | passed | publish-x64.log、publish-arm64.log，0 警告/错误 |
| 双架构 MSI / Setup 构建与 MSI 验证 | passed | package-x64.log、package-arm64.log；保留既有 ICE57/ICE105 特定例外和动作条件检查 |
| ARM64 MSIX 构建回归 | passed | msix-arm64.log，0 警告/错误；未安装 MSIX |
| 应用测试（含 Windows Shell 快捷方式集成） | passed | tests/all-tests.trx：727 通过、6 项外部服务测试跳过；首次运行因旧 testhost 文件锁失败，锁释放后完整重跑通过 |
| 原生注册表与设置清理测试 | passed | native-tests/results.json：48 项；含嵌套备份同名配置、原字节保留及硬链接拒绝 |
| EXE-only 发布策略 | passed | policy-final.log：26 项，使用模拟远程服务，未发布 GitHub Release |
| PRI 缺失资源负向门禁 | passed | resource-negative.json |
| 默认卸载保留设置 | passed | bundle-default/results.json，独立 Setup 安装/维护/卸载，设置和备份字节不变 |
| 显式清理设置且保留备份 | passed | bundle-clear/results.json：10 项；静默 Setup 实际卸载，配置消失，备份/历史/其他文件字节不变 |
| 安装快捷方式使用 EXE 图标 | passed | bundle-clear/results.json：installed-shortcut-stable-icon |
| 同包维护、同版本异包拒绝、唯一登记、安装位置 | passed | 上述两个 Bundle 场景结果 |
| 显式清理选项不影响维修 | passed | repair-result.json、repair.log |
| 中英文维护页显示、默认未勾选及选中后取消 | passed | maintenance-zh.png、maintenance-en.png、maintenance-*-tree.json；英文选中后取消，未触发卸载 |
| x64 新应用实际运行与窗口属性 | passed | runtime.png、runtime-tree.json、window-identity.json；独立数据根启动，窗口图标句柄非零，四项身份属性与 EXE 一致 |
| 完整 UI 卸载无系统权限错误 | blocked | ui-uninstall-environment-error.png、ui-clear_000_FolderRewindMsi.log：本机 Installer 对 D:\Config.Msi 中回滚文件设置权限返回错误 5/1926；不以静默退出 0 掩盖此问题 |
| 完整 UI Apply 未成功时保留设置 | passed | ui-clear-result.json：已勾选后仍保留配置及备份，Apply 返回 0x800700e8，未执行清理；MSI 产品已移除，不能将其等同于整个 Bundle 成功 |
| 干净 VM 升级、取消、UAC、另一管理员、机器范围清理 | not-run | 仍需专用环境验证，不触碰正式安装 |
| ARM64 真实运行、用户既有任务栏缓存的升级体验 | not-run | 本轮没有 ARM64 设备；合成固定项和窗口属性检查不等于所有 Explorer 缓存状态均已复现 |

代码审阅已按 winui-code-review 检查本次 Shell 互操作、平台标注、COM 释放和清理边界；无新增应用 XAML。修复了测试工程链接 Windows helper 的平台分析警告，测试数据根绕过运行中实例检查仅适用于经过验证的独立测试上下文。

## 候选包与发行状态

`artifacts/taskbar-settings-193/public/` 仅包含 x64 / ARM64 的 1.9.3.0 Setup EXE 和各自 .sha256。内部 MSI、测试包、截图及日志均在独立证据目录。`evidence/manifest.json` 记录包身份、哈希、签名状态和构建时源码快照；本轮为未签名诊断候选包。

`evidence/acceptance.json` 保持严格门禁：上述隔离测试结果不能自动替代对生产候选包确切哈希的所有必测验证。本轮未生产签名，未发布远程 Release。

完整安装可靠性仍未达到发行就绪。此前 1.9.2.0 报告中的 Installer 注册回滚失败继续保留，见 [历史报告](exe-repair-1.9.2-acceptance-2026-10-02.md)。本轮新增的完整 UI 卸载权限错误也必须在干净环境复核，未改系统 ACL 或手工修复 Installer 注册表。

# Plugin System v3 — M5 人工测试清单

状态：M6 clean break 已落地，Revision 16 硬化自动门已通过；M5R 发布 Gate 继续拒绝，本清单真实联调全部通过前不得发布。

## M5R 聚焦复测（更新于 2026-08-17）

- 先以 `EnabledIntent=true` 且 code 已卸载的状态手动安装 MineRewind v3：完成提示和列表都必须为 Disabled；不得在安装过程中 Activate。
- 使用会在构造、module initializer 或 `ActivateAsync` 写 marker 的测试包重复首次安装和 Disabled update：所有 marker 均不得出现；只有显式 Enable 或 Active replace 才允许执行候选代码。
- 显式启用后重启：Runtime 必须为 Active，日志不得出现 `0x8001010E`、`KeyboardAccelerator` 跨线程异常或“Plugin v3 initialization failed”；默认 hotkeys 应可见并可触发。
- 重启后打开插件页并停留至少 30 秒：MineRewind 开关必须稳定保持 Enabled；不得闪烁、重复刷新或反复弹出 `runtime.already_active`，且只能存在一个 runtime session。
- MineRewind 只要已安装（分别验证 Disabled 与 Active），新建配置和配置设置的类型列表均应显示随当前 Host 语言变化的“Minecraft Saves”/“Minecraft 存档”；创建后配置文件必须保存 `com.folderrewind.minerewind/minecraft-saves`，而不是把本地化显示文本当作身份。
- 插件列表中的 MineRewind 描述应随中英文切换；点击设置应显示三个来自静态 Schema 的本地化布尔项。Disabled/Safe Mode 保存不得加载插件，Active 保存应事务化 Replace；重启后设置保持且失败时旧 session/settings 不变。
- `Delete data...` 按钮、危险删除预览/确认/结果和 Minecraft 入门预设的进度、步骤结果、外部下载/启动确认必须完整本地化；中文界面不得出现 raw action ID 或本轮已知英文硬编码。
- 分别以 `EnabledIntent=true/false` 放入真实 1.8.2 flat payload 后启动：主页、导航、键盘与 `Alt+F4` 在迁移全程必须响应。
- 两种 intent 均应产生 `install-state.v1.json`、`versions/1.9.0`、`legacy-quarantine` 和 `plugins/.migration/com.folderrewind.minerewind.v2-v3.json`；journal 最终为 `Completed`，日志应包含每个迁移 phase。
- 制造 hash/文件占用或取消失败：journal 必须为 `RecoveryRequired` 并带诊断；flat DLL 不执行，部分 quarantine move 要么完整回滚，要么保留可恢复 quarantine，不能静默丢失。
- 在一个由 Minecraft 占用（`session.lock` 被持有）的已配置世界中，依次从真实模组发送 `BACKUP`、`LIST_BACKUPS`、`RESTORE` 且仅带 `current_save=true`（以及既有 `from/request_id`）：三者不得再返回“缺少 config_id”；列表应返回该世界的 History 文件名，备份/还原应立即回复已受理。
- `BACKUP + current_save=true` 同时发送 URL 编码的非 ASCII `comment`，例如“3.0插件修复后测试”；新 History 必须逐字保存并显示该注释。未发送 `comment` 时仍保存空注释，不能复用上一次值。
- 检查热备日志/行为顺序：`handshake → HANDSHAKE_RESPONSE → handshake_ack → pre_hot_backup → WORLD_SAVED → Host backup`。模组不兼容或保存超时时，Preferred Full backup 应 raw fallback 并持久化 warning；Require consistency 应 Block。
- 检查活跃世界热还原顺序：`handshake → pre_hot_restore → WORLD_SAVE_AND_EXIT_COMPLETE → 文件释放 → Host mutation once-only → restore_finished → 至少 3 秒稳定窗口 → rejoin_world → REJOIN_RESULT → hot_restore_complete`。模组必须实际自动重进；日志必须能观察 `restore_finished`、稳定窗口、`rejoin_world` 和最终状态。握手、退出或文件释放失败时不得进入 mutation，并应发送 `restore_cancelled`；完整 History 使用 Clean，Partial History 强制 Overwrite。
- 本轮 Official Catalog 按用户授权跳过；上述场景和三套测试基线通过后，再由用户决定 M5 Gate。

## A. 包安装与静态安全

- 从“插件商店”刷新 Official Catalog；断网后再次进入，确认已安装插件正常、目录显示离线缓存或明确诊断。
- 通过 Manual Install 选择 `MineRewind-1.9.0.frplugin`，确认安装路径为 `plugins/com.folderrewind.minerewind/versions/1.9.0`，且默认 Disabled。
- 确认由 MineRewind commit `5b237ff` 生成的候选包 SHA-256 为 `48eb2ab4e70cbb10c92f81d036540caaa35ca42dc294e014482c919fa3852d84`，包内包含 `fNbt.dll` 且不含 `FolderRewind.Plugin.Abstractions.dll`。
- 尝试包含 `../`、大小写重复路径、Abstractions DLL 或高压缩 bomb 的测试包：均应在写入插件版本目录前拒绝。
- 安装 Manual provenance 的包后模拟 Official Catalog 后台更新：必须拒绝覆盖。

## B. 启停、更新与卸载

- 启用 MineRewind，确认 Runtime State 为 Active；有进行中 backup lease 时禁用，应进入 Draining，可取消并保持原 session。
- 候选包 Activate/migrate 失败时，确认 current pointer、settings/state 和运行 session 保持 previous known-good。
- 用测试插件让 `DeactivateAsync` 永久挂起并忽略 cancellation：约 5 秒后 Disable/Replace 必须返回，其他插件 transition 仍可继续；旧 capability 已不可路由，Runtime 显示 `Failed + RequiresRestart`。Replace 时新 session 必须保持 Active。
- 安装只声明 `logging` 的测试插件：Config/Backup/Restore/History/Notification/KnotLink/DataStore/TemporaryStorage 调用必须以 `host_service.not_declared` 拒绝，`KnotLink.IsAvailable` 返回 false；声明后的对应 façade 才可用，Activation 阶段 DataStore 仍保持不可用。
- 普通卸载：只删除 code；Enabled Intent、typed settings、provider state、DataStore 和 History/Artifact graph 全部保留。
- “卸载并删除数据”：界面必须列出 settings/state/data 与受影响 History，要求输入精确确认文本；Artifact graph 仍保留且还原 fail-closed。
- 在 destructive uninstall 的 `Prepared`、`Quarantined`、`ConfigCommitted` 后分别终止进程，并制造 Config save failure、占用 quarantine 与最终清理失败：重启必须幂等回滚或完成清理；`RecoveryRequired` 不得显示普通成功，Enabled Intent 与 History/Artifact graph 始终不变。

## C. 旧用户离线升级

- 准备 1.8.x flat MineRewind payload、Enabled 状态、三个 boolean setting、SelectedRegions 和 Minecraft ExtendedProperties。
- 断网启动 1.9.0：必须从随包 `.frplugin` 安装 v3，保持 Enabled Intent 和数据；旧代码移入 `legacy-quarantine`，不执行、不删除。
- 新用户首次启动：MineRewind 不自动安装/启用；只有选择 Minecraft Enhanced Experience Preset 才安装并显式启用。

## D. Minecraft parity 与非对称降级

- Discovery 只产生候选；`AutoCreateConfigs` 关闭时不落盘，开启后由 Host 校验并原子提交。
- Full/manual/automatic：KnotLink 不可用时 raw backup 成功并持久化 `SuccessWithWarnings`，自动化按成功计数且不重试。
- Selected Regions 或 `Require consistency`：provider/consistency 不可用时 Block。
- Restore：owner Disabled/Failed/missing 一律 Block；活跃世界的 KnotLink/兼容模组不可用时 Block，冷世界仍走 Host 安全还原而不要求无意义的退出握手。
- Hot restore：Save & Exit → Host `BackupBeforeRestore` → Safe Restore/mutation once-only → PreservePlayerData → Rejoin。安全备份失败/取消时不得进入 mutation。
- 删除中间 semantic Artifact History 时，确认 Host 按 graph retention/GC 处理，不能形成断链；Cloud queue 只观察 committed graph root。用 rclone 在第二个空环境下载 semantic History，确认先校验 manifest root/revision 和 reachable hashes，再允许 Save & Exit。
- 命令 ID 验证：`com.folderrewind.minerewind/hotbackup.active-world` 与 `.../hotrestore.active-world`；默认 `Alt+Ctrl+S` / `Alt+Ctrl+Z`，修改用户 override 后重启仍保持 override。
- 用含 legacy `level.dat/Data/Player` 的世界验证 world name/mode/seed/time/player-data metadata 与 PreservePlayerData；如有 26.1+ fixture，再验证 `singleplayer_uuid` + `players/data/<uuid>.dat`。

## E. Preset 与外部安装器

- Minecraft Enhanced Experience 每步展示独立结果，半成功不得显示整体成功。
- 当前 KnotLink 外部 installer action 应显示“等待固定官方 URL/SHA-256”warning，不下载、不启动。
- 补齐固定 URL/SHA 后：下载前确认一次；SHA 校验成功后、启动前再确认一次；任一拒绝均为 Blocked step。

## F. 通过标准

- A–E 全部符合；无数据丢失、无静默 fallback、无绕过 Safe Restore。
- Host/MineRewind/runtime tests 全绿；Host x86/x64/ARM64 Release、MineRewind、`.frplugin`、Catalog、Site gates 全绿。
- 记录实际安装路径、配置备份路径、失败更新 journal 和一份完整 backup/restore 日志，作为 M5 审阅证据。

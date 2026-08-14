# Plugin System v3 — M5 人工测试清单

状态：首轮 M5 Gate 已拒绝；等待 M5R 聚焦复测。不要在通过本清单前进入 M6 clean break。

## M5R 聚焦复测（2026-08-14）

- 先以 `EnabledIntent=true` 且 code 已卸载的状态手动安装 MineRewind v3：完成提示和列表都必须为 Disabled；不得在安装过程中 Activate。
- 显式启用后重启：Runtime 必须为 Active，日志不得出现 `0x8001010E`、`KeyboardAccelerator` 跨线程异常或“Plugin v3 initialization failed”；默认 hotkeys 应可见并可触发。
- 分别以 `EnabledIntent=true/false` 放入真实 1.8.2 flat payload 后启动：主页、导航、键盘与 `Alt+F4` 在迁移全程必须响应。
- 两种 intent 均应产生 `install-state.v1.json`、`versions/1.9.0`、`legacy-quarantine` 和 `plugins/.migration/com.folderrewind.minerewind.v2-v3.json`；journal 最终为 `Completed`，日志应包含每个迁移 phase。
- 制造 hash/文件占用或取消失败：journal 必须为 `RecoveryRequired` 并带诊断；flat DLL 不执行，部分 quarantine move 要么完整回滚，要么保留可恢复 quarantine，不能静默丢失。
- 本轮 Official Catalog 按用户授权跳过；上述场景和三套测试基线通过后，再由用户决定 M5 Gate。

## A. 包安装与静态安全

- 从“插件商店”刷新 Official Catalog；断网后再次进入，确认已安装插件正常、目录显示离线缓存或明确诊断。
- 通过 Manual Install 选择 `MineRewind-1.9.0.frplugin`，确认安装路径为 `plugins/com.folderrewind.minerewind/versions/1.9.0`，且默认 Disabled。
- 确认包 SHA-256 为 `b0ed525bcf49dc22a7ee0ee04c07eda6242fe4efa52368758fc9ac406fc76b63`，包内包含 `fNbt.dll` 且不含 `FolderRewind.Plugin.Abstractions.dll`。
- 尝试包含 `../`、大小写重复路径、Abstractions DLL 或高压缩 bomb 的测试包：均应在写入插件版本目录前拒绝。
- 安装 Manual provenance 的包后模拟 Official Catalog 后台更新：必须拒绝覆盖。

## B. 启停、更新与卸载

- 启用 MineRewind，确认 Runtime State 为 Active；有进行中 backup lease 时禁用，应进入 Draining，可取消并保持原 session。
- 候选包 Activate/migrate 失败时，确认 current pointer、settings/state 和运行 session 保持 previous known-good。
- 普通卸载：只删除 code；Enabled Intent、typed settings、provider state、DataStore 和 History/Artifact graph 全部保留。
- “卸载并删除数据”：界面必须列出 settings/state/data 与受影响 History，要求输入精确确认文本；Artifact graph 仍保留且还原 fail-closed。

## C. 旧用户离线升级

- 准备 1.8.x flat MineRewind payload、Enabled 状态、三个 boolean setting、SelectedRegions 和 Minecraft ExtendedProperties。
- 断网启动 1.9.0：必须从随包 `.frplugin` 安装 v3，保持 Enabled Intent 和数据；旧代码移入 `legacy-quarantine`，不执行、不删除。
- 新用户首次启动：MineRewind 不自动安装/启用；只有选择 Minecraft Enhanced Experience Preset 才安装并显式启用。

## D. Minecraft parity 与非对称降级

- Discovery 只产生候选；`AutoCreateConfigs` 关闭时不落盘，开启后由 Host 校验并原子提交。
- Full/manual/automatic：KnotLink 不可用时 raw backup 成功并持久化 `SuccessWithWarnings`，自动化按成功计数且不重试。
- Selected Regions 或 `Require consistency`：provider/consistency 不可用时 Block。
- Restore：owner Disabled/Failed/missing 一律 Block；KnotLink 不可用一律 Block。
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

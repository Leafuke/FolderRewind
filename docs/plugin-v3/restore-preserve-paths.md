# 单次完整当前状态保留

RESTORE 的 `restore_preserve_paths` 接受逗号分隔相对路径，整个值使用 KnotLink 编码。末尾 `/` 选择完整目录，否则选择精确文件；路径不能包含逗号、通配符、正则、绝对根或父目录跳转。最多 16 个选择，重叠子树归并。

Minecraft（com.folderrewind.minerewind）在锁定的管理源内定位唯一 level.dat，路径以该世界为根；存在多个世界或边界无法包含所选数据则拒绝。其他配置以管理源为根。选定目录当前缺失表示当前空状态，会删除历史同目录文件。

Host 先冻结并验证所有复制内容与删除列表，使用现有 4096 文件 / 64 MiB 准备限额，再仅修改暂存区。插件玩家字段处理后执行强保留，所以显式完整文件保留优先。沿用 Native Restore 提交、回滚、Derived Workspace 和配置守卫。clean 与 overwrite 均从当前目录制作回滚副本，随后应用已准备目录；当前不存在的历史文件不会从当前回滚副本复活。

旧 restore_whitelist 仍仅保留归档缺少的当前文件；Checkout/Merge 不启用强保留。FTB 需同时选择 ftbquests/、ftbteams/，这保留所有当前团队及领奖记录，任务定义和奖励的其他世界副作用不在本契约内。

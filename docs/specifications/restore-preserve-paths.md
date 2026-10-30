# 单次完整当前状态保留

RESTORE 的 `restore_preserve_paths` 接受逗号分隔相对路径，每项单独使用 KnotLink RFC 3986 编码，以原始逗号连接。例如 `ftbquests/,ftbteams/` 发为 `restore_preserve_paths=ftbquests%2F,ftbteams%2F`。末尾 `/` 选择完整目录，否则选择精确文件；路径不能包含逗号、通配符、正则、绝对根或父目录跳转。最多 16 个选择，重叠子树归并。

仅此参数兼容旧发送端的整值编码：原始值不含逗号时，解码一次后按逗号拆分；原始值含逗号时使用标准逐项解码，不兼容混合格式。因路径自身禁止逗号，该兼容不会改变任何合法强保留路径的含义。字面 `%2C` 必须编码为 `%252C`，不会被二次解码。所有结果继续进入既有路径校验。普通 GetList/DecodeList、白名单和黑名单行为不变，`a%2Cb` 仍表示一个包含逗号的列表项。重复键仍拒绝，不能发送多条 `restore_preserve_paths=`。

Minecraft（com.folderrewind.minerewind）在锁定的管理源内定位唯一 level.dat，路径以该世界为根；存在多个世界或边界无法包含所选数据则拒绝。其他配置以管理源为根。选定目录当前缺失表示当前空状态，会删除历史同目录文件。

Host 先冻结并验证所有复制内容与删除列表，使用现有 4096 文件 / 64 MiB 准备限额，再仅修改暂存区。插件玩家字段处理后执行强保留，所以显式完整文件保留优先。沿用 Native Restore 提交、回滚、Derived Workspace 和配置守卫。clean 与 overwrite 均从当前目录制作回滚副本，随后应用已准备目录；当前不存在的历史文件不会从当前回滚副本复活。

旧 restore_whitelist 仍仅保留归档缺少的当前文件；Checkout/Merge 不启用强保留。FTB 需同时选择 ftbquests/、ftbteams/，这保留所有当前团队及领奖记录，任务定义和奖励的其他世界副作用不在本契约内。

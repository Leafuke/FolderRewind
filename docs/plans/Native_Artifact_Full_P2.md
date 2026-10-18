# P2：Full 语义表示与 Native History 开发验收闭环

基线 Host a320b2b、MineRewind e7d780e。两仓库分支 codex/native-artifact-full。
用户 AGENTS.md 修改不纳入实现提交。只做本地提交，不推送、发布或替换安装态插件。

交付链：CoreFull → Runtime Transformer → Host封存/逐字节验证 → 同一Version双表示
→ 重启/指定插件表示恢复 → Retention/本地remote/加密。
默认用户入口关闭；Core对照保留；不实现delta、Capture API或语义Merge。

公共Plugin API保持3.9。MineRewind新增 minecraft-world-full/v1、
minecraft-world-materializer；独立维护格式，P1 scratch不变成公开格式。
未知/零字节region可按字节Exact保存，但不得宣称可语义比较。

Gate：实际插件包经Runtime调用、联合持久提交/故障恢复、Restore/Checkout/SafetySnapshot/
通用Merge输入、配对保留/闭包GC、真实7z加密及rclone本地remote、真实世界字节一致。
只有全部有实际证据才宣布P2完成；局部实现不取消开发限制或宣称全闭环。

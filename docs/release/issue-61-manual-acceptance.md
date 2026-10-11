# Issue #61 人工验收记录

日期：2026-10-11（Asia/Shanghai）。代码基线：`1.9.x`，`dd29fe0` 加本次工作区修改。

本次实现统一 rclone 连接与任务快照目录，后台迁移 1.9.6 自动创建的凭据，并补充可区分、可导出的诊断。不迁移 1.8 云存储，不调整 OpenList 状态或自动启动，不新增或改写自动化测试。**2026-10-11 已完成人工实机验收的核心路径；完整验收仍未通过，限制与未执行项目见下表。**

## 构建环境和候选文件

- Windows App SDK：2.5.1；项目已有 WinUIAnalyzer 0.7.0，保留现有依赖策略。
- 解决方案检查：`dotnet build .\FolderRewind.slnx -c Debug -p:Platform=x64 -p:GenerateAppxPackageOnBuild=false --no-restore`。
- MSIX：`artifacts/issue61/FolderRewind_1.9.6.0_x64_issue61_unsigned.msix`；打包日志：`artifacts-issue61-msix-build.log`。
- MSI：`artifacts/issue61/msi/FolderRewind_1.9.6.0_x64.msi`；日志：`artifacts-issue61-msi-publish.log`、`artifacts-issue61-msi-package.log`。
- 最终解决方案编译通过：0 警告、0 错误。只编译了现有测试工程，没有运行自动化测试，也没有修改测试文件。
- MSIX 打包成功；核对包内身份为 `Leafuke.FolderRewind`、版本 `1.9.6.0`、架构 `x64`，含实际应用程序集，无签名。
- MSI 自包含发布通过 `/warnaserror`；中英文安装数据库构建均为 0 警告、0 错误，已嵌入中文语言变换。既有打包流程的 21 项 PRI 文件引用及自包含资源校验、最终 MSI ICE 校验通过。
- MSIX SHA-256：`A6A1D13169D53ACD3694C8637172ECD93177519DF70DC245479A05CC865A18EF`。
- MSI SHA-256：`8C526AB47D6C57FC6ADFAA17161E87BDBBC494266179D471DECEE102D95B5CCF`。

候选包沿用 `1.9.6.0`，不是正式发布包。MSIX 原件未签名。实机验收使用设备上已存在、已受信任且 Publisher 匹配的证书（thumbprint `883C19BCC7CF69295C8CC4081629EC13FF18934A`）签署临时副本；未生成或导入证书，也未修改信任存储。原始候选 MSIX 哈希保持不变。临时签名包以 `Leafuke.FolderRewind_1.9.6.0_x64__xh6qzj9v1wvnm` 身份安装，运行时 `IsDevelopmentMode=False`。MSI 候选也实际安装并完成回归，验收结束后已卸载 MSI，保留签名 MSIX 候选。正式发布按现有流程递增版本。

## 行为与诊断

MSIX 的新根目录为 `%LOCALAPPDATA%/Packages/<PackageFamilyName>/LocalState/FolderRewind/credentials`；MSI 为 `%LOCALAPPDATA%/FolderRewind/credentials`。连接保存在 `rclone/connection-GUID/rclone.conf`，任务快照保存在 `rclone-tasks/task-GUID/rclone.conf`。

后台迁移只识别当前项目引用的旧普通 LocalAppData 根目录下 `rclone/connection-<32位GUID>/rclone.conf`。先校验字节、SHA-256 和所选远端，再通过现有配置事务保存新路径。重解析点与内容不同的目标文件会阻止迁移。旧凭据始终保留；现存目标内容相同时可复用，并限制为当前用户访问。MSI 新旧根目录相同，不发生复制。

主窗口激活后才开始读取与复制，最多并行处理两份凭据。使用同一份凭据的连接共享等待结果，新旧路径都等待持久化确认。源文件和目标在事务提交期间保持只读共享句柄。退出先收束迁移，再封闭配置写入队列。

普通迁移失败时继续保留旧路径；任务仍在新根目录创建快照。保存结果不确定时，相关云操作被拒绝，用户可在向导中重新检查、选择并保存连接；成功显式保存会解除这一迁移状态。所有后台失败均记录脱敏日志。

| 诊断代码 | 含义 |
| --- | --- |
| `cloud.source-config-unavailable` | 源配置缺失、不可读或超过大小限制 |
| `cloud.remote-mismatch` | 所选远端不在配置中 |
| `cloud.snapshot-create-failed` | 任务目录、锁或配置快照创建失败 |
| `cloud.snapshot-unavailable` | rclone 明确报告找不到本次 `--config` 所指的快照 |
| `cloud.credentials-migration-failed` | 后台迁移或保存失败，原文件保留 |
| `cloud.credentials-target-conflict` | 新位置存在不同内容，未覆盖 |
| `cloud.credentials-save-uncertain` | 保存和补偿写入均未确认，需要重新保存连接 |
| `cloud.connection-save-failed` | 向导保存未通过 |

诊断列表提供中英文组件名称及固定修复导航；导出沿用既有白名单，只包含代码、状态、检查时间和修复目标。没有明确快照缺失证据的 rclone 报错仍保留原始脱敏诊断，不推断为 MSIX 虚拟化。

## 人工操作清单

使用独立验收数据和 WebDAV 目录。记录操作系统版本、发行形态、包身份、rclone 版本、验收日期与结果。凭据正文、密码和令牌不得放入本文件或截图。

| 场景 | 操作与通过标准 | 实机结果 |
| --- | --- | --- |
| 1.9.6 基线跨进程读取 | 在实际 MSIX 1.9.6 中创建连接，暂停在快照写入后、rclone 启动前；核对应用视图存在快照，再用同用户的独立终端执行下方命令。记录远端是否出现及脱敏错误；不能仅凭源码将根因标为已确认。 | **未执行。** 验收机最初只有开发注册 MSIX 与 MSI 1.9.6.0，没有独立的旧版签名 MSIX 基线包；未据此推断旧版行为或根因。 |
| 修复版本跨进程读取 | 在同样条件下检查新路径，独立 rclone 返回预期远端。恢复应用后验证浏览、读写和上传。 | **核心路径通过；断点时序部分验证（候选 MSIX 与 MSI）。** 两种发行形态的 UI 读写验证均通过；并行文件观察器捕获了 `credentials/rclone-tasks/task-…/rclone.conf`，独立 rclone `listremotes` 退出码 0 并返回预期 WebDAV remote；任务目录在操作结束后无残留。未设置源码断点，因此未验证文档所述“应用 rclone 启动前暂停”的精确时序。MSIX 和 MSI 的浏览、读写、上传均成功。 |
| 新建 MSIX 连接 | 自动生成的凭据位于 LocalState；浏览并选择目录、验证读写、保存、重启，再上传一个版本。确认源凭据保留，任务结束清理自己的快照。 | **通过。** 候选 MSIX 新建的连接文件位于 `Packages/<PFN>/LocalState/FolderRewind/credentials/rclone/connection-…/rclone.conf`；独立 rclone 可读取。浏览、写入验证、保存、退出并重启后路径持久；上传 `local-source` 成功。远端 `payload` 与 185 字节本地归档 SHA-256 均为 `BFF5B0352534837D601FCD9A8200F6F9FEB22336CE0965476F76F47B73F93A40`，写入探针已清理。确认框截图：[MSIX 上传确认](issue-61-evidence/msix-upload-confirm.png)。 |
| 从 1.9.6 升级 | 保留同一包身份、同一用户的配置及旧凭据部署候选版本，不通过卸载清空数据。窗口可立即使用；后台结束后路径已可靠保存。多个项目共享旧文件时一致迁移，文件字节和 SHA-256 一致，旧文件仍存在。 | **凭据迁移机制通过，完整升级场景部分验证。** 隔离测试项目引用旧路径 `%LOCALAPPDATA%/FolderRewind/credentials/rclone/connection-<GUID>/rclone.conf`；候选 MSIX 启动后自动将其迁移到 LocalState，新旧文件 SHA-256 一致，旧源仍存在，项目路径已持久更新，迁移后浏览和读写通过。没有实际旧版签名 MSIX 包，故未验证真实 1.9.6 MSIX 就地升级、同包身份升级或多项目共享引用。 |
| 源缺失或远端不匹配 | 使用验收项目删除源配置或选择不存在的远端。向导展示对应代码，检查失败后仍能重新选择文件、远端并保存。 | 未执行。 |
| 目标冲突与重解析点 | 在新位置准备不同内容文件，或对验收路径设置重解析点。迁移停止，不覆盖目标、不删除源文件；提示可查看。另验内容相同目标的复用和私有 ACL。 | **部分通过。** 检查了迁移后目标文件 ACL：访问规则仅列出当前用户 FullControl；未执行内容冲突、重解析点及同内容复用场景。 |
| 复制或保存失败 | 用验收目录权限、磁盘条件或占用配置文件产生失败。保存失败恢复旧路径；再次重启可重试。源配置不会丢失，其他项目和主窗口仍可用。 | 未执行。 |
| 补偿写入不确定 | 保持配置文件不可写，使保存与补偿均失败。新旧凭据保留，相关云操作被拒绝；解除占用后在向导重新确认并保存，云操作恢复。 | 未执行。 |
| 迁移期间编辑项目 | 暂停后台复制，修改项目连接路径或远端、删除或替换项目，再恢复迁移。不覆盖新的用户选择；未变化的共享引用正常保存。 | 未执行。 |
| 立即操作与退出 | 启动后立即进入向导或触发云操作，仅等待相关凭据，UI 不冻结。迁移提交期间新路径也等待同一结果。关闭页面可取消等待；迁移期间退出后不再产生配置写入或启动新的云任务。 | 未执行迁移竞争与取消压力场景；正常关闭并重启 MSIX、MSI 实例已验证。 |
| 外部配置和任务隔离 | 使用用户自选外部配置，它不迁移、不删除、不写回；任务仍读取快照。排队或运行期间改动源文件，下一次命令被拒绝；多个任务目录互不清理。 | 未执行。 |
| MSI 回归 | 实际 MSI 安装环境中连接仍位于原 LocalAppData 目录；新建、保存、重启、浏览、读写、上传正常，没有重复迁移。 | **通过。** 实际安装候选 MSI 1.9.6.0 并从 MSI 进程运行；隔离项目新建连接保存到 `%LOCALAPPDATA%/FolderRewind/credentials/rclone/connection-…/rclone.conf`。首次写入验证期间，独立 rclone 成功读取任务快照；退出并重启后 UI 路径持久，重新浏览/读写、生成本地版本和上传均成功。远端 payload 与 MSI 本地归档 SHA-256 相同；原有 6 个 MSI 项目未变。验收后 MSI 产品代码与可执行文件已卸载，保留 MSIX 候选；原 Burn bundle 的卸载项仍残留。确认框截图：[MSI 上传确认](issue-61-evidence/msi-upload-confirm.png)。 |
| 中英文诊断 | 切换语言检查上述代码的消息、组件名和修复入口；导出包含实际代码而非 `unknown`，不包含凭据或原始服务输出。 | 未执行。 |

快照需要保持存活，断点宜设在 `RcloneExecutionContext` 写入 `SnapshotPath` 后，尚未释放上下文时。独立 PowerShell 终端使用应用实际选择的 rclone 可执行文件：

```powershell
& '<实际 rclone.exe 绝对路径>' --config '<本次 SnapshotPath 绝对路径>' listremotes
```

记录退出码和预期远端是否出现，勿使用 `config show` 导出敏感正文。恢复断点后确认快照按现有规则清理。GUID 目录每次变化属于正常行为。
# 1.9.x 云重构验收记录（2026-10-12）

实施基线：`1.9.x` / `13962afeeae864c7bfc7ac04797f6d1984e48948`。兼容标签：`v1.9.6` / `23c194710527d56f4d9d88e708df4b0d0cfe2ba6`。按用户要求，**全流程 UI 验收交给人工，没有运行 winapp UI 自动验收**。没有推送或发布。

## 已执行验证

- .NET SDK 10.0.401；完整 Debug x64 solution build：0 errors。最终定向构建结果见下述命令和本地日志。
- Host Release 全量测试：1011 passed，0 failed，0 skipped；最后补充 Manifest/Fact 间隙用例后，相关云上传、真实 rclone 恢复及本地化定向测试 18 passed；补充未归属快照删除守卫后，凭据及本地化定向测试 12 passed。去重合计覆盖 1013 个 Host 用例。
- Plugin.Abstractions：15 passed；Plugin.Runtime：146 passed，均无失败/跳过。
- 使用实际 `7za.exe` 和 rclone 本地后端运行普通/加密增量闭包上传、新空仓库只读拉取、更换 remote 名称、下载校验与文件内容恢复；3500 Pack 枚举通过。这里的“新设备”是隔离的空仓库目录，**不是第二台实体电脑或真实云服务**。
- 受控测试 Host 在 Prepared 落盘、History durable/激活前被真实终止；重启重建保护，分别得到 Aborted/Released 和 Pending/Held。OAuth 快照写新 Token 后真实终止 Host，存活子进程阻断回收；准确终止该子进程后按原修订成功回收。
- WinUI 编译及现有 WinUIAnalyzer 检查通过；检查了新增 x:Bind 通知、虚拟化列表、AutomationId、主题资源与本地化。构建若出现 MSTEST0037，是现有 `LocalizationQualityTests` 的断言风格警告，不是新增 UI/API 警告。
- `Fixtures/Legacy196` 是按发布标签结构构造的固定脱敏样本，包含 schema 1 配置、无效占位凭据、format 2 descriptor、format 1 全量/增量 Pack；身份与原字节读取/导入测试通过。它**不是用户 v1.9.6 实机导出**，不能代替真实升级。已有 Legacy182/迁移测试继续通过。
- 本轮没有修改用户运行中应用的配置、凭据或历史数据；测试使用隔离临时目录。

复现命令（仓库根目录）：

```powershell
dotnet restore .\FolderRewind.slnx -p:Platform=x64
dotnet build .\FolderRewind.slnx -c Debug -p:Platform=x64 -p:GenerateAppxPackageOnBuild=false --no-restore
$env:FOLDERREWIND_TEST_7Z = 'D:\Programs\FolderRewind\7za.exe'
$env:FOLDERREWIND_TEST_RCLONE = 'D:\Programs\Software-Test\rclone\rclone.exe'
dotnet test .\FolderRewind.Tests\FolderRewind.Tests.csproj -c Release
dotnet test .\FolderRewind.Plugin.Abstractions.Tests\FolderRewind.Plugin.Abstractions.Tests.csproj -c Release
dotnet test .\FolderRewind.Plugin.Runtime.Tests\FolderRewind.Plugin.Runtime.Tests.csproj -c Release
```

本地证据：根目录 `cloud-refactor-build.log`、`cloud-refactor-app-final-build.log`、`cloud-refactor-tests.log`、`cloud-refactor-final-targeted-tests.log`、`cloud-refactor-final-credentials-tests.log`、`cloud-refactor-abstractions-tests.log`、`cloud-refactor-runtime-tests.log`；各测试工程 `TestResults/cloud-refactor-*.trx`。日志/测试产物被忽略，不作为提交文件。本文及用户指引是明确 force-add 的单个文档，没有放开整个忽略目录。

## A01–A32

“自动化通过”只涵盖该行所述测试层；“部分”表示仍缺指定的实机场景，不构成阶段发布放行。

| 编号 | 结果与证据 | 人工补验 |
|---|---|---|
| A01 | 服务自动化通过：闭包、Manifest、载荷及 metadata 全成功才 Complete | 真项目自动开关到云终态 |
| A02 | 自动化/实际本地传输通过：基础+增量闭包，失败不 Complete，新空仓库恢复内容 | 真云断网基础失败后连续备份 |
| A03 | 部分：上传/校验/metadata 取消重试，Journal 持久回放 | 真实 rclone 上传 payload 后强杀整个应用 |
| A04 | 自动化通过：Manifest 落盘后、Fact 之前取消，重试使用原稳定 ReplicaId | 可选实机断电复核 |
| A05 | 自动化通过：metadata 失败不报成功，重试不重复创建已验证副本，取消后下一任务可完成 | 慢云/卡网与任务中心反馈 |
| A06 | 自动化通过：Held 根及计划指纹、损坏/缺失 Journal 阻断清理；计划后新增任务保留真实归档 | UI 手动删除与自动保留竞争 |
| A07 | 实际 rclone 本地后端 3500 Pack 枚举通过；流式容量达到边界明确失败 | 真大云仓库拉取 |
| A08 | 部分：保留完整下载 SHA256，损坏上传拒绝成功 | 限制哈希能力的 WebDAV 与损坏下载 |
| A09 | 模型通过：共享独立项目根、专属所有者与 GUID 拼写；连接管理已实现 | 两项目共享编辑、删除引用提示 |
| A10 | 受控进程通过：新 Token 写回、其他 section 保留、下一调用用新值 | 真 OAuth 轮换账户 |
| A11 | 未执行 MSIX 实包；已复核基线统一目录与显式 --config 路径 | 商店/签名 MSIX：创建、重启、导入、读写 |
| A12 | 受控进程通过：外部 endpoint 不授予停止权 | 外部 OpenList + 实际 WebDAV |
| A13 | 部分：并发只启动一次、租约释放不停止、未授权不启动 | 真 OpenList、游戏连续备份、FR 退出后存活 |
| A14 | 部分：实际 local 后端 old_name→new_name，新空仓库内容恢复；ConfigId 不改 | 两环境 JSON 导入→重绑→旧版本恢复 |
| A15 | 服务测试通过 descriptor 身份阻断；项目重绑先核对实际 descriptor | UI 选择错误仓库、核对云端未变 |
| A16 | 服务通过：Pull 不创建 descriptor、不上传本地独有 Pack、不改 workspace | 真只读账户远端目录快照比较 |
| A17 | 自动化通过缺失 Manifest/载荷、哈希损坏不 Complete；恢复真实读取 Manifest | UI 缺失/损坏 payload 恢复失败文案 |
| A18 | 代码及编译通过：选仓库 setter、代次及确认证据失效 | 分析 A 后切 B、返回重进及恢复确认 |
| A19 | 现有迁移自动化通过独立权限/历史初始化不启任务；导出清除本机绑定 | UI 缺插件/工具/路径时保持暂停 |
| A20 | 部分：固定脱敏 Legacy196 原 Pack 字节及身份兼容；Legacy182 继续通过 | v1.9.6 实机配置/原云仓库升级 |
| A21 | 本地化/脱敏回归通过；审查导出清除 credential/import路径、旧命令、OpenList 授权 | 实际 JSON/诊断导出搜密码和 Token |
| A22 | 未执行托盘/游戏 UI 场景；工作器独立于页面且退出有限 drain | 连续快速备份、窗口隐藏、退出重启 |
| A23 | 真实受控 Host kill 通过 durable→activate 间隙回放及原目标保留 | 可选整应用强杀复核 |
| A24 | 自动化通过多个登记故障点、注意标记也失败、损坏/缺失 Journal；本地备份继续 | UI 诊断可见、显式未知保护处置 |
| A25 | 受控进程通过同存储两上下文串行；GUID/path 模型回归 | 两实际专属档案共用 OAuth 文件 |
| A26 | 真实受控 Host kill 通过新 Token 快照/活子进程/安全回收；冲突留证 | 真实 OAuth 提前轮换及服务端异常 |
| A27 | 部分：冻结修订、可信刷新链、取消保留、目标 guard；共享编辑使用配置门闩 | 排队/运行中改目标、关开关、删项目/档案 |
| A28 | 自动化通过并行 Active/Retired 拒绝复用；共享生命周期投影 | 可选双设备冲突版本预览 |
| A29 | 自动化通过缺失 Manifest/损坏载荷拒绝；实际 local 恢复读真实 Manifest；未知字段阻断 | 真云篡改 Manifest/ArtifactRoot 身份 |
| A30 | 路径模型四种形式及内部拼接通过；歧义输入拒绝 | FTP/SFTP 后端相对与绝对根回归 |
| A31 | 部分：服务只读不写；普通/加密隔离空仓库按需恢复通过 | 只有读权限的真实账户完整向导 |
| A32 | 部分：实际普通/加密增量恢复及缺密码阻断；插件缺 Handler 安全阻断 | 干净电脑、crypt 原材料；兼容插件 Handler 实测 |

## 人工全流程步骤

1. **普通/加密主链路**：隔离测试源建立全量及两个智能增量；在项目云向导验证读写，开启自动上传；查云端完整对象和任务完成状态；断网再备份，确认本地成功、云待传与清理保护，联网重试。手动补传旧版本验证同闭包。
2. **连接管理与变更**：两项目共享同凭据，一个专属；改显示名应保持任务目标，改 endpoint/remote/根目录应暂停且旧任务不改投；关闭自动上传、取消、重试、放弃保护分别检查。删除有引用/待传/刷新证据的连接应被阻断。验证小窗口、深浅主题、键盘与读屏、中英文。
3. **崩溃/任务中心**：在 payload、校验、metadata 阶段分别结束 FR，再启动；检查稳定任务/ReplicaId、无假成功、Held 数据未清理。破坏 Journal 的副本做隔离故障验收，确认本地备份能继续、物理清理暂停。不要在唯一真实数据上做破坏测试。
4. **OpenList**：外部实例可达时检查 Stop 不可用；授权指定环境，连续任务只启动一次；传输期间禁止停止，完成/托盘/退出不停止；重开 FR 不认领旧实例。重新签名授权后验证实际 `/dav` 账户权限。
5. **带 JSON 的换机恢复**：在另一电脑或全新 Windows 用户环境导入脱敏 JSON，保持源和备份暂停；重新认证，以不同 remote 名连接原根；核对 ConfigId，关联原仓库并拉历史，选普通/加密增量恢复到新目录，比对目录内容/哈希；确认路径及插件后分别启用本地备份/上传。错误 ConfigId 应拒绝且不写原云仓库。
6. **应急与只读**：不导入 JSON，仅用只读账户发现仓库；分析 A 后切 B，旧版本/确认必须失效；恢复时比对远端对象清单前后不变。删除或损坏一个载荷/Manifest 的测试副本，必须精确失败而无假恢复成功。
7. **MSIX/旧版**：保存实际 v1.9.6 用户配置、凭据及 History 的安全副本；分别运行 MSIX 和非 MSIX 升级，核对实际统一目录、--config 文件和重启后访问，原 Pack/hash/身份无改变。v1.8.2 只验原迁移提示，不期待旧云格式自动转换。
8. **导出检查**：连续两次云 JSON 导出文件名不同、旧文件保留；导入可选新旧文件。搜索 JSON 与诊断：不得出现密码、Token、.conf 正文、OpenList 启动授权。敏感原材料单独保存，不以截图/日志公开。

每项人工记录安装形式、工具版本、后端/权限、项目 ConfigId、任务 ID、操作时间、预期/实际结果；秘密不要写进证据。MSIX、真实 OAuth、真 OpenList、只读账户、实际升级及双环境恢复均未执行，**P2/P4/P5/P6 的实机发布门槛尚未放行**。

## 维护边界与回滚

新增持久格式仅为本机连接档案、cloud-jobs v1、受保护刷新 attempt v1 与非秘密 revision ledger v1。原 Native Pack/descriptor/Replica 协议不变。回滚二进制不能回滚已经可信刷新的 Token；旧版不理解 Held 保护，未完成云任务时不得用旧版执行保留/删除。先保全载荷及 local-state，完成/处置任务，再考虑二进制回退。

登记证据损坏不自动删除；未知凭据快照不自动隔离/过期清理；所有 Held 任务下物理清理保守暂停。未知后端刷新和 PluginArtifact 缺兼容恢复 Handler 时阻断并显示诊断。这些降级是明确的安全边界，不应在发布说明中描述为全场景已验收。

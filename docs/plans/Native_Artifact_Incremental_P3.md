# P3：Smart 文件变化筛选与语义增量

> 当前交付：用户已批准显式启用的实验性 P3 与手动 checkpoint 入口。原严格性能 Gate 的失败记录保留；本轮不运行 UI 自动测试，人工步骤见验收记录文末。

实施基线：Host 4ee0efd、MineRewind b0812d3。分支 codex/native-artifact-incremental。
用户批准的实施合同来自本次对话；用户 AGENTS.md 改动不纳入提交。只进行本地提交，不发布。

## 不变量

- size-mtime-v1 筛选；变化输入不等于 PartialSource。稳定视图条件不弱于普通 Smart。
- 明确 Workspace BaseVersion/Representation/root；不按时间猜基线。
- 完整状态目录 → recipe/index packs → data packs，最多三层，不依赖上一版本 root。
- 数据包 64MiB、recipe 包 8MiB、residual 64KiB；保留 Exact 所需全部字节。
- unchanged recipe/reference 必须不变；changed 从候选 Artifact 重建并对照捕获字节。
- API 3.10 兼容加法；旧 Full v1 reader 保留；没有 Capture/Merge 新框架。
- 成功只保留 semantic 主表示；Core transport 无历史依赖，journal 管理清理。
- Cloud v2 缺失密文对象传输，descriptor 最后发布；共享 blob 保守保留，明确空间成本。
- Gate 通过前正常入口关闭；保持用户 opt-in，无自动迁移。

## 阶段与状态

当前交付为开发闭环，尚未完成 P3 全部准入。以下阶段不因为已有部分代码而勾选完成；实际证据及缺口见 [验收记录](Native_Artifact_Incremental_P3_Acceptance.md)。格式、API 和生命周期约束见 [合同](../plugin-v3/incremental-artifact-p3.md)。

- [ ] P3.0 合同、格式、测量基线
- [ ] P3.1 变化计划、统计 witness、cache schema 3
- [ ] P3.2 按需 Artifact 与 file-view 验证
- [ ] P3.3 MineRewind recipe/data pack 增量
- [ ] P3.4 单表示提交、transport 清理与 Core 回退
- [ ] P3.5 本地生命周期、加密、Cloud v2
- [ ] P3.6 安全/性能 Gate 与正常用户显式入口

## 原始 Gate（历史要求；本轮修订见下）

至少20次暖缓存、10次可证明冷缓存；P1真实语料与独立>=4GiB语料；30版本含checkpoint/Retention。
无变化不生成transport、不调Transformer、不解码；p95 <= Smart*1.10+250ms。
稀疏变化存储/上传 <= Smart50%，p95 <= Smart*1.20+500ms。
密集变化p95 <= 基线*1.25+1s，存储 <=1.25倍；恢复p95 <=Core*1.50+1s。
插件缓冲/缓存<=256MiB。统计Host及子进程、临时空间、snapshot、读写、物理新增/保留与上传量。
原P2崩溃切点和新增witness/清理/Cloud切点，缺插件/损坏依赖/mcc/opaque/边界变化必须通过。
不能控制冷缓存时明确标记，不宣布Gate通过。性能不合格先优化，仍不合格保持开发入口。

P2验收为已完成历史事实。本阶段的实际命令、结果、未通过Gate另记验收文档，不覆盖P2证据。

## 已落地的开发顺序

1. `FileChangeCapturePlan` 复用 Smart 增删改结果；捕获前固定 Workspace revision、BaseVersion、policy tips。
2. API 3.10 可选上下文/file-view、lazy closure、候选文件独立字节验证。
3. MineRewind 完整目录 + recipe/data packs；mcc、opaque、Exact 残余字节；自包含 checkpoint。
4. 单语义主表示、封存 evidence、schema 3 统计缓存、临时 primary journal 清理、CoreFull 失败回退。
5. Cloud v2 缺失密文对象和 descriptor、v1 reader 兼容、共享 blob 保守保留。

尚未冻结公共发布或开放正常入口。P4 独立 Merge/文本 Provider 和 P5 地图冲突 UI 顺序不变。

## 2026-10-07 用户批准的缩减验收

用户允许减小测试规模、将客观暂不可实现的测试移出 Gate，并授权自动化。真实世界为用户提供的联机生存世界，约 537 MiB / 829 文件 / 513 region；测试只修改隔离副本。

- 每个普通场景 1 次预热 + 3 次计量；首次基线和 checkpoint 单独测量。小样本报告中位数和最大值，不报告有统计意义的 p95。原延迟容差用三次最大值对比，阈值不因结果调整。
- ≥4 GiB 真实样本、10 倍真实世界、可证明 OS 冷缓存、跨物理设备实测移出本次 Gate。原因分别为没有该语料、不能把复制膨胀当作独立真实样本、没有可证明的缓存控制、只有当前设备。冷启动不冒称冷缓存。
- 分阶段内核 I/O / 子进程精确归因作为诊断项，不作为阻止验收的数字门槛；提供实际可观测的进程 I/O、存储、传输、恢复和峰值指标并标明范围。
- 仍为硬 Gate：相同稳定快照条件、真实 chunk payload 变化、完整字节恢复、明确基线与依赖、不可变数据、损坏拒绝、取消/崩溃恢复、加密和 Cloud 闭包；不得移除可执行的安全测试。
- 不豁免实现缺口，也不把性能失败改成通过。若缩减矩阵暴露缺陷，修复后针对性重测；若仍超出准入标准，正常入口保持关闭并记录最终失败原因。

新结果写入独立最终验收记录，既有开发验收数据不改写。

## 2026-10-07 性能复核后的路线修订（用户已认可）

首次缩减性能矩阵失败，不能据此开放入口。保留安全性和 Exact，不增加 Normalized 表示。

1. 区分完整逻辑版本、自包含表示与物理整理。每个 root 已有完整目录，不按历史版本回放；语义路径不再继承 Core `MaxSmartBackupsPerFull` 的周期重打包策略。Core Smart 的原设置不变。
2. 删除插件按新字节 70% 或当前 root 放大 2 倍强制 checkpoint 的行为。后者只保留为统计提示；它既不是全局可回收空间，也不能证明云端共享 blob 可以释放。首次/不兼容/边界重建仍可自包含，验收显式请求 checkpoint。
3. 正常路径使用同一稳定 lease 下的 Host 文件变化视图。复用 size-mtime-v1 扫描与比较，只允许声明的新增/修改路径；Host 计算一次清单摘要并在本次操作共享。成功路径无需创建 Core transport；旧 transport journal/recovery reader 保留。失败按策略从仍有效稳定视图创建独立 CoreFull，取消不回退。
4. 插件直接读取受控视图，不复制第二个输入目录。流式对象消费在最后一个字节返回前核验摘要；不完整消费不视作验证，seek 使用原预验证路径。Host 仍从 candidate Artifact 闭包重建验证，不使用输入字节代替输出。
5. 新 pack 的布局实验保持 v1 上限 64 MiB，目标 16 MiB，并尽量让一个可容纳 region 不跨包；metadata 仍为 8 MiB，residual 仍为 64 KiB。必须连同节点数量和恢复成本一起验收，不能只看稀疏结果。旧布局仍可读取。
6. 有独立工作区的 Host codec 显式声明并行能力，最多四个 envelope worker；默认 codec 仍串行。所有 worker 结束后才清理失败工作区，任一失败不得提交。显式 checkpoint 可以预先并行验证其必读闭包；日常增量保持按需读取。
7. 自动 compaction 暂不启用。Host 未来必须证明全局可回收收益、空间预算和节点余量；当前超过硬配额时拒绝语义提交，按失败策略回退。不得放大 4096 节点限制掩盖长期问题。

使用 BCL ActivitySource 做可关闭的阶段测量，无新的公共 Plugin capability。嵌套/并行阶段报告 inclusive 耗时，不相加冒充端到端耗时。区分 Host I/O、已观测 7z 子进程 I/O、25ms 采样的并发子进程工作集，以及未观测的网络协议开销。调优中间结果不作为最终 Gate。

保留独立 Merge Provider 的后续路线；文本插件无需采用这些语义备份格式。正常入口继续受性能及安全 Gate 控制。

## 实验性交付决策与产品入口

用户后续明确批准按「性能例外公开、安全要求不降级」交付实验性 P3，并要求提供启用和手动 checkpoint 入口。本节覆盖前文「入口仍关闭」的阶段状态，不覆盖历史测试数据。

- 不宣称原性能 Gate 已通过：密集变化略超阈值、checkpoint 明显慢于 Core 的记录保留。显式 checkpoint 作为低频维护操作呈现，不重新启用周期重打包。
- 配置设置 → 备份策略 → 语义备份（实验性）。持久化 `ArtifactTransformPolicy.ExperimentalEnabled`，缺省 false，普通配置不迁移。策略仍使用现有 Transformer、Runtime、History、Artifact 管线。
- 根据当前活动插件的声明和能力匹配入口：增量输入、Full/Smart、完整/变化输入、file-view materializer、兼容 Config Kind。MineRewind 声明只覆盖 Java Kind；Bedrock、Rolling、选区/子集来源不开放。
- 可选择普通语义捕获转换失败后回退独立 CoreFull 并记录 warning，或停止。缺插件/无法安全捕获不强行回退；手动 checkpoint 一律禁止 Core 回退。
- 「保存并创建 checkpoint」先保存草稿，随后经同一配置操作门和备份事务为全部来源建立自包含版本。未变化也会执行；请求标志仅存在于当前 `BackupInvocationOptions`，不持久化、不影响下一次普通备份。提供取消、运行与结果状态。
- 关闭后下次普通备份建立 CoreFull，保留已有语义历史。界面说明恢复的插件依赖、额外时间/空间，以及云端副本退役不保证共享数据物理回收。
- UI 按用户要求只提供人工验收步骤。本轮自动验证限于构建、非 UI 测试和真实服务集成，不将人工 UI 验收列为已通过。

后续仍独立安排：收益驱动 compaction、更大世界长期成本、随机访问存储封装、云端空间分类面板。P4 语义 Merge 和文本 Provider 的独立能力边界不变。

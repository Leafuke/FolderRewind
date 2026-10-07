# P3：Smart 文件变化筛选与语义增量

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

## Gate（不得通过改阈值或挑样本规避）

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

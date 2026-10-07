# API 3.10 / P3 开发合同

状态：实验性实现，未发布 NuGet、插件或用户入口。跨仓库测试引用隔离 feed 中的 `3.10.0-p3.1`，assembly identity 仍为 `3.0.0.0`。既有构造器、enum 数值和 Full v1 reader 保留。

## Host 边界

`ArtifactTransformRequest.IncrementalInput` 固定 CaptureId、Full/ChangesOnly、boundary、size-mtime-v1、BaseVersion、BaseRepresentation、base root 和增删改集合。`ReadableArtifacts` 是本次授权闭包，不是全 store 查询入口。Manifest 的 `SupportsIncrementalInput` 默认 false；旧 provider 不收到增量请求。

`IArtifactFileViewProvider` 是现有 Materializer 的可选扩展，未增加 capability kind。view 仅用其候选闭包重建文件；Host 持有 capability/read lease 直到 view 和流释放。它不授予 live source、primary 或目标目录访问权。

`ArtifactTransformResult.RequiresFullVerification` 仅扩大验证集合，用于 provider 决定的自包含重打包。Host 要求其闭包全部为本次新 Artifact，并实际验证全部文件。插件不能用它减弱验证。

Host 独立生成 `_host/capture-evidence.json`，绑定 Source/Version/Representation/boundary，保存完整 path/length/SHA 与捕获统计。它随 root 封存、加密和传输。未变文件必须继承相同 recipe reference；变化文件从 candidate view 实际读取并计算 SHA。仅比较插件自报 SHA 不构成验证。

`SourceCaptureBaseline` schema 3 区分 Artifact root/evidence 与 Core locator，接受旧 schema 2。Workspace 的 Version、源路径及 ContentRealizationId 决定适用性；Restore/Checkout 改变实现态。丢失统计 witness 时执行冷缓存内容复核，不能信任原捕获 mtime 代表恢复后的 mtime。

Host 在捕获前固定 Workspace revision、BaseVersion 和 policy tips，提交时比较固定值。成功提交只产生 PluginArtifact 主表示，临时 Core 输入不进入表示依赖。清理由 journal 中的所有权路径和原 SHA 决定；缓存发布失败不撤销 durable History。

## Minecraft 表示（开发候选 v1）

```text
Version / Representation
  state root (完整文件目录 + Host evidence)
    recipe/index packs
      immutable data packs
```

root 不引用旧 root；父版本关系不充当数据依赖。每个文件记录 path、长度、SHA、recipe 和可选 chunk index reference。引用明确包含 ArtifactId、pack.bin、offset、length、SHA。recipe 为覆盖整个原文件的有序连续 segment；Restore 与验证使用同一个执行器。

数据 pack 上限 64 MiB，recipe/index pack 上限 8 MiB。有效 region 的原始压缩 payload 独立引用；剩余 header/envelope/padding/free sectors/tail 按原偏移对齐的 64 KiB 段保存。Raw 文件按最多 64 MiB 分段；零字节文件有空 recipe。完整 opaque 字节不解释为 chunk 删除。

只处理 Host 宣告的变化文件。`.mcc` 单独变化会刷新 owner index，保持 owner 原始 recipe；关联依据最终 inventory，未在删除集的 orphan 保留。相同压缩 payload/hash 可复用已验证的 decoded hash；chunk timestamp 不充当内容证明。读取失败、取消或解码预算耗尽失败；已读到的未知压缩/损坏字节保留为 opaque。

周期 checkpoint 复用 MaxSmartBackupsPerFull；新原始对象达到逻辑字节的 70% 时本次自包含重打包，复用本次已生成 payload，不重捕获。每次计算完整 recipe/index/data 闭包的逻辑 pack 字节和有效 reference 区间并集；超过 2 倍时建议下一次 checkpoint。此统计是解压后的逻辑保留放大，**不是压缩后的物理空间统计**；物理成本仍须从 Host ledger/Cloud 实测。

重打包中被替换的暂存 metadata 不进入最终 root 的新闭包。Runtime 只封存/提交 patch 所引用的分配；其他暂存分配随 staging 释放。

## 完整性和 Cloud

Lazy lease 固定并 pin 全闭包，预检身份、可用性、大小及 envelope；只解封实际访问的节点。访问时验证 storage/logical bytes。实际观察到的 hash 错误留下 Host integrity 标记；后续 shallow 继承拒绝已知损坏，修复或 fresh deep verification 才清除。未访问旧字节不宣称本次完成 deep verification。

Cloud v2 descriptor 指向独立密文对象，key 绑定 Config 和 storage SHA。缺失对象先上传并验证，descriptor 和 Replica 事实最后发布。rclone receipt 证明此前验证成功，加当前存在/大小检查；它不证明远端当前没有同大小 bitrot。下载仍逐对象校验 SHA、完整闭包再发布。v1 bundle reader 保留。对象下载目前复用 v1 导入事务，存在一次临时 bundle 的额外 I/O。

副本 descriptor 的退役不删除共享 blob；不能依据本机 History 推断其他设备无引用。未实现云端共享对象物理回收，不得宣称已回收空间。

## 不在此合同内

chunk 子集、语义 Merge、增量 Merge 输出、通用 Capture Engine、block/NBT 字段合并、跨设备 blob GC。Spatial Preview 保持只读。文本 Merge Provider 可以独立使用未来 Merge seam。

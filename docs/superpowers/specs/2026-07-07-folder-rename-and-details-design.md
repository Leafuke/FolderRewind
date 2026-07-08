# FolderRewind 文件夹重命名与详细信息设计

**日期**：2026-07-07
**状态**：已批准
**作者**：Codex

---

## 概述

本轮设计为 `FolderManagerPage` 引入两个新能力：

1. **文件夹重命名**
   用户在 Manager 卡片中直接重命名被管理的源文件夹。操作会真实修改磁盘上的目录名，并同步迁移本地备份目录、`_metadata` 目录、历史记录和相关配置引用，确保 FolderRewind 重命名后仍能继续识别同一份“存档/文件夹”。
2. **详细信息**
   用户在 Manager 卡片中打开一个详情对话框，查看该文件夹的基础信息与插件扩展信息。宿主统一渲染只读信息，插件只负责返回额外的键值数据。本轮同步为 MineRewind 增加 Minecraft 世界信息扩展。

本设计采用已选定的 **方案 1：事务式本地迁移 + 只读详情插件接口**。

---

## 背景与动机

### 问题 1：现有识别链路对目录名变化不友好

当前项目中，`ManagedFolder.Path` 是源文件夹的稳定识别锚点，但 `DisplayName` 又同时参与多个关键路径与索引：

- 本地备份目录：`{DestinationPath}/{FolderName}`
- 本地元数据目录：`{DestinationPath}/_metadata/{FolderName}`
- 历史记录中的 `HistoryItem.FolderName`
- 云端默认远程路径：`{RemoteBasePath}/{ConfigName}/{FolderName}/...`
- Manager / History 页面最近选择
- 部分 KnotLink 文本事件与按名称查询逻辑

如果用户在资源管理器里直接修改源目录名，`ManagedFolder.Path` 会失效，FolderRewind 无法自动把旧路径识别为同一文件夹。即使只在应用内修改 `DisplayName`，也会导致“源目录名、备份目录名、历史显示名、云端路径名”各层脱节。

因此，本轮的“重命名文件夹”不能只是 UI 层改一个字段，而必须是一次**受控的数据迁移**。

### 问题 2：当前 Manager 卡片缺少可扩展的“详细信息”

Manager 页面已经承担“选择、备份、管理”入口职责，但 `...` 菜单中的信息密度不够，用户无法直接查看：

- 文件夹大小
- 修改日期 / 创建日期
- 统计文件数和子目录数
- 对特定类型文件夹更有价值的领域信息

以 MineRewind 为例，Minecraft 玩家更关心的是：

- 世界名
- 游戏模式
- 种子
- 世界时间 / 天数
- 最近一次写入时间

这类信息适合以插件扩展方式接入，但宿主应继续控制 UI 结构，避免让插件直接输出自定义界面造成风格和稳定性失控。

---

## 目标

- 在 Manager 卡片的 `...` 菜单中新增“重命名”和“详细信息”入口。
- “重命名”只支持修改最后一级目录名，父目录保持不变。
- 重命名时真实修改磁盘上的源目录名。
- 如果同一路径被多个配置引用，一次操作要更新所有引用。
- 本地迁移必须尽量做到全有或全无：若任一关键步骤失败，系统应尝试回滚到旧状态。
- 本地备份目录和 `_metadata` 目录随重命名一并迁移。
- 云端目录迁移不作为本轮成功前提；旧云路径继续保留可用，新备份可自然落到新名字对应路径。
- “详细信息”对话框秒开，耗时统计项异步刷新。
- 插件扩展采用只读键值信息接口，宿主统一渲染。
- MineRewind 本轮实现首个详情扩展，基于 `fNbt` 读取 `level.dat`。

## 非目标

- 不支持把源目录移动到新的父目录。
- 不在本轮引入新的稳定 `FolderId` 身份模型。
- 不在本轮真正迁移云端目录树或重写远端历史文件结构。
- 不允许插件返回自定义 XAML / Markdown / 富文本 UI。
- 不在本轮实现详情页的持久缓存、图表或复杂可视化。

---

## 方案总览

### 方案选择

本设计选用：

- **方案 1：事务式本地迁移 + 只读详情插件接口**

其核心思路是继续沿用当前以 `ManagedFolder.Path` 为识别锚点的宿主模型，但把“重命名”收敛成一个统一的迁移服务，在操作前预检影响范围，在操作中记录回滚日志，在操作后统一更新本地引用。

### 否决方案

- **方案 2：先引入 `FolderId` 再做重命名**
  长期最整洁，但当前会波及历史、自动化、窗口、KnotLink、云同步等多层索引，改造跨度过大，不适合作为本轮功能交付前置条件。
- **方案 3：只改源目录和配置路径，其他尽量不动**
  开发成本最低，但会留下“本地备份目录名/历史名/源目录名分裂”的问题，不符合本轮目标。

---

## 设计一：文件夹重命名

### 用户入口

在 `FolderManagerPage.xaml` 的卡片 `MenuFlyout` 中新增：

- `FolderManager_RenameFolder`

点击后弹出 `ContentDialog`，用于输入新目录名。

### 交互约束

对话框只允许输入新目录名，而非完整路径。用户无法修改父目录。

对话框中需要显示：

- 当前名称
- 新名称输入框
- 当前完整路径
- 影响说明摘要

影响说明至少包括：

- 将重命名磁盘上的源目录
- 将迁移本地备份目录
- 将迁移本地 `_metadata` 目录
- 将更新所有引用该路径的配置
- 将更新历史记录和最近选择
- 云端旧记录保留原路径，不保证远端目录跟随迁移

### 宿主服务

新增宿主服务：

- `FolderRenameService` 或 `FolderRenameCoordinatorService`

职责：

- 统一执行预检、迁移、回滚、模型更新与结果汇总
- 不把复杂迁移逻辑散落在 `FolderManagerPageViewModel`、`HistoryService`、`CloudSyncService`、`MiniWindowService` 中

建议返回结构：

```csharp
public sealed class FolderRenameResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string OldPath { get; init; } = string.Empty;
    public string NewPath { get; init; } = string.Empty;
    public int AffectedConfigCount { get; init; }
    public int AffectedHistoryCount { get; init; }
    public bool LocalBackupDirectoryMigrated { get; init; }
    public bool LocalMetadataDirectoryMigrated { get; init; }
}
```

### 预检阶段

重命名前先进行完整预检，失败即不进入迁移阶段。

预检项：

1. 当前 `ManagedFolder.Path` 非空且目录存在。
2. 新名字非空，去除空白后仍有效。
3. 新名字不包含非法文件名字符，不是 `.` / `..`。
4. 新名字与旧目录名不同。
5. 同级目录下不存在同名目标目录。
6. 找出所有引用旧路径的 `ManagedFolder`，如果为 0 说明上下文异常。
7. 计算所有受影响配置的本地备份目录与 `_metadata` 目录。
8. 若 MiniWindow 正在打开该路径，则先要求关闭或由宿主主动关闭并停止 watcher。
9. 若关键目录正在占用导致无法重命名，应直接阻止操作。

引用扫描范围：

- `ConfigService.CurrentConfig.BackupConfigs[*].SourceFolders[*].Path`
- 自动化单文件夹目标 `Automation.TargetFolderPath`
- 最近选择：`GlobalSettings.LastManagerFolderPath` / `LastHistoryFolderPath`
- 历史记录：`HistoryItem.FolderPath`

### 迁移范围

本轮的本地迁移范围如下：

1. **源目录**
   `OldParent\OldLeaf` -> `OldParent\NewLeaf`
2. **本地备份目录**
   `{DestinationPath}/{OldStorageFolderName}` -> `{DestinationPath}/{NewStorageFolderName}`
3. **本地元数据目录**
   `{DestinationPath}/_metadata/{OldStorageFolderName}` -> `{DestinationPath}/_metadata/{NewStorageFolderName}`

说明：

- “存储目录名”继续使用当前项目的安全文件夹名规则，即 `BackupStoragePathService.TryResolveStorageFolderName(...)`。
- 本轮只迁移本地，不迁移云端目录树。
- 如果某个配置尚无备份目录或 `_metadata` 目录，则视为可跳过，不算失败。

### 显示名同步规则

`ManagedFolder.DisplayName` 不能无条件覆盖。

规则如下：

- 如果当前 `DisplayName` 等于旧目录叶子名，则自动同步为新目录叶子名。
- 如果当前 `DisplayName` 是用户自定义名，则保持不变，只更新 `Path`。

这样既能让默认命名行为符合用户预期，也不会覆盖用户已手工维护的展示名。

### 多配置引用处理

用户已明确要求：

- 如果同一个物理文件夹被多个配置引用，重命名时一次性更新所有引用该路径的配置。

因此宿主应把“受影响文件夹引用”抽象为一个集合，不允许只更新当前配置的一份副本。

### 历史记录更新规则

历史记录需要同时兼容“源路径识别”和“本地归档目录定位”。

本轮更新规则：

1. 所有受影响 `HistoryItem.FolderPath` 更新为新路径。
2. `HistoryItem.FolderName` 只在其当前值等于旧存储目录名时同步更新为新存储目录名。
3. 若某条历史记录的 `FolderName` 明显已经是旧自定义备份身份名，则不强制覆盖。
4. `CloudArchiveRemotePath`、`CloudMetadataRecordRemotePath`、`CloudMetadataStateRemotePath` 原样保留。

这样做的原因是：

- `FolderPath` 是当前导入与映射时最稳定的宿主锚点。
- `FolderName` 同时决定本地归档定位，必须随本地备份目录迁移而更新。
- 远端路径是“既有事实”，不能因为本地重命名就假定云端目录也同步存在。

### 自动化与页面状态更新

受影响字段：

- `Automation.TargetFolderPath`
- `GlobalSettings.LastManagerFolderPath`
- `GlobalSettings.LastHistoryFolderPath`

如果这些字段等于旧路径，则同步替换为新路径。

### MiniWindow / Watcher 行为

由于 `MiniWindowService` 和 `FolderWatcherService` 目前都以 `folder.Path` 作为键，本轮必须显式处理：

1. 若该文件夹的 MiniWindow 打开中，先关闭对应窗口。
2. 停止旧路径 watcher。
3. 重命名完成后，不自动恢复旧窗口；由用户重新打开。

不建议在本轮做“自动把已打开窗口无缝迁移到新路径”，因为这会把 UI 生命周期、文件监视和数据迁移耦合过深。

### 事务式执行与回滚

迁移采用三阶段：

1. **预检**
2. **文件系统迁移**
3. **模型/索引更新并持久化**

文件系统迁移时需要维护 rollback journal，记录每一步成功的“旧路径 -> 新路径”移动。

推荐执行顺序：

1. 关闭 MiniWindow / watcher
2. 重命名源目录
3. 对每个受影响配置迁移本地备份目录
4. 对每个受影响配置迁移 `_metadata` 目录
5. 更新内存模型与历史记录
6. `ConfigService.Save()`
7. `HistoryService.Save()`

如果步骤 2-5 中任一步失败：

- 逆序执行回滚
- 尝试把已移动的目录移回旧路径
- 恢复内存中的旧值
- 对用户提示“重命名失败，已尝试回滚”

本轮成功标准是：

- 只有在本地关键资源都成功迁移并保存后，才算重命名成功

### 云端兼容策略

用户已明确接受：

- 云端目录迁移如果很难实现，本轮可以不做

因此本轮策略是：

1. 不主动搬迁云端目录
2. 不修改已有历史项中的远端路径字段
3. 旧云备份继续通过历史记录中的旧远端路径访问
4. 重命名后的新备份按新名字生成新的默认远端路径

结果上会形成“旧云记录在旧目录，新云记录在新目录”的兼容状态，但不会影响本地识别，也不会破坏已有远端可下载性。

---

## 设计二：详细信息对话框

### 用户入口

在 `FolderManagerPage.xaml` 的卡片 `MenuFlyout` 中新增：

- `FolderManager_Details`

点击后弹出 `FolderDetailsDialog`。

### 交互目标

- 对话框必须秒开。
- 快速可得的信息立即显示。
- 大小、文件数、子目录数等耗时项异步加载。
- 对话框关闭时，后台统计任务应取消。

### 宿主结构

新增：

- `Views/FolderDetailsDialog.xaml`
- `Views/FolderDetailsDialog.xaml.cs`
- `ViewModels/FolderDetailsDialogViewModel.cs`

统一数据结构：

```csharp
public sealed class FolderDetailsSection
{
    public string Title { get; init; } = string.Empty;
    public IReadOnlyList<FolderDetailsItem> Items { get; init; } = Array.Empty<FolderDetailsItem>();
}

public sealed class FolderDetailsItem
{
    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool IsLoading { get; init; }
    public bool IsError { get; init; }
}
```

XAML 只负责渲染 section 和 item，不为具体插件写分支 UI。

### 基础信息区

宿主统一提供的基础信息建议至少包括：

- 名称
- 完整路径
- 目录状态（存在 / 缺失）
- 创建时间
- 最后修改时间
- 文件夹大小
- 文件数
- 子目录数
- 最近备份时间
- 配置名称
- 配置类型
- 本地备份目录
- 本地元数据目录

其中：

- 路径、配置名、配置类型可同步显示
- 大小、文件数、子目录数通过后台递归统计
- 备份目录和元数据目录通过 `BackupStoragePathService` 计算

### 异步统计策略

大小统计采用：

- 对话框先显示
- 后台异步统计
- 初始文案显示“正在统计”

后台统计应支持：

- `CancellationToken`
- 捕获异常并回写错误态
- 不阻塞 UI 线程

统计建议输出：

- 总字节数格式化为 KB / MB / GB
- 文件数量
- 子目录数量

### 错误处理

如果目录不存在或扫描失败：

- 基础信息区仍然显示已知静态信息
- 对失败字段显示“统计失败”或“目录不存在”
- 插件扩展区仍可单独显示

---

## 设计三：插件详情接口

### 接口目标

插件扩展只能提供**只读键值信息**，由宿主统一渲染。

不允许插件：

- 返回自定义 XAML
- 返回 Markdown / HTML
- 直接操作对话框控件

### 新增接口

新增可选接口：

```csharp
public interface IFolderRewindFolderDetailsProvider
{
    Task<IReadOnlyList<FolderDetailsSection>> GetFolderDetailsSectionsAsync(
        BackupConfig config,
        ManagedFolder folder,
        IReadOnlyDictionary<string, string> settingsValues,
        CancellationToken cancellationToken);
}
```

### 插件服务入口

在 `PluginService` 中新增统一调用入口，例如：

```csharp
public static Task<IReadOnlyList<FolderDetailsSection>> GetFolderDetailsSectionsAsync(
    BackupConfig config,
    ManagedFolder folder,
    CancellationToken cancellationToken)
```

行为：

1. 遍历所有已启用插件
2. 仅调用实现了 `IFolderRewindFolderDetailsProvider` 的插件
3. 对单个插件使用 `try/catch` 隔离
4. 某个插件失败时写日志，不中断其他插件

### 渲染规则

宿主最终展示顺序：

1. 基础信息
2. 插件信息 section（按插件返回顺序附加）

每个插件 section 的标题由插件给出，例如：

- `Minecraft 世界信息`

---

## MineRewind 扩展设计

### 实现方式

`MinecraftSavesPlugin` 本轮新增实现：

- `IFolderRewindFolderDetailsProvider`

使用现有 `fNbt` 依赖和 `NbtHelper` 读取 `level.dat`。

### 信息来源

优先读取：

- `Data.LevelName`
- `Data.GameType`
- `Data.RandomSeed`
- `Data.WorldGenSettings.seed`（兼容新结构）
- `Data.LastPlayed`
- `Data.DayTime`
- `Data.Time`
- `Data.Player`

### 展示字段

建议输出：

- 世界名称
- 游戏模式
- 种子
- 世界天数
- 世界总时间
- 最近游玩时间
- 是否检测到玩家数据

### 关于“游玩时间”的措辞

本轮不应把 `level.dat` 中的时间字段表述成“精确玩家在线时长”。

原因：

- `Time` / `DayTime` 反映的是世界时间推进，不等于精确的人类游玩时长

因此对外文案应使用：

- `世界总时间`
- `世界天数`

如果需要进一步友好展示，可把 `Time` 按 20 tick = 1 秒换算为近似时长，并在描述里注明“基于世界时间换算”。

### 降级行为

如果目标目录不满足 Minecraft 世界结构：

- 没有 `level.dat`
- `level.dat` 读取失败
- 关键字段不存在

则 MineRewind 不抛错中断，而是：

- 返回空 section
- 或返回部分可读字段

---

## 涉及文件

### 宿主侧

- `FolderRewind/Views/FolderManagerPage.xaml`
- `FolderRewind/Views/FolderManagerPage.xaml.cs`
- `FolderRewind/ViewModels/FolderManagerPageViewModel.cs`
- `FolderRewind/Views/FolderDetailsDialog.xaml`
- `FolderRewind/Views/FolderDetailsDialog.xaml.cs`
- `FolderRewind/ViewModels/FolderDetailsDialogViewModel.cs`
- `FolderRewind/Services/FolderRenameService.cs`
- `FolderRewind/Services/HistoryService.cs`
- `FolderRewind/Services/Plugins/IFolderRewindPlugin.cs`
- `FolderRewind/Services/Plugins/PluginService.cs`
- `FolderRewind/Models/BackupModels.cs`
- `FolderRewind/Strings/zh-CN/Resources.resw`
- `FolderRewind/Strings/en-US/Resources.resw`

### 插件侧

- `FolderRewind-Plugin-Minecraft/MineRewind/MinecraftSavesPlugin.cs`
- `FolderRewind-Plugin-Minecraft/MineRewind/NbtHelper.cs`

### 文档

- `FolderRewind-Site/docs/plugins/developing/plugin-api.md`

---

## 测试策略

### 1. 重命名迁移测试

重点验证：

1. 单配置、单文件夹重命名成功
2. 同一路径被多个配置引用时全部更新
3. 默认显示名跟随目录名同步
4. 自定义显示名不被覆盖
5. 本地备份目录迁移成功
6. `_metadata` 目录迁移成功
7. 历史记录 `FolderPath` / `FolderName` 更新正确
8. `Automation.TargetFolderPath` 更新正确
9. 最近选择路径更新正确
10. 任一步失败时能回滚

推荐单测/集成测试关注：

- 文件系统路径冲突
- 目录不存在
- 目录被占用
- 回滚后二次打开配置仍保持旧状态

### 2. 详情对话框测试

重点验证：

1. 对话框可立即打开
2. 大小、文件数、子目录数异步刷新
3. 关闭对话框时取消后台统计
4. 缺失目录时显示降级信息
5. 插件失败不会阻断宿主基础信息

### 3. MineRewind / NBT 解析测试

重点验证：

1. `level.dat` 存在时可解析基础世界信息
2. 同时兼容旧结构种子和新结构种子
3. `Time` 换算结果正确
4. 缺失 `Player` compound 时正常降级
5. `level.dat` 损坏时仅返回失败/空 section，不抛出未处理异常

---

## 风险与缓解

### 风险 1：重命名触及层面多，容易漏索引

缓解：

- 统一通过 `FolderRenameService` 收敛影响范围
- 把路径替换点列表写入实现前计划

### 风险 2：文件系统回滚不完全

缓解：

- 只在关键步骤全部成功后才持久化
- 文件系统移动使用 journal 逆序回滚
- 失败时给出明确错误信息，避免“半成功但静默”

### 风险 3：详情统计导致卡顿

缓解：

- 对话框秒开
- 耗时统计后台执行
- 支持取消

### 风险 4：MineRewind 字段含义被用户误解

缓解：

- 避免把世界时间描述成精确“游玩时长”
- 在字段描述中说明其来源或换算方式

---

## 实施顺序

1. 在 Manager 菜单中加入“重命名”和“详细信息”入口
2. 实现 `FolderRenameService` 的预检、迁移、回滚与引用更新
3. 补齐重命名相关字符串、本地通知和错误提示
4. 实现 `FolderDetailsDialog` 与 `FolderDetailsDialogViewModel`
5. 新增插件详情接口和 `PluginService` 调用入口
6. 为 MineRewind 实现 `level.dat` 详情扩展
7. 补充测试与插件 API 文档

---

## 成功标准

满足以下条件可视为本轮设计达成：

- 用户可在 Manager 卡片中重命名受管文件夹
- 重命名后 FolderRewind 继续识别该文件夹，且本地备份链不丢失
- 多配置引用、历史记录、自动化目标和最近选择均正确更新
- 本地迁移失败时系统会尝试回滚，而不是留下静默半完成状态
- 用户可在详情对话框中查看基础信息
- MineRewind 可为 Minecraft 世界展示额外详情
- 插件扩展接口文档同步更新

---

## 变更历史

- 2026-07-07：初始设计文档

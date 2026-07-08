# FolderRewind 插件启动后自动补全配置与 MineRewind 自动发现设计

**日期**：2026-07-08
**状态**：已批准
**作者**：Codex

---

## 概述

本轮设计聚焦两个紧密相关的目标：

- 为 FolderRewind 插件系统新增“应用启动后自动补全现有配置”的宿主接口
- 基于该接口优化 MineRewind，使其能够在应用启动后自动把新出现的 Minecraft 存档加入已有的 `Minecraft Saves` 配置

用户已确认的约束如下：

- 采用 **方案 A：新增正式插件接口，由宿主统一编排配置补全**
- `MineRewind` 的“自动发现存档”是 **全局插件设置**
- 该开关 **默认启用**
- 自动发现 **只补充已有的 `Minecraft Saves` 配置**，**不自动新建配置**
- 自动扫描范围只限于 **已有世界所属的同级 `saves` 目录**
- 若一个配置中混放多个版本来源，则对每个来源分别补扫
- 启动后若确实新增了存档，应给用户一条简短提示
- 当用户把“自动发现存档”从关闭切换为开启时，应立即执行一次补扫
- MineRewind 中现有“热备份”设置应删除，热备份能力改为默认始终启用

本设计的目标是把“启动后自动补配置”做成正式插件能力，而不是把 Minecraft 规则硬编码回主程序。

---

## 背景与现状

### 当前插件系统现状

FolderRewind 当前已经具备以下插件能力：

- 插件定义配置类型
- 插件介入备份与还原流程
- 插件手动发现文件夹
- 插件批量创建配置
- 插件自定义设置 UI

现有相关入口主要包括：

- [IFolderRewindPlugin.cs](D:/Programs/FolderRewind/FolderRewind/Services/Plugins/IFolderRewindPlugin.cs)
- [PluginService.cs](D:/Programs/FolderRewind/FolderRewind/Services/Plugins/PluginService.cs)
- [App.xaml.cs](D:/Programs/FolderRewind/FolderRewind/App.xaml.cs)
- [PluginsKnotLinkControl.xaml.cs](D:/Programs/FolderRewind/FolderRewind/Views/Settings/PluginsKnotLinkControl.xaml.cs)

目前插件发现能力主要面向“用户手动指定一个根目录，然后让插件帮我发现文件夹或创建配置”。它还没有一个正式入口来表达：

- “应用启动后，检查现有配置是否需要被插件自动补全”

### MineRewind 当前现状

MineRewind 已具备：

- `Minecraft Saves` 配置类型
- 从 `.minecraft` / `versions/<version>/saves` 手动发现世界
- 批量创建 Minecraft 配置
- 热备份与热还原
- 插件设置与热键能力

当前相关实现主要位于：

- [MinecraftSavesPlugin.cs](D:/Programs/FolderRewind/FolderRewind-Plugin-Minecraft/MineRewind/MinecraftSavesPlugin.cs)
- [MinecraftSavesPlugin.Discovery.cs](D:/Programs/FolderRewind/FolderRewind-Plugin-Minecraft/MineRewind/MinecraftSavesPlugin.Discovery.cs)
- [MinecraftSavesPlugin.Localization.cs](D:/Programs/FolderRewind/FolderRewind-Plugin-Minecraft/MineRewind/MinecraftSavesPlugin.Localization.cs)

MineRewind 已经支持手动扫描 `.minecraft` 下的世界，但还没有在应用启动后对“已有 Minecraft 配置”执行自动补齐。

### 当前缺口

现状对 Minecraft 版本隔离用户不够友好：

- 用户可能先手动把 `versions/<version>/saves/WorldA` 加进了配置
- 随后又在同一个 `saves` 目录里新建了 `WorldB`
- 现有流程下，`WorldB` 不会自动进入 FolderRewind，仍需用户手动补加

因此本轮需要把“启动后自动增量补世界”的能力抽象成插件接口，并由 MineRewind 使用它。

---

## 目标

- 为插件系统新增正式的“启动后自动补全配置”能力
- 让宿主负责统一编排、异常隔离、去重、保存和通知
- 让 MineRewind 在启动后自动补充同级 `saves` 目录中新出现的世界
- 删除 MineRewind 中冗余的“热备份”插件设置
- 保持现有手动发现、批量创建、热备份和还原行为不回归

## 非目标

- 不在本轮自动为新的 Minecraft 版本目录创建全新配置
- 不在本轮支持用户自定义自动扫描根目录
- 不在本轮把“自动发现存档”做成每个配置单独开关
- 不在本轮自动处理显示名冲突后的重命名策略
- 不在本轮重做整个插件设置系统

---

## 方案总览

### 选定方案

本设计采用：

- **方案 A：新增插件接口，由宿主统一执行“启动后配置补全”**

核心原则如下：

- 插件负责“识别哪些现有配置可以补、应该补哪些文件夹”
- 宿主负责“调用、去重、冲突过滤、保存、通知、日志”
- 插件不直接修改 `ConfigService.CurrentConfig`

### 未采用方案

- **方案 B：在 Host 中硬编码 MineRewind 自动发现逻辑**
  改动快，但会把 Minecraft 特例塞回主程序，违背插件化方向。
- **方案 C：只复用现有 `TryDiscoverManagedFolders` / `TryCreateConfigs`，由 Host 侧拼装自动行为**
  看似少改接口，但 Host 仍要猜测 MineRewind 语义，耦合没有真正消失。

---

## 设计一：新增插件配置补全接口

### 接口职责

新增一个可选插件接口，专门表达“对现有配置的增量补全能力”。建议命名为：

- `IFolderRewindConfigAugmenter`

它的职责不是创建新配置，而是：

- 检查当前已有配置
- 返回应追加到某个已有配置中的 `ManagedFolder`

### 输入与输出模型

建议接口按“宿主提供上下文，插件返回补丁”的模式设计。

插件输入至少包括：

- 当前配置列表快照
- 插件自身设置值
- 触发原因，例如 `Startup` / `SettingsEnabled`

插件输出建议为显式补丁对象，而不是直接改配置。例如：

- 一个结果集合
- 每条结果声明目标 `ConfigId`
- 每条结果附带待新增的 `ManagedFolder` 列表
- 可选统计或日志摘要

推荐新增以下模型：

- `PluginConfigAugmentationReason`
- `PluginConfigAugmentationRequest`
- `PluginConfigAugmentationItem`
- `PluginConfigAugmentationResult`

### 宿主与插件边界

边界应固定为：

- 插件只返回“建议新增哪些文件夹”
- 宿主负责应用补丁
- 宿主负责最终 `ConfigService.Save()`
- 宿主负责通知与日志

这样做有几个直接好处：

- 宿主可以统一复用现有去重与冲突规则
- 插件不会绕过宿主直接污染全局配置状态
- 后续别的插件也能复用这套能力

### 向后兼容

该接口应为可选接口：

- 不实现该接口的插件完全不受影响
- 现有 `IFolderRewindPlugin` 行为保持不变
- 宿主仅在插件实现该新接口时才调用

---

## 设计二：宿主执行流程

### 启动时机

宿主在 [App.xaml.cs](D:/Programs/FolderRewind/FolderRewind/App.xaml.cs) 中完成 `PluginService.Initialize()` 后，异步触发一次“配置补全”流程。

建议新增宿主入口，例如：

- `PluginService.RunConfigAugmentationAsync(PluginConfigAugmentationReason reason)`

首次启动调用使用：

- `reason = Startup`

该调用应满足：

- 不阻塞首屏显示
- 在后台异步执行
- 单个插件失败不影响其他插件

### 执行步骤

宿主执行顺序建议如下：

1. 收集已启用且已加载的插件
2. 过滤出实现 `IFolderRewindConfigAugmenter` 的插件
3. 为每个插件构造请求上下文
4. 调用插件并捕获异常
5. 聚合所有插件返回的补丁结果
6. 统一将补丁应用到当前配置
7. 若至少有一项实际新增，则只保存一次配置
8. 若触发者允许通知且确有新增，则弹出简短提示

### 防重与并发保护

宿主需要保证：

- 启动补扫与“设置开启后立即补扫”不会并发地重复运行
- 同一轮运行中，同一个路径不会被重复加入同一配置
- 同一配置如果收到多个插件对同一路径的建议，只处理一次

建议 `PluginService` 为该流程维护一个轻量运行锁，避免重复触发造成双写。

### 补丁应用规则

宿主在应用每条补丁时，应按如下顺序过滤：

- 目标配置存在
- 目标配置类型仍然匹配插件意图
- 候选 `ManagedFolder.Path` 非空
- 候选路径尚未存在于该配置中
- 候选显示名不与现有文件夹显示名冲突

若任一条件不满足：

- 跳过该项
- 写调试日志
- 不向用户弹错误

### 保存策略

保存策略应保持保守：

- 仅在本轮实际新增了至少一个文件夹时才 `ConfigService.Save()`
- 无论补了多少个配置，本轮只保存一次

这样可以减少：

- 不必要的磁盘写入
- `ConfigService.Saved` 引发的连锁行为

---

## 设计三：MineRewind 设置与功能调整

### 新设置项

MineRewind 插件设置调整为：

- 删除 `EnableHotBackup`
- 保留 `PreservePlayerData`
- 新增 `AutoDiscoverSaves`

其中：

- `AutoDiscoverSaves` 类型为 `Boolean`
- 默认值为 `true`
- 显示名称为“自动发现存档”
- 描述强调“在应用启动后，自动将现有 Minecraft 配置所属 `saves` 目录中的新世界补充进配置”

### 默认启用策略

由于插件设置当前由 `Dictionary<string, string>` 承载，未填写时会回落到设置定义中的默认值，因此：

- 老用户升级后无需显式迁移脚本
- 未设置该项时按 `true` 处理

### 删除热备份设置

MineRewind 中“启用热备份”设置删除后，行为调整为：

- 热备份逻辑始终可用
- 初始化阶段不再读取 `EnableHotBackup`
- 与热备份相关的文案、设置项与本地化资源同步清理

这符合当前产品事实：

- 热备份已成为默认能力
- 保留显式开关只会增加理解成本

---

## 设计四：MineRewind 的自动补扫规则

### 作用范围

MineRewind 的自动补扫只处理：

- `ConfigType == "Minecraft Saves"` 的配置
- 且该配置应由 MineRewind 负责解释

它不负责：

- 自动新建配置
- 跨插件补普通文件夹
- 跨版本把别的 `saves` 目录混进不相关配置

### 扫描锚点

对每个目标配置，MineRewind 遍历当前已有 `SourceFolders`，仅把“看起来是 Minecraft 世界”的文件夹作为扫描锚点。

判定规则为：

- 文件夹路径下存在 `level.dat`

若一个配置里混放了多个世界来源，则：

- 每个世界都可成为锚点

### 扫描边界

每个锚点只回退到它所属的同级 `saves` 目录进行补扫。

例如：

- 已有 `...\versions\1.20.1\saves\WorldA`
- 则只扫描 `...\versions\1.20.1\saves\`

不会因为配置里存在一个 Minecraft 世界，就重扫整个 `.minecraft` 根目录。

这保证了自动补扫行为：

- 只补同版本、同来源下的新世界
- 不会把其他版本目录中的世界误加入当前配置

### 多来源配置

若一个配置中已有多个版本来源，例如：

- `...\versions\1.20.1\saves\WorldA`
- `...\versions\1.21\saves\WorldB`

则 MineRewind 应：

- 对 `1.20.1\saves` 扫一次
- 对 `1.21\saves` 再扫一次

同时应先按 `saves` 目录去重，避免同一目录被多个现有世界重复扫描。

### 候选世界过滤

从某个 `saves` 目录发现的候选世界，必须满足：

- 目录存在
- 含 `level.dat`
- 路径当前尚未存在于该配置
- 显示名不会与当前配置中已有文件夹冲突

若显示名冲突：

- 直接跳过
- 不自动改名

这样可以避免启动过程中静默改变用户的命名体系。

### `ManagedFolder` 构造

自动补进的世界沿用 MineRewind 当前发现逻辑生成：

- `DisplayName` 使用文件夹名
- `Description` 使用 Minecraft 世界描述
- `CoverImagePath` 优先取 `icon.png`

这样可以保持手动发现和自动补扫的一致性。

---

## 设计五：设置开启后的即时补扫

### 触发时机

当用户在插件设置界面中把 `AutoDiscoverSaves` 从关闭切换为开启时，宿主应在保存设置后立即触发一次补扫。

该补扫复用与启动补扫完全相同的宿主入口：

- `PluginService.RunConfigAugmentationAsync(reason: SettingsEnabled)`

### 行为要求

即时补扫与启动补扫共享同一套核心规则：

- 同样只补已有配置
- 同样只扫同级 `saves`
- 同样做去重与冲突过滤
- 同样只在有新增时保存与提示

这样可以避免出现“两套规则逐渐漂移”的维护问题。

### 重复触发保护

设置层需要避免以下情况：

- 用户保存设置但值未变化时重复补扫
- 设置开启动作与启动补扫重叠时同时执行

因此，设置界面触发逻辑应只在：

- 旧值为 `false`
- 新值为 `true`

时触发即时补扫。

---

## 设计六：通知、日志与错误处理

### 用户提示策略

通知保持简洁保守：

- 启动或即时补扫只有在确实新增了存档时才提示
- 没有新增时完全静默

提示内容建议为类似：

- “MineRewind 已自动加入 2 个新存档”

如果未来多个插件都实现该能力，宿主可先按插件分别提示；本轮只需先满足 MineRewind 的清晰反馈即可。

### 日志策略

日志应覆盖：

- 启动补扫开始与结束
- 每个插件返回的新增数量
- 跳过项的原因，例如重复路径、显示名冲突、目标配置不存在
- 插件抛出的异常

这样可以在不打扰用户的前提下，保留足够的诊断信息。

### 异常隔离

异常处理策略如下：

- 单个插件执行失败，仅记录日志
- 不影响其他插件继续执行
- 不中断应用启动
- 不向用户弹错误对话框

这是因为本能力属于增强型自动化，而不是启动的关键路径。

---

## 涉及文件

本轮设计预计主要影响以下区域：

- `FolderRewind/Services/Plugins/IFolderRewindPlugin.cs`
- `FolderRewind/Services/Plugins/PluginService.cs`
- `FolderRewind/App.xaml.cs`
- `FolderRewind/Views/Settings/PluginsKnotLinkControl.xaml.cs`
- `FolderRewind-Plugin-Minecraft/MineRewind/MinecraftSavesPlugin.cs`
- `FolderRewind-Plugin-Minecraft/MineRewind/MinecraftSavesPlugin.Discovery.cs`
- `FolderRewind-Plugin-Minecraft/MineRewind/MinecraftSavesPlugin.Localization.cs`

若需要补充宿主侧结果模型，可新增插件相关模型文件，但应保持职责集中，避免把自动补扫模型散落到无关区域。

---

## 测试策略

### MineRewind 逻辑测试

至少覆盖以下场景：

1. 已有世界所在 `saves` 目录中出现新世界时，能正确补出候选
2. 一个配置中存在多个不同版本来源时，会分别扫描对应 `saves` 目录
3. 同一个 `saves` 目录被多个现有世界指向时，不会重复扫描
4. 已存在路径不会重复加入
5. 显示名冲突的世界不会自动加入

### 宿主补丁应用测试

至少覆盖以下场景：

1. 多个插件返回结果时能正确聚合
2. 单个插件异常不会中断整体补扫
3. 实际新增多个文件夹时只保存一次配置
4. 无新增时不会保存配置

### 设置触发测试

至少覆盖：

1. `AutoDiscoverSaves` 从 `false -> true` 时触发即时补扫
2. `true -> true` 或 `false -> false` 不触发
3. 即时补扫与启动补扫共享同一套去重规则

### 手动回归

手动回归重点包括：

1. 应用启动后已存在的 `Minecraft Saves` 配置能自动补入新世界
2. 不会跨版本误加其他目录中的世界
3. 启动时新增成功会出现简短提示
4. 没有新增时完全静默
5. MineRewind 设置中不再显示“热备份”开关
6. “自动发现存档”默认处于开启语义

---

## 风险与缓解

### 风险 1：宿主接口设计过宽，导致插件可以半绕过配置模型

缓解：

- 插件只返回显式补丁
- 宿主统一应用补丁
- 插件不直接拿 `CurrentConfig` 做写操作

### 风险 2：自动补扫把不该加入的世界加入配置

缓解：

- 扫描范围严格限制为“已有世界所属同级 `saves` 目录”
- 不跨版本重扫 `.minecraft`
- 显示名冲突直接跳过

### 风险 3：启动与设置开启动作重复触发，导致重复保存或重复提示

缓解：

- 宿主增加运行锁
- 设置开启仅在 `false -> true` 时触发
- 最终新增结果仍由宿主去重

### 风险 4：删除热备份设置后，旧代码路径仍依赖该开关

缓解：

- 实现中统一移除对 `EnableHotBackup` 的读取判断
- 通过热备份相关回归测试确认默认行为不变

---

## 成功标准

满足以下条件可视为本轮设计达成：

- 插件系统具备正式的“启动后自动补全配置”接口
- MineRewind 能在启动后自动把同级 `saves` 目录中新建的世界补入已有配置
- 不会自动新建 Minecraft 配置
- 不会跨版本误扫整个 `.minecraft`
- “自动发现存档”是默认开启的全局插件设置
- MineRewind 中不再暴露冗余的“热备份”开关
- 整个流程在用户感知上是轻量、安静且可预测的

---

## 变更历史

- 2026-07-08：初始设计文档

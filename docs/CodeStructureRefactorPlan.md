# FolderRewind 代码结构梳理与重构规划

## 1. 当前总体结构（按职责）

- `FolderRewind/`
  - `App.xaml.cs`、`MainWindow.xaml.cs`：应用生命周期、窗口与托盘控制
  - `Views/*.xaml.cs`：页面交互与 UI 行为
  - `Services/`：核心业务服务（配置、备份、历史、日志、主题、通知、启动项、插件等）
  - `Models/`：配置模型、历史模型、插件模型、日志模型
  - `Converters/`：XAML 绑定转换器
  - `Strings/`：多语言资源
- `FolderRewind-Plugin-Minecraft/MineRewind/`
  - `MinecraftSavesPlugin.cs`：Minecraft 插件全部逻辑（发现、配置创建、热备份、KnotLink、热还原）

---

## 2. 关键调用链

1. `App.OnLaunched` 初始化配置、主题、插件系统、托盘和自动化服务。  
2. `MainWindow` 负责标题栏、关闭行为（最小化到托盘/退出）和窗口资源释放。  
3. `ConfigService` 提供配置加载、迁移、保存、导入导出。  
4. `PluginService` 加载插件后，插件通过接口参与：
   - 配置类型识别与发现
   - 备份前/后钩子
   - 热键触发
   - KnotLink 指令处理
5. `MineRewind` 在备份时创建快照，在还原时与模组握手并驱动“保存退出-还原-重进”流程。

---

## 3. 已完成的结构整理（本次）

### 3.1 `#region` 分块补齐

- `FolderRewind/App.xaml.cs`
  - 全局状态与共享入口
  - 构造与全局异常捕获
  - 应用生命周期
  - 语言与窗口标题
  - 主窗口偏好应用
  - 托盘图标与命令

- `FolderRewind/MainWindow.xaml.cs`
  - 常量与状态
  - 构造与初始化
  - 窗口激活与最小尺寸
  - 关闭行为控制
  - 主题与标题栏

- `FolderRewind/Services/ConfigService.cs`
  - 常量与状态
  - 路径与默认值
  - 初始化与迁移
  - 持久化与重载
  - 配置文件访问
  - 导入导出
  - 规范化与内部工具

- `FolderRewind-Plugin-Minecraft/MineRewind/MinecraftSavesPlugin.cs`
  - 插件清单与初始化
  - 插件设置定义
  - 设置读取与宿主上下文
  - 模组握手与热还原（进一步细分：握手流程 / 热还原主流程 / 热还原辅助工具）

### 3.2 不改变行为的整理原则

- 仅调整结构分块与阅读顺序，不改业务路径与接口签名。
- 保持现有日志、异常吞吐策略和异步调用行为。

---

## 4. 当前不合理点（建议后续分阶段重构）

### 4.1 MineRewind 单文件过大（高优先级）

问题：`MinecraftSavesPlugin.cs` 超长，聚合了 5 类职责（配置发现、备份钩子、热键、KnotLink 协议、热还原状态机）。

建议拆分：

- `MinecraftSavesPlugin.cs`：仅保留接口实现与总调度
- `MinecraftDiscoveryService.cs`：目录扫描与配置创建
- `MinecraftSnapshotService.cs`：快照创建与清理、锁检测
- `MinecraftKnotLinkCoordinator.cs`：握手、事件命令、状态同步
- `MinecraftHotRestoreService.cs`：热还原主流程与等待策略
- `MinecraftPluginSettings.cs`：设置读取/默认值/键名常量

### 4.2 App 初始化职责过多（中优先级）

问题：`OnLaunched` 内串联太多系统初始化，异常域过大。

建议拆分：

- `InitializeCoreServices()`
- `InitializeMainWindow()`
- `InitializeBackgroundFeatures()`（插件、KnotLink、自动化、启动项同步）
- `InitializeTray()`

### 4.3 ConfigService 迁移逻辑过重（中优先级）

问题：初始化里包含大量节点兼容逻辑，后续维护成本高。

建议拆分：

- `ConfigMigrationService`：按版本迁移（`v1 -> v2`）
- `ConfigValidator`：空值修复与范围校验
- `ConfigRepository`：纯读写（序列化/反序列化/原子写）

---

## 5. 推荐重构顺序

1. **先拆插件**（最独立、收益最大，回归范围可控）。
2. 再拆 `ConfigService` 的迁移和仓储层。
3. 最后整理 `App` 启动编排（引入启动管线）。

---

## 6. 验收标准（后续重构阶段）

- 每个核心类不超过约 300~400 行。
- 每个类职责单一，命名与文件名一致。
- 启动与插件关键路径有最小化日志闭环（成功/失败/耗时）。
- 不改变现有配置文件格式与插件接口行为。

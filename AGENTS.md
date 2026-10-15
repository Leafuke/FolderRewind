# FolderRewind 仓库协作约定

本文件指导在本仓库中工作的编码代理。用户当前任务中的明确要求优先；子目录有更具体的 `AGENTS.md` 时，按其适用范围执行。

## 沟通与工作范围

- 根据现有上下文自主完成必要的检查和局部修改，不为常规、可逆的实现选择反复请求确认。无法从代码推断的关键需求再向用户澄清。
- 聚焦当前任务，避免顺手重构、无关格式化、批量升级依赖或清理他人的改动。
- 完成后说明改了什么、为何修改、实际进行了哪些验证，以及尚未验证的部分。

## Git 提交要求

**Git Message 采用 Conventional Commits 格式：**

```text
<type>(<scope>): <subject>
```

- 一个提交围绕一个可解释的目的；复杂改动在正文说明原因、兼容性影响及验证。破坏性变更须明确描述，不能用普通修复标题掩盖。
- 新建分支使用合适前缀，用户另有要求时遵从。
- 本文件不构成自动提交、推送或发布授权，是否执行遵循当前任务及既有用户授权。

## 仓库与模块边界

- `FolderRewind/`：WinUI 应用，主要关注 `Services`、`History`、`ViewModels`、`Views`、`Controls`。
- `FolderRewind.Plugin.Abstractions/`：公开、BCL-only 的插件契约，不引入应用、WinUI 或 Minecraft 类型。
- `FolderRewind.Plugin.Runtime/`：插件加载、激活、能力路由、调用生命周期和受控 Artifact 操作。
- `FolderRewind.Tests/`、`FolderRewind.Plugin.Abstractions.Tests/`、`FolderRewind.Plugin.Runtime.Tests/`：对应验证工程。
- 本地可能存在 `FolderRewind-Plugin-Minecraft/`、`FolderRewind-Site/`、插件目录或模板目录等独立仓库。存在于本目录下不代表属于 Host 的 Git 仓库；跨仓库修改前分别检查其工作区和约定。
- MineRewind 与 Host 独立构建，通过 Abstractions 包及精确的 `.frplugin` 产物集成；不要新增对 Host 应用项目或源码的插件编译依赖，也不要在插件包中捆绑 Host Abstractions DLL。
- `docs` 下部分目录受 `.gitignore` 限制。需要交付文档时检查是否被忽略，明确本地保存与纳入版本控制的区别；不要为一份文档放开整个忽略目录。

## 架构与数据安全

- **Host owns orchestration; plugins own domain semantics。** 配置持久化、Operation、History、事务、完整性、加密、Retention、Cloud 和目标目录修改由 Host 控制。
- 在获得用户许可的情况下，允许直接操作和使用用户现有FolderRewind软件相关的数据文件。

## UI、代码与本地化

- 遵循现有 C# 风格和 MVVM 组织方式。业务逻辑放在合适的 Service/领域层，ViewModel 管理呈现状态，`XAML.cs` 主要桥接 UI 事件。
- 用户可见文本同步维护 `FolderRewind/Strings/zh-CN/Resources.resw` 与 `FolderRewind/Strings/en-US/Resources.resw`，避免硬编码业务文案。
- 配置与通知优先复用 `ConfigService`、`NotificationService` 等已有入口，不另建并行实现。
- 耗时 I/O 不阻塞 UI 线程，遵守现有取消和资源释放模式。

## 构建与验证

本仓库当前使用 .NET 10，应用构建需要 Windows/WinUI 工具链。
从仓库根目录运行，缺少还原结果时先 restore：

```powershell
dotnet restore .\FolderRewind.slnx -p:Platform=x64
dotnet build .\FolderRewind.slnx -c Debug -p:Platform=x64 -p:GenerateAppxPackageOnBuild=false --no-restore
```

按影响范围选择测试工程，可先用 `--filter` 定向验证：

```powershell
dotnet test .\FolderRewind.Plugin.Abstractions.Tests\FolderRewind.Plugin.Abstractions.Tests.csproj -c Release
dotnet test .\FolderRewind.Plugin.Runtime.Tests\FolderRewind.Plugin.Runtime.Tests.csproj -c Release
dotnet test .\FolderRewind.Tests\FolderRewind.Tests.csproj -c Release
```

- 数据安全、公共契约和核心行为变化需要有意义的回归测试；避免只镜像实现的测试和无关测试扩张。
- 先完成相关验证，再根据失败或未消除的风险扩大范围；不要无理由反复运行全量测试。避免并行构建争用同一项目的 `bin/obj`。
- 与UI相关自动验收默认使用 winappcli （可使用 `winapp --help` 查阅使用说明），若用户要求人工验收则给出验收步骤即可。
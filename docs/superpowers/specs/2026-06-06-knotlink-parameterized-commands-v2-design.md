# KnotLink 参数化指令 v2 设计规格

**日期:** 2026-06-06
**范围:** FolderRewind KnotLink 新版参数化指令、会话识别、响应/事件格式、插件上下文广播 API、协议文档
**目标:** 在保持旧版位置参数指令兼容的前提下，为新版参数化指令提供可追踪、可并发区分、可解析的远程联动协议

---

## 1. 问题陈述

当前 KnotLink 远程指令已经支持新版参数化形式，例如：

```text
BACKUP -config_id=ID -folder="文件夹名称" -comment="注释"
BACKUP -current_save=true -comment=QuickSave
RESTORE -current_save=true -file=backup.7z -preserve_player_data=true
```

但协议缺少稳定的会话识别字段。FolderRewind 收到远程指令后，无法区分这条指令来自 MineRewind 模组、MineRewind 插件，还是其他外部工具；在同一来源并发发送多个备份/还原请求时，也无法可靠区分后续广播属于哪个请求。

这会导致接收端只能根据事件名或业务字段猜测流程归属，容易出现以下问题：

1. 模组和插件同时监听同一 KnotLink 事件时互相误触发。
2. 同一调用方并发请求时，后续 `backup_success` / `restore_failed` 等事件串线。
3. 直接响应和异步广播格式不统一，接收端难以稳定解析。
4. 事件字段由各处手动拼接，特殊字符、中文、`;`、`=` 等值可能破坏键值对解析。
5. 插件需要自己拼接事件字符串，难以保证和 Host 内置事件格式一致。

## 2. 设计目标

本次升级定义为 **KnotLink 参数化指令 v2 的会话元数据层**。

目标：

1. **区分来源** — 使用 `-from=xxx` 标识调用方或流程来源。
2. **区分单次请求** — 使用 `-request_id=xxx` 标识一次具体请求，避免并发串线。
3. **统一回填元数据** — 直接响应、Host 广播、插件广播都带回同一组 `from/request_id`。
4. **统一编码** — 新版响应和事件字段值统一 URL 编码，保证可解析。
5. **统一字段命名** — 新版协议统一使用 `folder=`，不再兼容 `world=`。
6. **暴露能力查询** — 调用方可以通过 `GET_CAPABILITIES` 判断 Host 支持哪些协议能力。
7. **统一生命周期事件** — 异步命令广播 `command_accepted`、`command_started`、`command_progress`、`command_completed`、`command_failed`。
8. **插件复用 Host 协议格式化能力** — `PluginHostContext` 提供上下文广播 API，插件不再手动拼接 `from/request_id`。

非目标：

- 不把整个 KnotLink 协议改成 JSON。
- 不废弃旧版位置参数指令。
- 不强制所有查询类命令带 `from/request_id`。
- 不在第一版实现完整幂等请求缓存；仅为未来基于 `from + request_id` 去重预留设计空间。

## 3. 协议概览

### 3.1 新版参数化命令示例

```text
BACKUP -from=minerewind.mod -request_id=abc123 -current_save=true -comment=QuickSave
RESTORE -from=minerewind.mod -request_id=abc124 -current_save=true -file=backup.7z -preserve_player_data=true
LIST_BACKUPS -from=minerewind.plugin -request_id=query001 -current_save=true
GET_CAPABILITIES -from=minerewind.mod -request_id=cap001
```

### 3.2 标准元数据字段

| 字段 | 作用 | 是否强制 |
|---|---|---|
| `from` | 调用方或流程来源，例如 `minerewind.mod`、`minerewind.plugin`、`external.tool` | 核心异步参数化命令强制 |
| `request_id` | 单次请求 ID；同一来源并发请求时用于区分事件归属 | 核心异步参数化命令强制 |

预留字段：

| 字段 | 作用 | 本次行为 |
|---|---|---|
| `reply_to` | 未来指定响应或事件路由目标 | 解析并保留，不强制使用 |
| `protocol_version` | 未来区分协议版本 | 解析并保留，不强制使用 |
| `flow` | 业务流程分类，例如 `hot_backup`、`hot_restore` | 解析并保留，不替代 `request_id` |

### 3.3 强制元数据的命令

以下参数化命令会产生状态变化或后续异步事件，必须包含 `from` 和 `request_id`：

```text
BACKUP
RESTORE
BACKUP_ALL
AUTO_BACKUP
STOP_AUTO_BACKUP
MARK_IMPORTANT
```

缺少元数据时返回结构化错误，例如：

```text
ERROR:message=Missing%20required%20conversation%20metadata%3A%20from%2C%20request_id
```

如果已经能解析出部分元数据，则带回已知字段：

```text
ERROR:from=minerewind.mod;message=Missing%20required%20conversation%20metadata%3A%20request_id
```

### 3.4 元数据可选的查询命令

以下查询类命令不强制元数据：

```text
LIST_CONFIGS
LIST_FOLDERS
LIST_BACKUPS
GET_CONFIG
GET_STATUS
PING
GET_CAPABILITIES
```

如果查询命令带了 `from/request_id`，响应和广播带回；如果没带，保持旧行为。

### 3.5 旧版位置参数兼容

旧版指令继续兼容，不强制元数据，不改变返回格式：

```text
BACKUP <config_id> <folder> [comment]
BACKUP_CURRENT
LIST_BACKUPS_CURRENT
RESTORE_CURRENT
```

旧版兼容只适用于位置参数指令。新版参数化命令进入 v2 校验和格式化流程。

## 4. 响应格式

### 4.1 新版参数化直接响应

带会话元数据的参数化命令直接响应采用键值对格式：

```text
OK:from=minerewind.mod;request_id=abc123;message=Backup%20started%20for%20folder%20%27MyWorld%27
ERROR:from=minerewind.mod;request_id=abc123;message=No%20active%20world
OK:from=minerewind.plugin;request_id=query001;data=backup1.7z%3Bbackup2.7z
```

规则：

1. 保留 `OK:` / `ERROR:` 前缀。
2. 前缀后使用 `key=value;key=value`。
3. 字段值统一使用 `Uri.EscapeDataString()` 编码。
4. 接收端必须对字段值执行 URL 解码。
5. 旧版位置参数命令返回格式不变。

### 4.2 响应字段

| 字段 | 说明 |
|---|---|
| `from` | 原样回填请求中的 `from` |
| `request_id` | 原样回填请求中的 `request_id` |
| `message` | 人类可读消息，URL 编码 |
| `data` | 查询数据或旧 payload，URL 编码 |
| `command` | 可选，返回对应命令名 |
| `protocol` | 能力查询使用 |
| `supports` | 能力查询使用 |
| `requires_metadata` | 能力查询使用 |

### 4.3 解析失败

如果命令字符串在解析阶段失败，例如引号未闭合，可能无法读取元数据。这类错误保持简单格式：

```text
ERROR:Unclosed quote
```

如果命令已解析成功，只是校验失败或执行失败，则使用结构化错误。

## 5. 广播事件格式

### 5.1 统一键值对事件

新版事件继续使用 KnotLink 现有分号分隔键值对格式：

```text
event=<事件名>;key=value;key=value
```

带会话元数据的流程中，所有 Host 与插件事件都必须附带：

```text
from=<来源>
request_id=<请求ID>
```

示例：

```text
event=command_accepted;from=minerewind.mod;request_id=abc123;command=BACKUP
event=backup_started;from=minerewind.mod;request_id=abc123;config=...;folder=MyWorld
event=backup_success;from=minerewind.mod;request_id=abc123;config=...;folder=MyWorld;file=backup.7z
event=backup_failed;from=minerewind.mod;request_id=abc123;config=...;folder=MyWorld;error=...
```

所有字段值同样 URL 编码。

### 5.2 `folder` 字段取代 `world`

新版参数化协议统一使用：

```text
folder=<文件夹名>
```

不再兼容：

```text
world=<世界名>
```

如果新版参数化命令检测到调用方传入 `world` 参数，直接返回错误并提醒升级协议：

```text
ERROR:from=x;request_id=y;message=Deprecated%20field%20%27world%27.%20Use%20%27folder%27%20instead.
```

旧版位置参数命令的历史广播不在本次强制改造范围内；但所有新版参数化路径和新文档只使用 `folder`。

## 6. 生命周期事件

异步命令统一广播生命周期事件，便于接收端判断流程状态：

```text
command_accepted
command_started
command_progress
command_completed
command_failed
```

建议语义：

| 事件 | 触发时机 |
|---|---|
| `command_accepted` | 命令通过解析和校验，准备进入处理流程 |
| `command_started` | 实际后台任务开始执行 |
| `command_progress` | 后台任务有可报告进度时发送 |
| `command_completed` | 后台任务成功完成 |
| `command_failed` | 后台任务失败或执行前失败 |

业务事件仍可保留，例如 `backup_started`、`backup_success`、`backup_failed`、`restore_started`、`restore_success`、`restore_failed`。调用方可以使用生命周期事件判断统一状态，也可以监听业务事件获取更细的业务信息。

示例：

```text
event=command_accepted;from=minerewind.mod;request_id=abc123;command=BACKUP
event=command_started;from=minerewind.mod;request_id=abc123;command=BACKUP
event=command_progress;from=minerewind.mod;request_id=abc123;command=BACKUP;progress=42
event=command_completed;from=minerewind.mod;request_id=abc123;command=BACKUP
```

## 7. 能力查询

新增命令：

```text
GET_CAPABILITIES
```

不带元数据时返回：

```text
OK:protocol=2;supports=from,request_id,encoded_kv,lifecycle_events,folder_field,plugin_context_broadcast;requires_metadata=BACKUP,RESTORE,BACKUP_ALL,AUTO_BACKUP,STOP_AUTO_BACKUP,MARK_IMPORTANT
```

带元数据时返回：

```text
OK:from=minerewind.mod;request_id=cap001;protocol=2;supports=from,request_id,encoded_kv,lifecycle_events,folder_field,plugin_context_broadcast;requires_metadata=BACKUP,RESTORE,BACKUP_ALL,AUTO_BACKUP,STOP_AUTO_BACKUP,MARK_IMPORTANT
```

调用方可先查询能力，再决定是否发送 v2 参数化命令。

## 8. 组件设计

### 8.1 `KnotLinkCommandMetadata`

职责：从 `KnotLinkCommandRequest` 读取协议元数据。

包含字段：

```text
From
RequestId
ReplyTo
ProtocolVersion
Flow
HasConversation
```

它只负责读取和规范化，不做业务校验。

### 8.2 `KnotLinkCommandContext`

职责：表示一次命令处理上下文。

包含字段：

```text
Request
Metadata
IsParameterized
```

内置命令处理器、插件处理器、后台任务、广播 helper 都应尽量传递该上下文。

### 8.3 `KnotLinkCommandValidator`

职责：集中校验新版参数化协议规则。

校验内容：

1. 核心异步参数化命令必须带 `from/request_id`。
2. 新版参数化请求不得使用 `world` 字段。
3. 可选校验 `from/request_id` 不能为空白字符串。
4. 可选校验 `request_id` 长度上限，避免过长 payload。

### 8.4 `KnotLinkProtocolFormatter`

职责：统一格式化响应和事件。

能力：

```text
FormatOk(context, fields)
FormatError(context, message)
FormatEvent(context, eventName, fields)
EncodeValue(value)
```

规则：

1. 自动回填 `from/request_id`。
2. 自动添加 `event=`。
3. 对所有字段值进行 URL 编码。
4. 保持字段顺序稳定：`event`、`from`、`request_id`、`command`、业务字段。

### 8.5 `KnotLinkService` 上下文广播 API

新增重载或辅助方法：

```csharp
BroadcastEvent(KnotLinkCommandContext context, string eventName, IReadOnlyDictionary<string, string?> fields)
BroadcastEventAsync(KnotLinkCommandContext context, string eventName, IReadOnlyDictionary<string, string?> fields)
```

旧 `BroadcastEvent(string eventData)` 保留，用于兼容旧事件或外部传入的原始事件。

### 8.6 `PluginHostContext` 上下文广播 API

为插件提供上下文广播能力，例如：

```csharp
BroadcastEvent(KnotLinkCommandContext context, string eventName, IReadOnlyDictionary<string, string?> fields)
BroadcastEventAsync(KnotLinkCommandContext context, string eventName, IReadOnlyDictionary<string, string?> fields)
```

MineRewind 等插件可以复用 Host 格式化逻辑，不再手动拼接：

```text
event=pre_hot_backup;plugin=minerewind;from=...;request_id=...;folder=...
```

## 9. 数据流

以 MineRewind 当前存档备份命令为例：

```text
BACKUP -from=minerewind.mod -request_id=abc123 -current_save=true -comment=QuickSave
```

流程：

1. `KnotLinkCommandParser` 解析成 `KnotLinkCommandRequest`。
2. `KnotLinkCommandMetadata` 读取 `from=minerewind.mod`、`request_id=abc123`。
3. `KnotLinkCommandContext` 包装请求和元数据。
4. `KnotLinkCommandValidator` 判断 `BACKUP` 是核心异步参数化命令，确认元数据齐全，且未使用 `world`。
5. `KnotLinkService` 广播：
   ```text
   event=command_accepted;from=minerewind.mod;request_id=abc123;command=BACKUP
   ```
6. 插件参数化处理优先执行；MineRewind 因为 `current_save=true` 接管。
7. MineRewind 启动后台热备任务并返回：
   ```text
   OK:from=minerewind.mod;request_id=abc123;message=Backup%20started%20for%20%27MyWorld%27
   ```
8. 后台任务广播：
   ```text
   event=command_started;from=minerewind.mod;request_id=abc123;command=BACKUP
   event=pre_hot_backup;from=minerewind.mod;request_id=abc123;plugin=minerewind;folder=MyWorld
   event=backup_started;from=minerewind.mod;request_id=abc123;config=...;folder=MyWorld
   event=backup_success;from=minerewind.mod;request_id=abc123;config=...;folder=MyWorld;file=backup.7z
   event=command_completed;from=minerewind.mod;request_id=abc123;command=BACKUP
   ```

## 10. 错误处理

### 10.1 解析失败

命令格式不可解析时返回旧式错误：

```text
ERROR:Unclosed quote
```

### 10.2 缺少必填元数据

核心异步参数化命令缺少元数据：

```text
ERROR:message=Missing%20required%20conversation%20metadata%3A%20from%2C%20request_id
```

部分元数据已知时带回：

```text
ERROR:from=minerewind.mod;message=Missing%20required%20conversation%20metadata%3A%20request_id
```

### 10.3 使用弃用字段

新版参数化请求使用 `world`：

```text
ERROR:from=minerewind.mod;request_id=abc123;message=Deprecated%20field%20%27world%27.%20Use%20%27folder%27%20instead.
```

### 10.4 执行前失败

例如找不到配置、找不到文件夹、找不到备份文件：

```text
ERROR:from=minerewind.mod;request_id=abc123;message=Folder%20not%20found%3A%20MyWorld
```

### 10.5 后台执行失败

直接响应已经返回 `OK:` 后，后台失败通过事件通知：

```text
event=command_failed;from=minerewind.mod;request_id=abc123;command=BACKUP;error=...
event=backup_failed;from=minerewind.mod;request_id=abc123;config=...;folder=MyWorld;error=...
```

## 11. 插件接入

MineRewind 参数化接管逻辑继续保留：

```text
BACKUP -current_save=true
LIST_BACKUPS -current_save=true
RESTORE -current_save=true
```

但核心异步新版用法必须带会话元数据：

```text
BACKUP -from=minerewind.mod -request_id=abc123 -current_save=true -comment=QuickSave
RESTORE -from=minerewind.mod -request_id=abc124 -current_save=true -file=backup.7z -preserve_player_data=true
```

插件处理参数化命令时应接收或可访问 `KnotLinkCommandContext`，并通过 `PluginHostContext` 的上下文广播 API 发送事件。

如果接口需要演进，应保持旧插件兼容。可选路径：

1. 保留现有 `IFolderRewindParameterizedKnotLinkCommandHandler`，通过 `KnotLinkCommandRequest` 读取元数据；Host 对插件返回值进行结构化包装。
2. 新增扩展接口或辅助 API，让插件能拿到 `KnotLinkCommandContext`；旧接口继续可用。

实现计划阶段应评估最小破坏方案，优先不要求旧插件重编译。

## 12. 接收端规则

文档必须明确要求接收端：

1. 发送核心异步参数化命令时必须附带 `from` 和唯一 `request_id`。
2. 只处理匹配自己 `from/request_id` 的响应和事件。
3. 对响应和事件字段值进行 URL 解码。
4. 把 OpenSocket 直接返回视为“请求已接收或执行前失败”，把后续广播视为实际执行状态。
5. 不再发送或依赖 `world` 字段；新版协议必须使用 `folder`。
6. 可先发送 `GET_CAPABILITIES` 判断 Host 是否支持 v2 能力。

## 13. 文档与本地化

需要同步更新：

1. `docs/PluginDevelopmentGuide.md` 的 KnotLink 事件协议、命令扩展、接收端规则。
2. 设置页 KnotLink 命令示例：`Strings/zh-CN/Resources.resw` 与 `Strings/en-US/Resources.resw`。
3. MineRewind 或插件文档中的远程命令示例。
4. 如果 FolderRewind-Site 文档已覆盖 KnotLink 协议，也应在后续网站文档任务中同步更新。

新增用户可见错误文本需要同时补充中英文资源。

## 14. 测试策略

### 14.1 解析与元数据测试

- `-from=a -request_id=b` 正确读取。
- `-request-id=b` 与 `-request_id=b` 等价。
- 中文、空格、`;`、`=` 等特殊值能编码和解码。
- 重复参数继续报错。

### 14.2 强制规则测试

- `BACKUP -config_id=... -folder=...` 缺少元数据时返回错误。
- `BACKUP -from=x -request_id=y -world=MyWorld` 返回弃用字段错误。
- `LIST_BACKUPS -config_id=... -folder=...` 缺少元数据时仍可执行。
- `LIST_BACKUPS -from=x -request_id=y ...` 返回和广播带回元数据。

### 14.3 响应格式测试

- 参数化成功返回 `OK:from=...;request_id=...;message=...`。
- 参数化错误返回 `ERROR:from=...;request_id=...;message=...`。
- 查询响应 `data` 字段 URL 编码。
- 旧位置参数命令返回格式不变。

### 14.4 广播测试

- `BACKUP` 成功路径：`command_accepted`、`command_started`、`backup_started`、`backup_success`、`command_completed` 都带同一 `from/request_id`。
- `BACKUP` 失败路径：`command_failed`、`backup_failed` 带同一 `from/request_id`。
- MineRewind `BACKUP -current_save=true` 接管后，插件自己的事件带同一元数据。
- 并发两个不同 `request_id` 的备份时，事件不会串线。

### 14.5 能力查询测试

- `GET_CAPABILITIES` 返回 `protocol=2` 和支持能力列表。
- `GET_CAPABILITIES -from=x -request_id=y` 带回元数据。

## 15. 实施顺序建议

1. 添加 `KnotLinkCommandMetadata`、`KnotLinkCommandContext`、`KnotLinkCommandValidator`、`KnotLinkProtocolFormatter`。
2. 为 `KnotLinkService` 增加上下文处理入口和上下文广播 API。
3. 对参数化命令接入强制元数据校验和 `world` 弃用校验。
4. 实现结构化 `OK:` / `ERROR:` 响应格式。
5. 实现 `GET_CAPABILITIES`。
6. 将内置参数化命令的广播迁移到 formatter。
7. 增加生命周期事件。
8. 为 `PluginHostContext` 增加上下文广播 API。
9. 迁移 MineRewind 参数化接管和插件事件广播。
10. 更新设置页示例、插件开发指南和相关文档。
11. 补充测试和手动验证路径。

## 16. 验收标准

1. 核心异步参数化命令缺少 `from/request_id` 时拒绝执行并返回明确错误。
2. 带 `from/request_id` 的参数化命令，直接响应和所有后续广播都带回同一元数据。
3. 新版参数化命令使用 `world` 字段时返回升级提示错误。
4. 新版响应和事件字段值统一 URL 编码，接收端可稳定解析。
5. `GET_CAPABILITIES` 可查询 v2 能力。
6. 插件可通过 Host 上下文 API 发送带会话元数据的事件。
7. 旧版位置参数指令仍可使用，返回格式不变。
8. 文档说明接收端必须按 `from/request_id` 过滤事件，并使用 `folder` 字段。

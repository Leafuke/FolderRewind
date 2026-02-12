# FolderRewind 插件开发指南

> **版本**：适用于 FolderRewind v1.2.0+  
> **最后更新**：2026-02-08

---

## 目录

1. [概述](#1-概述)
2. [快速开始](#2-快速开始)
3. [插件项目结构](#3-插件项目结构)
4. [核心接口详解](#4-核心接口详解)
   - [IFolderRewindPlugin — 基础插件接口](#41-ifolderrewindplugin--基础插件接口)
   - [IFolderRewindHotkeyProvider — 热键扩展接口](#42-ifolderrewindhotkeyprovider--热键扩展接口)
5. [manifest.json 清单文件](#5-manifestjson-清单文件)
6. [插件设置系统](#6-插件设置系统)
7. [备份流程钩子](#7-备份流程钩子)
   - [同步钩子 — OnBeforeBackupFolder / OnAfterBackupFolder](#71-同步钩子)
   - [异步钩子 — OnBeforeBackupFolderAsync](#72-异步钩子--onbeforebackupfolderasync)
8. [配置类型发现与创建](#8-配置类型发现与创建)
9. [完全接管备份/还原](#9-完全接管备份还原)
10. [KnotLink 互联系统](#10-knotlink-互联系统)
    - [什么是 KnotLink](#101-什么是-knotlink)
    - [通过 PluginHostContext 使用 KnotLink](#102-通过-pluginhostcontext-使用-knotlink)
    - [信号广播](#103-信号广播)
    - [请求/响应查询](#104-请求响应查询)
    - [信号订阅](#105-信号订阅)
    - [日志记录](#106-日志记录)
    - [KnotLink 事件协议](#107-knotlink-事件协议)
11. [宿主服务 API 参考](#11-宿主服务-api-参考)
12. [插件的加载与生命周期](#12-插件的加载与生命周期)
13. [打包与发布](#13-打包与发布)
14. [完整示例：从零开发一个插件](#14-完整示例从零开发一个插件)
15. [进阶：MineRewind 插件实战解析](#15-进阶minerewind-插件实战解析)
16. [常见问题 FAQ](#16-常见问题-faq)
17. [附录：数据模型参考](#17-附录数据模型参考)

---

## 1. 概述

FolderRewind 是一款基于 WinUI 3 的通用文件夹备份/还原工具。它的插件系统允许第三方开发者：

- **扩展配置管理方式** — 例如 Minecraft 插件可以自动扫描 `.minecraft` 下的存档并批量创建备份配置。
- **介入备份/还原流程** — 通过钩子函数在备份前后执行自定义逻辑（如热备份快照、KnotLink 信号通知）。
- **完全接管备份/还原** — 如果需要特殊的备份格式或逻辑，插件可以完全代替内置引擎。
- **注册全局/应用内热键** — 插件可以定义自己的快捷键，用户可在 FolderRewind 设置中自定义。
- **与 KnotLink 互联系统交互** — 通过 KnotLink 向外部程序（如游戏内模组）发送信号、接收命令。

插件运行在同一进程中（通过独立的 `AssemblyLoadContext` 隔离），使用 C# / .NET 8 开发。

---

## 2. 快速开始

### 2.1 环境准备

| 需求 | 版本 |
|------|------|
| .NET SDK | 8.0+ |
| 目标框架 | `net8.0-windows10.0.19041.0` |
| IDE | Visual Studio 2022 / VS Code + C# Dev Kit |

### 2.2 创建插件项目

```bash
# 1. 创建类库项目
dotnet new classlib -n MyPlugin --framework net8.0-windows10.0.19041.0

# 2. 添加对 FolderRewind 主项目的引用（仅编译时引用，不复制到输出）
cd MyPlugin
```

编辑 `.csproj` 文件：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <Platforms>x64;ANYCPU</Platforms>
    <!-- 重要：不生成依赖文件，因为插件由 Host 加载 -->
    <GenerateDependencyFile>false</GenerateDependencyFile>
  </PropertyGroup>
  
  <ItemGroup>
    <!-- 引用 FolderRewind 主项目（仅编译时，不复制到输出） -->
    <ProjectReference Include="..\..\FolderRewind\FolderRewind\FolderRewind.csproj">
      <Private>false</Private>
      <ExcludeAssets>runtime</ExcludeAssets>
    </ProjectReference>
  </ItemGroup>
</Project>
```

> **重要**：`Private=false` 和 `ExcludeAssets=runtime` 确保 FolderRewind 的 DLL 不会被复制到插件输出目录。插件在运行时会由 Host 的 `AssemblyLoadContext` 自动解析宿主程序集。

### 2.3 编写 manifest.json

在项目根目录创建 `manifest.json`（必须包含在输出目录中）：

```json
{
  "Id": "com.example.myplugin",
  "Name": "MyPlugin",
  "Version": "1.0.0",
  "Author": "Your Name",
  "Description": "A sample FolderRewind plugin.",
  "LocalizedName": {
    "en-US": "MyPlugin",
    "zh-CN": "我的插件"
  },
  "LocalizedDescription": {
    "en-US": "A sample FolderRewind plugin.",
    "zh-CN": "一个示例 FolderRewind 插件。"
  },
  "EntryAssembly": "MyPlugin.dll",
  "EntryType": "MyPlugin.MyPluginMain",
  "MinHostVersion": "1.1.0"
}
```

### 2.4 实现插件入口类

```csharp
using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MyPlugin
{
    public class MyPluginMain : IFolderRewindPlugin
    {
        public PluginInstallManifest Manifest { get; } = new()
        {
            Id = "com.example.myplugin",
            Name = "MyPlugin",
            Version = "1.0.0",
            Author = "Your Name",
            Description = "A sample FolderRewind plugin."
        };

        public IReadOnlyList<PluginSettingDefinition> GetSettingsDefinitions()
        {
            return new List<PluginSettingDefinition>();
        }

        public void Initialize(IReadOnlyDictionary<string, string> settingsValues)
        {
            // 初始化时读取设置
            LogService.LogInfo("MyPlugin 已初始化！", "MyPlugin");
        }

        public string? OnBeforeBackupFolder(BackupConfig config, ManagedFolder folder,
            IReadOnlyDictionary<string, string> settingsValues)
        {
            return null; // 不修改源路径
        }

        public void OnAfterBackupFolder(BackupConfig config, ManagedFolder folder,
            bool success, string? generatedArchiveFileName,
            IReadOnlyDictionary<string, string> settingsValues)
        {
            if (success)
            {
                LogService.LogInfo($"备份成功: {folder.DisplayName}", "MyPlugin");
            }
        }

        public IReadOnlyList<ManagedFolder> TryDiscoverManagedFolders(
            string selectedRootPath, IReadOnlyDictionary<string, string> settingsValues)
        {
            return new List<ManagedFolder>();
        }
    }
}
```

### 2.5 编译、打包与安装

```bash
# 编译
dotnet build -c Release

# 打包：将输出目录中的 DLL + manifest.json 打成 zip
# zip 文件内部结构必须为：
#   MyPlugin/
#     manifest.json
#     MyPlugin.dll
```

将 zip 文件通过 FolderRewind 的插件管理页面安装，或手动解压到：
```
%LOCALAPPDATA%\FolderRewind\plugins\MyPlugin\
```

---

## 3. 插件项目结构

一个典型的 FolderRewind 插件项目目录结构：

```
MyPlugin/
├── MyPlugin.csproj              # 项目文件
├── manifest.json                # 插件清单（必须）
├── MyPluginMain.cs              # 插件入口类（实现 IFolderRewindPlugin）
├── README.md                    # 说明文档
└── bin/
    └── Release/
        └── net8.0-windows10.0.19041.0/
            └── x64/
                ├── MyPlugin.dll      # 编译产物
                └── manifest.json     # 自动复制到输出目录
```

**打包目录结构**（zip 内部）：

```
MyPlugin.zip
└── MyPlugin/                    # 顶层文件夹名即插件目录名
    ├── manifest.json
    ├── MyPlugin.dll
    └── (其它依赖 DLL，如有)
```

> **注意**：zip 内的顶层必须恰好有一个文件夹，其内包含 `manifest.json`。

---

## 4. 核心接口详解

### 4.1 IFolderRewindPlugin — 基础插件接口

这是每个插件**必须实现**的核心接口。它定义了插件与 FolderRewind 宿主交互的全部契约。

```csharp
public interface IFolderRewindPlugin
{
    // ========== 基本信息 ==========

    /// <summary>插件基本信息（用于 UI 与日志）。</summary>
    PluginInstallManifest Manifest { get; }

    /// <summary>插件可选设置定义。</summary>
    IReadOnlyList<PluginSettingDefinition> GetSettingsDefinitions();

    /// <summary>由 Host 调用：在插件被启用时触发一次初始化。</summary>
    void Initialize(IReadOnlyDictionary<string, string> settingsValues);

    // ========== 备份钩子 ==========

    /// <summary>备份前钩子（同步）：返回新路径则替换源目录。</summary>
    string? OnBeforeBackupFolder(BackupConfig config, ManagedFolder folder,
        IReadOnlyDictionary<string, string> settingsValues);

    /// <summary>
    /// 备份前钩子（异步）：支持 KnotLink 交互。
    /// Host 优先调用此版本。默认实现委托给同步版本。
    /// </summary>
    Task<string?> OnBeforeBackupFolderAsync(BackupConfig config, ManagedFolder folder,
        IReadOnlyDictionary<string, string> settingsValues, PluginHostContext hostContext)
        => Task.FromResult(OnBeforeBackupFolder(config, folder, settingsValues));

    /// <summary>备份后钩子：清理快照/记录元数据等。</summary>
    void OnAfterBackupFolder(BackupConfig config, ManagedFolder folder,
        bool success, string? generatedArchiveFileName,
        IReadOnlyDictionary<string, string> settingsValues);

    // ========== 配置类型与发现 ==========

    IReadOnlyList<string> GetSupportedConfigTypes() => Array.Empty<string>();
    bool CanHandleConfigType(string configType) => false;
    IReadOnlyList<ManagedFolder> TryDiscoverManagedFolders(string selectedRootPath,
        IReadOnlyDictionary<string, string> settingsValues);
    PluginCreateConfigResult TryCreateConfigs(string selectedRootPath,
        IReadOnlyDictionary<string, string> settingsValues)
        => new PluginCreateConfigResult { Handled = false };

    // ========== 完全接管（可选） ==========

    bool WantsToHandleBackup(BackupConfig config) => false;
    bool WantsToHandleRestore(BackupConfig config) => false;
    Task<PluginBackupResult> PerformBackupAsync(...) => ...;
    Task<PluginRestoreResult> PerformRestoreAsync(...) => ...;
}
```

#### 调用时序图

```
Host 启用插件
  │
  ├─ Initialize(settingsValues)          ← 一次性初始化
  │
  ├─ 用户添加文件夹时:
  │    ├─ GetSupportedConfigTypes()
  │    ├─ CanHandleConfigType("...")
  │    ├─ TryDiscoverManagedFolders(rootPath, settings)
  │    └─ TryCreateConfigs(rootPath, settings)
  │
  └─ 每次备份:
       ├─ WantsToHandleBackup(config)?
       │   ├─ true  → PerformBackupAsync(...)
       │   └─ false → 内置引擎:
       │       ├─ OnBeforeBackupFolderAsync(config, folder, settings, ctx)
       │       │   └─ (此处插件可发送 KnotLink 信号、创建快照)
       │       ├─ 内置 7z 压缩...
       │       └─ OnAfterBackupFolder(config, folder, success, fileName, settings)
       │           └─ (此处插件可清理快照)
       └─ 还原流程类似
```

### 4.2 IFolderRewindHotkeyProvider — 热键扩展接口

**可选接口**。实现此接口的插件可以注册自定义热键。

```csharp
public interface IFolderRewindHotkeyProvider
{
    /// <summary>返回插件定义的热键列表。</summary>
    IReadOnlyList<PluginHotkeyDefinition> GetHotkeyDefinitions();

    /// <summary>热键被触发时调用。</summary>
    Task OnHotkeyInvokedAsync(
        string hotkeyId,
        bool isGlobalHotkey,
        IReadOnlyDictionary<string, string> settingsValues,
        PluginHostContext hostContext);
}
```

**PluginHotkeyDefinition 字段说明**：

| 字段 | 类型 | 说明 |
|------|------|------|
| `Id` | `string` | 热键唯一标识符（插件内唯一）|
| `DisplayName` | `string` | 在 UI 中显示的名称 |
| `Description` | `string?` | 可选描述 |
| `DefaultGesture` | `string?` | 默认手势，如 `"Alt+Ctrl+S"`；为空表示默认不绑定 |
| `IsGlobalHotkey` | `bool` | `true` = 全局热键（Win32 RegisterHotKey），`false` = 应用内快捷键 |

**示例：注册全局热键**

```csharp
public class MyPlugin : IFolderRewindPlugin, IFolderRewindHotkeyProvider
{
    // ... IFolderRewindPlugin 实现省略 ...

    public IReadOnlyList<PluginHotkeyDefinition> GetHotkeyDefinitions()
    {
        return new List<PluginHotkeyDefinition>
        {
            new()
            {
                Id = "quick_backup",
                DisplayName = "快速备份",
                Description = "一键触发当前活跃项目的备份",
                DefaultGesture = "Alt+Ctrl+B",
                IsGlobalHotkey = true
            }
        };
    }

    public async Task OnHotkeyInvokedAsync(
        string hotkeyId, bool isGlobalHotkey,
        IReadOnlyDictionary<string, string> settingsValues,
        PluginHostContext hostContext)
    {
        if (hotkeyId == "quick_backup")
        {
            hostContext.LogInfo("热键触发！开始备份...");
            // 你的备份逻辑...
        }
    }
}
```

> **注意**：Host 在注册热键时会自动给 ID 加上前缀 `plugin.{PluginId}.`，以避免冲突。例如插件定义的 `quick_backup` 在 Host 中实际注册为 `plugin.com.example.myplugin.quick_backup`。

---

## 5. manifest.json 清单文件

每个插件目录下必须包含一个 `manifest.json` 文件。Host 在扫描插件目录时会读取此文件。

### 完整字段说明

```json
{
  "Id": "com.folderrewind.minerewind",      // 必须：插件唯一标识符（建议反向域名）
  "Name": "MineRewind",                      // 必须：插件显示名称
  "Version": "1.2.0",                        // 必须：语义化版本号
  "Author": "Leafuke",                       // 必须：作者名
  "Description": "...",                       // 必须：英文描述

  "LocalizedName": {                          // 可选：多语言名称
    "en-US": "MineRewind",
    "zh-CN": "MineRewind"
  },
  "LocalizedDescription": {                   // 可选：多语言描述
    "en-US": "Enhanced Minecraft saves backup...",
    "zh-CN": "Minecraft 存档备份增强插件..."
  },

  "EntryAssembly": "MineRewind.dll",          // 必须：插件入口 DLL 文件名
  "EntryType": "MineRewind.MinecraftSavesPlugin", // 必须：入口类的完全限定名

  "MinHostVersion": "1.1.0",                  // 可选：最低宿主版本要求
  "Homepage": "https://github.com/...",        // 可选：主页链接
  "Repository": "Leafuke/FolderRewind-Plugin-Minecraft" // 可选：GitHub 仓库（用于自动更新）
}
```

### Id 命名规范

推荐使用反向域名格式：
- `com.folderrewind.minerewind`
- `com.example.myplugin`
- `io.github.username.pluginname`

### 多语言支持

`LocalizedName` 和 `LocalizedDescription` 使用 BCP-47 语言标签作为 key：
- `en-US` — 英语（美国）
- `zh-CN` — 简体中文
- `ja-JP` — 日语

Host 会根据当前系统语言自动选择最匹配的翻译。

---

## 6. 插件设置系统

插件可以定义自己的设置项，Host 会：
1. 根据定义自动渲染设置 UI
2. 持久化用户填写的值
3. 在调用插件方法时通过 `settingsValues` 字典回传

### 定义设置

```csharp
public IReadOnlyList<PluginSettingDefinition> GetSettingsDefinitions()
{
    return new List<PluginSettingDefinition>
    {
        // 布尔开关
        new()
        {
            Key = "EnableFeatureX",
            DisplayName = "启用功能 X",
            Description = "开启后将执行额外逻辑",
            Type = PluginSettingType.Boolean,
            DefaultValue = "true",
            IsRequired = false
        },
        // 字符串输入
        new()
        {
            Key = "ApiKey",
            DisplayName = "API 密钥",
            Description = "可选的外部服务密钥",
            Type = PluginSettingType.String,
            DefaultValue = "",
            IsRequired = false
        },
        // 整数输入
        new()
        {
            Key = "MaxRetries",
            DisplayName = "最大重试次数",
            Description = "操作失败时的自动重试次数",
            Type = PluginSettingType.Integer,
            DefaultValue = "3",
            IsRequired = false
        },
        // 路径选择
        new()
        {
            Key = "OutputPath",
            DisplayName = "输出路径",
            Description = "自定义输出目录",
            Type = PluginSettingType.Path,
            DefaultValue = "",
            IsRequired = false
        }
    };
}
```

### 设置类型 PluginSettingType

| 枚举值 | 说明 | UI 表现 |
|--------|------|---------|
| `String` (0) | 字符串 | 文本输入框 |
| `Boolean` (1) | 布尔值 | 开关 Toggle |
| `Integer` (2) | 整数 | 数字输入框 |
| `Path` (3) | 文件/目录路径 | 带浏览按钮的路径选择器 |

### 读取设置值

```csharp
public void Initialize(IReadOnlyDictionary<string, string> settingsValues)
{
    // 所有设置值都以 string 形式存储，需要自行转换
    _enableFeatureX = GetBool(settingsValues, "EnableFeatureX", true);
    _maxRetries = GetInt(settingsValues, "MaxRetries", 3);
    _outputPath = GetString(settingsValues, "OutputPath", string.Empty);
}

// 推荐的辅助方法
private static bool GetBool(IReadOnlyDictionary<string, string> s, string key, bool def)
{
    if (s.TryGetValue(key, out var v) && bool.TryParse(v, out var r)) return r;
    return def;
}

private static int GetInt(IReadOnlyDictionary<string, string> s, string key, int def)
{
    if (s.TryGetValue(key, out var v) && int.TryParse(v, out var r)) return r;
    return def;
}

private static string GetString(IReadOnlyDictionary<string, string> s, string key, string def)
{
    return s.TryGetValue(key, out var v) ? v ?? def : def;
}
```

---

## 7. 备份流程钩子

FolderRewind 在每次备份文件夹时会依次调用已启用插件的钩子方法。

### 7.1 同步钩子

#### OnBeforeBackupFolder

```csharp
string? OnBeforeBackupFolder(
    BackupConfig config,          // 当前备份配置
    ManagedFolder folder,         // 要备份的文件夹
    IReadOnlyDictionary<string, string> settingsValues  // 插件设置
);
```

**返回值**：
- `null` — 不修改源路径，使用原始 `folder.Path`
- 非空字符串 — 使用返回的路径作为备份源（常用于快照目录）

**使用场景**：
- 创建源文件夹的快照副本（避免文件锁定冲突）
- 临时修改源目录内容
- 记录备份前状态

#### OnAfterBackupFolder

```csharp
void OnAfterBackupFolder(
    BackupConfig config,
    ManagedFolder folder,
    bool success,                   // 备份是否成功
    string? generatedArchiveFileName, // 生成的归档文件名（无变更时可能为 null）
    IReadOnlyDictionary<string, string> settingsValues
);
```

**使用场景**：
- 清理快照目录
- 记录备份结果
- 触发后续操作（如通知）

### 7.2 异步钩子 — OnBeforeBackupFolderAsync

```csharp
Task<string?> OnBeforeBackupFolderAsync(
    BackupConfig config,
    ManagedFolder folder,
    IReadOnlyDictionary<string, string> settingsValues,
    PluginHostContext hostContext    // 宿主上下文，可访问 KnotLink 等服务
);
```

**这是 v1.2.0 新增的异步版本**。Host 会优先调用此方法。默认实现委托给同步版本：

```csharp
// 接口默认实现（无需 KnotLink 交互的插件可以不重写此方法）
Task<string?> OnBeforeBackupFolderAsync(...) 
    => Task.FromResult(OnBeforeBackupFolder(config, folder, settingsValues));
```

**何时需要重写此方法**：
- 需要在备份前通过 KnotLink 发送信号（如 `pre_hot_backup`）
- 需要等待外部系统确认（如游戏模组完成世界保存）
- 任何需要 `await` 的异步操作

**完整示例（参考 MineRewind 的实现）**：

```csharp
public async Task<string?> OnBeforeBackupFolderAsync(
    BackupConfig config, ManagedFolder folder,
    IReadOnlyDictionary<string, string> settingsValues,
    PluginHostContext hostContext)
{
    Initialize(settingsValues);

    if (!ShouldHandleThisConfig(config))
        return null;

    // 1. 通过 KnotLink 通知外部系统
    if (hostContext != null && hostContext.IsKnotLinkAvailable)
    {
        try
        {
            // 发送 pre_hot_backup 信号
            await hostContext.BroadcastEventAsync(
                $"event=pre_hot_backup;plugin=myplugin;world={folder.DisplayName}");

            // 等待外部系统完成操作
            await Task.Delay(500);
        }
        catch (Exception ex)
        {
            hostContext.LogWarning($"KnotLink 信号发送失败：{ex.Message}");
            // 通知失败不阻止备份
        }
    }

    // 2. 创建快照
    var snapshotPath = CreateMySnapshot(folder.Path);
    return snapshotPath;
}
```

---

## 8. 配置类型发现与创建

插件可以定义自己的"配置类型"，并提供智能的文件夹发现与配置创建逻辑。

### 8.1 配置类型注册

```csharp
// 返回此插件支持的配置类型
public IReadOnlyList<string> GetSupportedConfigTypes()
{
    return new[] { "My Custom Type" };
}

// 检查是否能处理指定类型
public bool CanHandleConfigType(string configType)
{
    return string.Equals(configType, "My Custom Type", StringComparison.OrdinalIgnoreCase);
}
```

### 8.2 文件夹发现

当用户选择一个根目录时，Host 会调用所有已启用插件的 `TryDiscoverManagedFolders()` 来自动发现可管理的文件夹。

```csharp
public IReadOnlyList<ManagedFolder> TryDiscoverManagedFolders(
    string selectedRootPath,
    IReadOnlyDictionary<string, string> settingsValues)
{
    var results = new List<ManagedFolder>();

    // 示例：扫描目录下所有包含 "project.json" 的子目录
    foreach (var dir in Directory.EnumerateDirectories(selectedRootPath))
    {
        if (File.Exists(Path.Combine(dir, "project.json")))
        {
            results.Add(new ManagedFolder
            {
                Path = dir,
                DisplayName = Path.GetFileName(dir),
                Description = "自动发现的项目目录",
                CoverImagePath = FindIcon(dir) // 可选：封面图片
            });
        }
    }

    return results;
}
```

### 8.3 批量创建配置

```csharp
public PluginCreateConfigResult TryCreateConfigs(
    string selectedRootPath,
    IReadOnlyDictionary<string, string> settingsValues)
{
    var configs = new List<BackupConfig>();

    // 创建一个新的备份配置
    var config = new BackupConfig
    {
        Name = "My Project Backup",
        ConfigType = "My Custom Type",
        IconGlyph = "\uE8A5", // Segoe Fluent Icons 字符
    };

    // 添加发现的文件夹
    foreach (var folder in DiscoverFolders(selectedRootPath))
    {
        config.SourceFolders.Add(folder);
    }

    // 设置扩展属性
    config.ExtendedProperties["PluginVersion"] = "1.0.0";

    configs.Add(config);

    return new PluginCreateConfigResult
    {
        Handled = true,
        CreatedConfigs = configs,
        Message = $"已创建 {configs.Count} 个备份配置"
    };
}
```

---

## 9. 完全接管备份/还原

如果内置的 7z 备份引擎不满足需求，插件可以完全接管备份和/或还原流程。

### 声明接管意图

```csharp
public bool WantsToHandleBackup(BackupConfig config)
{
    // 只接管自己类型的配置
    return CanHandleConfigType(config.ConfigType);
}

public bool WantsToHandleRestore(BackupConfig config)
{
    return CanHandleConfigType(config.ConfigType);
}
```

### 实现自定义备份

```csharp
public async Task<PluginBackupResult> PerformBackupAsync(
    BackupConfig config,
    ManagedFolder folder,
    string comment,
    IReadOnlyDictionary<string, string> settingsValues,
    Action<double, string>? progressCallback = null)
{
    try
    {
        progressCallback?.Invoke(0, "开始自定义备份...");

        // 你的备份逻辑
        var backupDir = Path.Combine(config.DestinationPath, folder.DisplayName);
        Directory.CreateDirectory(backupDir);

        var fileName = $"{folder.DisplayName}_{DateTime.Now:yyyyMMdd_HHmmss}.bak";
        var filePath = Path.Combine(backupDir, fileName);

        // ... 执行备份 ...

        progressCallback?.Invoke(100, "备份完成");

        return new PluginBackupResult
        {
            Success = true,
            GeneratedFileName = fileName,
            Message = "自定义备份成功"
        };
    }
    catch (Exception ex)
    {
        return new PluginBackupResult
        {
            Success = false,
            Message = $"备份失败: {ex.Message}"
        };
    }
}
```

### 实现自定义还原

```csharp
public async Task<PluginRestoreResult> PerformRestoreAsync(
    BackupConfig config,
    ManagedFolder folder,
    string archiveFileName,
    IReadOnlyDictionary<string, string> settingsValues,
    Action<double, string>? progressCallback = null)
{
    try
    {
        progressCallback?.Invoke(0, "开始还原...");

        // 你的还原逻辑
        // ...

        return new PluginRestoreResult
        {
            Success = true,
            Message = "还原成功"
        };
    }
    catch (Exception ex)
    {
        return new PluginRestoreResult
        {
            Success = false,
            Message = $"还原失败: {ex.Message}"
        };
    }
}
```

---

## 10. KnotLink 互联系统

### 10.1 什么是 KnotLink

KnotLink 是 FolderRewind 内置的进程间通信（IPC）系统，基于 TCP 协议。它允许 FolderRewind 与外部程序（如游戏内模组）进行实时通信。

KnotLink 由外部的 KnotLink 服务端程序提供路由功能，使用以下端口：

| 功能 | 端口 | 说明 |
|------|------|------|
| Signal 发送 | 6370 | 广播事件给订阅者 |
| Signal 订阅 | 6372 | 接收特定频道的广播 |
| OpenSocket 查询 | 6376 | 向远端发送请求并等待回复 |
| OpenSocket 响应 | 6378 | 接收查询请求并发送回复 |

**通信模式**：

1. **Signal（信号广播）** — 一对多、fire-and-forget。发送方广播事件，所有订阅该频道的接收方都会收到。
2. **OpenSocket（请求/响应）** — 一对一、同步。发送方发送查询并等待回复。

### 10.2 通过 PluginHostContext 使用 KnotLink

插件通过 `PluginHostContext` 对象访问 KnotLink 系统。此对象在以下场景中由 Host 自动创建并传入：

- `OnBeforeBackupFolderAsync(config, folder, settings, hostContext)`
- `OnHotkeyInvokedAsync(hotkeyId, isGlobal, settings, hostContext)`

#### PluginHostContext API 一览

```csharp
public sealed class PluginHostContext
{
    // ===== 基本信息 =====
    string PluginId { get; }
    string PluginName { get; }

    // ===== KnotLink 状态 =====
    bool IsKnotLinkAvailable { get; }       // KnotLink 是否可用（已启用且已初始化）
    bool IsKnotLinkSenderReady { get; }     // 信号发送器是否就绪
    bool IsKnotLinkResponserReady { get; }  // 命令响应器是否就绪

    // ===== KnotLink 信号广播 =====
    void BroadcastEvent(string eventData);              // fire-and-forget 广播
    Task BroadcastEventAsync(string eventData);         // 可 await 广播

    // ===== KnotLink 请求/响应 =====
    Task<string> QueryKnotLinkAsync(string question, int timeoutMs = 5000);

    // ===== KnotLink 信号订阅 =====
    IDisposable? SubscribeSignal(string signalId, Func<string, Task> onSignal);

    // ===== KnotLink 命令发送 =====
    void SendKnotLinkCommand(string message);

    // ===== 日志 =====
    void LogInfo(string message);
    void LogWarning(string message);
    void LogError(string message, Exception? ex = null);
}
```

### 10.3 信号广播

信号广播是最常用的 KnotLink 功能。适用于通知性质的事件。

```csharp
// 方式 1：fire-and-forget（不等待发送完成）
hostContext.BroadcastEvent("event=my_event;key=value");

// 方式 2：异步（可等待发送完成）
await hostContext.BroadcastEventAsync("event=my_event;key=value");
```

**典型使用场景**：

```csharp
// 备份前通知游戏模组保存世界
await hostContext.BroadcastEventAsync("event=pre_hot_backup;");

// 备份成功后通知
hostContext.BroadcastEvent($"event=backup_success;world={worldName}");

// 热键触发通知
hostContext.BroadcastEvent("event=hotkey_triggered;action=backup");
```

### 10.4 请求/响应查询

通过 OpenSocket 向远端发送查询并等待回复。

```csharp
try
{
    // 向游戏模组查询当前世界信息（5 秒超时）
    var response = await hostContext.QueryKnotLinkAsync("GET_WORLD_INFO", 5000);

    if (response.StartsWith("OK:"))
    {
        var worldInfo = response.Substring(3);
        hostContext.LogInfo($"当前世界: {worldInfo}");
    }
}
catch (Exception ex)
{
    hostContext.LogWarning($"查询失败: {ex.Message}");
}
```

### 10.5 信号订阅

订阅特定频道，实时接收远端发送的广播信号。

```csharp
// 订阅信号频道
var subscription = hostContext.SubscribeSignal("0x00000020", async data =>
{
    // 每当该频道有新信号到达时触发
    hostContext.LogInfo($"收到信号: {data}");

    // 解析信号内容
    if (data.Contains("event=game_session_start"))
    {
        hostContext.LogInfo("检测到游戏启动！");
    }
});

// 订阅返回 IDisposable，调用 Dispose() 停止接收
// subscription?.Dispose();
```

> **注意**：`SubscribeSignal` 返回的 `IDisposable` 即 `SignalSubscriber` 对象。当不再需要时务必调用 `Dispose()` 释放资源。如果 KnotLink 不可用，返回 `null`。

### 10.6 日志记录

`PluginHostContext` 提供了便捷的日志方法，会自动附带插件名称前缀：

```csharp
hostContext.LogInfo("这是一条信息日志");          // [PluginName] 这是一条信息日志
hostContext.LogWarning("这是一条警告");            // [PluginName] 这是一条警告
hostContext.LogError("出错了", exception);          // [PluginName] 出错了 (含异常详情)
```

也可以直接使用 `LogService`：

```csharp
using FolderRewind.Services;

LogService.LogInfo("消息内容", "MyPlugin");
LogService.LogWarning("警告内容", "MyPlugin");
LogService.LogError("错误内容", "MyPlugin", ex);
```

### 10.7 KnotLink 事件协议

FolderRewind 与 MineBackup 共享同一套事件协议（保持兼容性）。事件使用**分号分隔的键值对**格式。

#### 系统事件（Host 自动发送）

| 事件 | Payload | 触发时机 |
|------|---------|---------|
| `app_startup` | `event=app_startup;version=1.2.0` | FolderRewind 启动 |
| `backup_started` | `event=backup_started;config={id};world={name}` | 备份开始 |
| `backup_success` | `event=backup_success;config={id};world={name};file={file}` | 备份成功 |
| `backup_failed` | `event=backup_failed;config={id};world={name};error={msg}` | 备份失败 |
| `restore_success` | `event=restore_success;config={id};world={name}` | 还原成功 |
| `restore_failed` | `event=restore_failed;config={id};world={name};error={msg}` | 还原失败 |
| `config_changed` | `event=config_changed;config={id};key={k};value={v}` | 配置变更 |
| `auto_backup_started` | `event=auto_backup_started;config={id};folder={name};interval={min}` | 自动备份启动 |
| `auto_backup_stopped` | `event=auto_backup_stopped;config={id};folder={name}` | 自动备份停止 |

#### 插件事件（由插件发送）

| 事件 | Payload | 说明 |
|------|---------|------|
| `pre_hot_backup` | `event=pre_hot_backup;plugin={id};world={name}` | 热备份前通知模组保存世界 |
| `hotkey_backup_triggered` | `event=hotkey_backup_triggered;plugin={id};...` | 热键触发备份 |
| 自定义事件 | `event=my_event;key=value` | 插件可发送任意自定义事件 |

#### 事件格式规范

```
event=<事件名>;[key1=value1;][key2=value2;]...
```

- 使用 `;` 分隔键值对
- 使用 `=` 分隔键和值
- `event` 字段为必须项
- 值中含特殊字符时使用 `Uri.EscapeDataString()` 编码

### 10.8 扩展 KnotLink 指令（插件定义新互联事件）

FolderRewind 作为 KnotLink 的 OpenSocket **响应器**时，会接收远端发送的指令字符串（例如 `BACKUP 0 myWorld`）。

内置指令（`LIST_CONFIGS` / `BACKUP` / `SEND` 等）由 Host 自己处理；如果收到**未知指令**，Host 会按顺序询问所有“已启用且已加载”的插件：是否愿意处理该指令。

插件侧通过实现可选接口 `IFolderRewindKnotLinkCommandHandler` 来扩展指令集：

```csharp
using FolderRewind.Services.Plugins;

public class MyPlugin : IFolderRewindPlugin, IFolderRewindKnotLinkCommandHandler
{
    public IReadOnlyList<PluginKnotLinkCommandDefinition> GetKnotLinkCommandDefinitions()
        => new[]
        {
            new PluginKnotLinkCommandDefinition
            {
                Command = "HELLO",
                Description = "Example command"
            }
        };

    public Task<string?> TryHandleKnotLinkCommandAsync(
        string command,
        string args,
        string rawCommand,
        IReadOnlyDictionary<string, string> settingsValues,
        PluginHostContext hostContext)
    {
        if (!string.Equals(command, "HELLO", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<string?>(null); // 不处理

        hostContext.BroadcastEvent("event=plugin_hello;plugin=myplugin");
        return Task.FromResult<string?>("OK:Hello from plugin");
    }
}
```

**返回值约定**：

- 返回 `null`：插件不处理该指令（Host 会继续询问下一个插件，或最终返回 Unknown command）。
- 返回字符串：表示“已处理”，该字符串会作为 OpenSocket 响应返回给远端。
  - 推荐以 `OK:` 或 `ERROR:` 开头，保持与内置指令一致。

**性能注意**：OpenSocket 是“请求/响应”模式。不要在 `TryHandleKnotLinkCommandAsync` 里做长时间阻塞操作（例如完整备份）。
推荐启动后台任务（`Task.Run`）并立即返回 `OK:`。

#### 示例：MineRewind 处理 `BACKUP_CURRENT`

MineRewind 插件实现了 `BACKUP_CURRENT` 指令：收到后检测“当前正在运行（文件被占用）”的 Minecraft 存档，并触发一次热备份（复用热键备份逻辑）。

远端调用示例：

```
BACKUP_CURRENT
```

典型返回：

```
OK:Backup started for 'MyWorld'
```

---

## 11. 宿主服务 API 参考

插件可以直接使用以下 FolderRewind 宿主服务（通过 `using FolderRewind.Services;`）：

### LogService — 日志服务

```csharp
using FolderRewind.Services;

LogService.LogInfo("信息", "PluginName");
LogService.LogWarning("警告", "PluginName");
LogService.LogError("错误", "PluginName", exception);
LogService.Log("自定义日志消息");
```

### ConfigService — 配置服务

```csharp
using FolderRewind.Services;

// 获取当前配置
var appConfig = ConfigService.CurrentConfig;

// 获取所有备份配置
var backupConfigs = ConfigService.CurrentConfig?.BackupConfigs;

// 保存配置更改
ConfigService.Save();
```

### BackupService — 备份服务

```csharp
using FolderRewind.Services;

// 备份单个文件夹
await BackupService.BackupFolderAsync(config, folder, "备份注释");

// 备份整个配置下的所有文件夹
await BackupService.BackupConfigAsync(config);
```

### I18n — 国际化服务

```csharp
using FolderRewind.Services;

// 获取翻译字符串
var text = I18n.GetString("ResourceKey");

// 格式化翻译字符串
var formatted = I18n.Format("ResourceKey_WithParam", param1, param2);
```

> **提示**：插件可以在 FolderRewind 的 `Strings/zh-CN/Resources.resw` 和 `Strings/en-US/Resources.resw` 中添加自己的本地化字符串，以 `{PluginName}_` 为前缀避免冲突。

### KnotLinkService — KnotLink 互联服务

```csharp
using FolderRewind.Services;

// 检查 KnotLink 状态
bool available = KnotLinkService.IsEnabled && KnotLinkService.IsInitialized;

// 广播事件
KnotLinkService.BroadcastEvent("event=my_event;");

// 异步广播
await KnotLinkService.BroadcastEventAsync("event=my_event;");

// 订阅信号
var sub = KnotLinkService.SubscribeSignal("0x00000020", async data =>
{
    Console.WriteLine($"收到: {data}");
});
```

> **推荐**：优先使用 `PluginHostContext` 提供的方法来访问 KnotLink，而非直接调用 `KnotLinkService`。因为 `PluginHostContext` 提供了更好的封装和未来兼容性保证。

---

## 12. 插件的加载与生命周期

### 加载流程

```
FolderRewind 启动
  │
  ├─ PluginService.Initialize()
  │   ├─ 创建 %LOCALAPPDATA%\FolderRewind\plugins 目录
  │   ├─ 清理上次标记为 .pending_delete 的插件
  │   ├─ RefreshInstalledList() — 扫描所有已安装插件的 manifest
  │   ├─ TryLoadEnabledPlugins() — 对每个已启用的插件:
  │   │   ├─ 检查 MinHostVersion 兼容性
  │   │   ├─ 创建 PluginLoadContext（独立加载上下文）
  │   │   ├─ 反射加载 EntryAssembly → 创建 EntryType 实例
  │   │   └─ 调用 plugin.Initialize(settingsValues)
  │   ├─ TryRegisterPluginHotkeysForEnabled() — 注册热键
  │   └─ 异步检查插件更新
  │
  └─ 运行时:
      ├─ 每次备份 → 调用钩子方法
      ├─ 热键触发 → OnHotkeyInvokedAsync
      ├─ 用户禁用插件 → PluginLoadContext.Unload()
      └─ 用户卸载插件 → Unload + 删除文件
```

### PluginLoadContext 隔离

每个插件在独立的 `AssemblyLoadContext` 中加载（`isCollectible: true`），这意味着：

1. **依赖隔离** — 插件的第三方 DLL 不会与宿主或其他插件冲突
2. **可卸载** — 禁用插件时可以释放程序集
3. **优先解析** — 插件目录下的 DLL 优先于全局程序集

```csharp
// 内部实现（供参考，开发者无需关心）
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly string _pluginDir;

    public PluginLoadContext(string pluginDir) : base(isCollectible: true)
    {
        _pluginDir = pluginDir;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // 优先从插件目录解析依赖
        var candidate = Path.Combine(_pluginDir, assemblyName.Name + ".dll");
        if (File.Exists(candidate))
            return LoadFromAssemblyPath(candidate);
        return null; // 回退到默认加载上下文
    }
}
```

### 生命周期事件

| 事件 | 说明 |
|------|------|
| `Initialize()` | 插件首次启用时调用（可能在每次宿主启动时） |
| 备份钩子 | 每次备份操作时调用 |
| 热键回调 | 用户按下热键时调用 |
| 卸载 | 用户禁用/卸载插件时，AssemblyLoadContext 被 Unload |

> **注意**：`Initialize()` 可能在每次 FolderRewind 启动或用户重新启用插件时被调用。确保此方法是幂等的。

---

## 13. 打包与发布

### 13.1 打包规范

插件以 `.zip` 格式分发。zip 内部结构：

```
MyPlugin.zip
└── MyPlugin/                     # 必须有且仅有一个顶层目录
    ├── manifest.json             # 必须
    ├── MyPlugin.dll              # 必须：EntryAssembly 指定的 DLL
    ├── SomeDependency.dll        # 可选：插件的第三方依赖
    └── assets/                   # 可选：插件资源文件
        └── icon.png
```

### 13.2 安装目录

插件安装后位于：
```
%LOCALAPPDATA%\FolderRewind\plugins\MyPlugin\
```

### 13.3 通过 GitHub Releases 发布

如果你在 `manifest.json` 中配置了 `Repository` 字段，FolderRewind 可以：

1. 自动检查 GitHub Release 中的新版本
2. 在插件管理页面显示更新提示
3. 用户一键下载更新

**发布步骤**：
1. 在 GitHub 创建 Release
2. 上传 zip 文件作为 Release Asset
3. zip 文件名建议包含版本号：`MyPlugin_v1.0.0.zip`

### 13.4 通过插件商店安装

FolderRewind 支持从配置的 GitHub 仓库作为插件商店源。用户可以在"插件商店"页面浏览和安装：

1. 商店管理员在指定 GitHub 仓库创建 Release
2. FolderRewind 从该仓库获取 Release Assets 列表
3. 用户选择想要的插件进行安装

---

## 14. 完整示例：从零开发一个插件

下面以一个"Unity 项目备份插件"为例，演示完整的开发流程。

### 14.1 创建项目

```xml
<!-- UnityBackup.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <Platforms>x64;ANYCPU</Platforms>
    <GenerateDependencyFile>false</GenerateDependencyFile>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\FolderRewind\FolderRewind\FolderRewind.csproj">
      <Private>false</Private>
      <ExcludeAssets>runtime</ExcludeAssets>
    </ProjectReference>
  </ItemGroup>
  <!-- 确保 manifest.json 被复制到输出目录 -->
  <ItemGroup>
    <None Update="manifest.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>
</Project>
```

### 14.2 manifest.json

```json
{
  "Id": "com.example.unitybackup",
  "Name": "UnityBackup",
  "Version": "1.0.0",
  "Author": "Developer",
  "Description": "Auto-discover and backup Unity projects",
  "LocalizedName": {
    "en-US": "UnityBackup",
    "zh-CN": "Unity 项目备份"
  },
  "LocalizedDescription": {
    "en-US": "Automatically discover and backup Unity projects with smart Library exclusion.",
    "zh-CN": "自动发现 Unity 项目并智能排除 Library 目录进行备份。"
  },
  "EntryAssembly": "UnityBackup.dll",
  "EntryType": "UnityBackup.UnityBackupPlugin",
  "MinHostVersion": "1.1.0",
  "Repository": "your-username/UnityBackup-Plugin"
}
```

### 14.3 插件实现

```csharp
using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace UnityBackup
{
    public class UnityBackupPlugin : IFolderRewindPlugin, IFolderRewindHotkeyProvider
    {
        private const string ConfigTypeName = "Unity Project";
        private const string ExcludeLibraryKey = "ExcludeLibrary";
        private const string ExcludeTempKey = "ExcludeTemp";

        private bool _excludeLibrary = true;
        private bool _excludeTemp = true;

        #region 基本信息

        public PluginInstallManifest Manifest { get; } = new()
        {
            Id = "com.example.unitybackup",
            Name = "UnityBackup",
            Version = "1.0.0",
            Author = "Developer",
            Description = "Auto-discover and backup Unity projects"
        };

        public IReadOnlyList<PluginSettingDefinition> GetSettingsDefinitions()
        {
            return new List<PluginSettingDefinition>
            {
                new()
                {
                    Key = ExcludeLibraryKey,
                    DisplayName = "排除 Library 目录",
                    Description = "Unity Library 目录含缓存数据，通常无需备份",
                    Type = PluginSettingType.Boolean,
                    DefaultValue = "true"
                },
                new()
                {
                    Key = ExcludeTempKey,
                    DisplayName = "排除 Temp 目录",
                    Description = "Unity Temp 目录是临时文件",
                    Type = PluginSettingType.Boolean,
                    DefaultValue = "true"
                }
            };
        }

        public void Initialize(IReadOnlyDictionary<string, string> settingsValues)
        {
            _excludeLibrary = GetBool(settingsValues, ExcludeLibraryKey, true);
            _excludeTemp = GetBool(settingsValues, ExcludeTempKey, true);
            LogService.LogInfo("UnityBackup 插件已初始化", "UnityBackup");
        }

        #endregion

        #region 配置类型与发现

        public IReadOnlyList<string> GetSupportedConfigTypes()
            => new[] { ConfigTypeName };

        public bool CanHandleConfigType(string configType)
            => string.Equals(configType, ConfigTypeName, StringComparison.OrdinalIgnoreCase);

        public IReadOnlyList<ManagedFolder> TryDiscoverManagedFolders(
            string selectedRootPath, IReadOnlyDictionary<string, string> settingsValues)
        {
            var results = new List<ManagedFolder>();
            if (!Directory.Exists(selectedRootPath)) return results;

            // 检查选中目录本身是否是 Unity 项目
            if (IsUnityProject(selectedRootPath))
            {
                results.Add(CreateManagedFolder(selectedRootPath));
                return results;
            }

            // 扫描子目录
            foreach (var dir in Directory.EnumerateDirectories(selectedRootPath))
            {
                if (IsUnityProject(dir))
                {
                    results.Add(CreateManagedFolder(dir));
                }
            }

            return results;
        }

        public PluginCreateConfigResult TryCreateConfigs(
            string selectedRootPath, IReadOnlyDictionary<string, string> settingsValues)
        {
            var folders = TryDiscoverManagedFolders(selectedRootPath, settingsValues);
            if (folders.Count == 0)
                return new PluginCreateConfigResult { Handled = false };

            var config = new BackupConfig
            {
                Name = "Unity Projects",
                ConfigType = ConfigTypeName,
                IconGlyph = "\uE8FC", // 代码图标
            };

            foreach (var folder in folders)
                config.SourceFolders.Add(folder);

            config.ExtendedProperties["Plugin"] = Manifest.Id;

            return new PluginCreateConfigResult
            {
                Handled = true,
                CreatedConfigs = new[] { config },
                Message = $"已发现 {folders.Count} 个 Unity 项目"
            };
        }

        #endregion

        #region 备份钩子

        public string? OnBeforeBackupFolder(BackupConfig config, ManagedFolder folder,
            IReadOnlyDictionary<string, string> settingsValues)
        {
            Initialize(settingsValues);

            // Unity 项目无需创建快照，直接备份
            // 但可以在这里添加黑名单规则
            if (_excludeLibrary)
            {
                LogService.LogInfo(
                    $"[UnityBackup] 备份将排除 Library 目录: {folder.DisplayName}",
                    "UnityBackup");
            }

            return null;
        }

        public async Task<string?> OnBeforeBackupFolderAsync(
            BackupConfig config, ManagedFolder folder,
            IReadOnlyDictionary<string, string> settingsValues,
            PluginHostContext hostContext)
        {
            Initialize(settingsValues);

            // 通过 KnotLink 通知（如有需要）
            if (hostContext?.IsKnotLinkAvailable == true)
            {
                await hostContext.BroadcastEventAsync(
                    $"event=unity_backup_start;project={Uri.EscapeDataString(folder.DisplayName ?? "")}");
            }

            return null;
        }

        public void OnAfterBackupFolder(BackupConfig config, ManagedFolder folder,
            bool success, string? generatedArchiveFileName,
            IReadOnlyDictionary<string, string> settingsValues)
        {
            if (success)
            {
                LogService.LogInfo(
                    $"[UnityBackup] Unity 项目备份成功: {folder.DisplayName}",
                    "UnityBackup");
            }
        }

        #endregion

        #region 热键

        public IReadOnlyList<PluginHotkeyDefinition> GetHotkeyDefinitions()
        {
            return new List<PluginHotkeyDefinition>
            {
                new()
                {
                    Id = "backup_all_unity",
                    DisplayName = "备份所有 Unity 项目",
                    DefaultGesture = "Alt+Ctrl+U",
                    IsGlobalHotkey = true
                }
            };
        }

        public async Task OnHotkeyInvokedAsync(
            string hotkeyId, bool isGlobalHotkey,
            IReadOnlyDictionary<string, string> settingsValues,
            PluginHostContext hostContext)
        {
            if (hotkeyId != "backup_all_unity") return;

            var configs = ConfigService.CurrentConfig?.BackupConfigs;
            if (configs == null) return;

            foreach (var config in configs)
            {
                if (!CanHandleConfigType(config.ConfigType)) continue;

                hostContext?.LogInfo($"热键触发：备份配置 {config.Name}");
                await BackupService.BackupConfigAsync(config);
            }
        }

        #endregion

        #region 辅助方法

        private static bool IsUnityProject(string path)
        {
            // Unity 项目特征：包含 Assets 和 ProjectSettings 目录
            return Directory.Exists(Path.Combine(path, "Assets"))
                && Directory.Exists(Path.Combine(path, "ProjectSettings"));
        }

        private static ManagedFolder CreateManagedFolder(string path)
        {
            return new ManagedFolder
            {
                Path = path,
                DisplayName = Path.GetFileName(path),
                Description = "Unity 项目"
            };
        }

        private static bool GetBool(IReadOnlyDictionary<string, string> s, string key, bool def)
        {
            if (s.TryGetValue(key, out var v) && bool.TryParse(v, out var r)) return r;
            return def;
        }

        #endregion
    }
}
```

---

## 15. 进阶：MineRewind 插件实战解析

MineRewind 是 FolderRewind 的官方 Minecraft 存档备份增强插件。它是插件开发的最佳实战参考。

### 15.1 核心功能

1. **热备份（Hot Backup）** — 游戏运行时通过 `xcopy` 创建快照，避免 `session.lock` 文件锁冲突
2. **KnotLink 联动** — 备份前通过 KnotLink 发送 `pre_hot_backup` 信号，通知游戏内模组执行 `/save-all` 保存世界
3. **批量发现** — 自动扫描 `.minecraft` 目录，支持标准模式和版本隔离模式（HMCL/PCL2）
4. **全局热键** — `Alt+Ctrl+S` 一键备份当前正在游玩的世界

### 15.2 KnotLink 联动流程

MineRewind 的 `OnBeforeBackupFolderAsync` 实现了完整的 KnotLink 联动流程：

```
用户触发备份
  │
  ├─ [异步钩子] OnBeforeBackupFolderAsync
  │   │
  │   ├─ 检查 KnotLink 是否可用
  │   │   └─ hostContext.IsKnotLinkAvailable
  │   │
  │   ├─ 发送 pre_hot_backup 信号
  │   │   └─ await hostContext.BroadcastEventAsync("event=pre_hot_backup;...")
  │   │
  │   ├─ 等待模组完成世界保存
  │   │   └─ await Task.Delay(snapshotDelayMs)
  │   │
  │   └─ 创建 xcopy 快照
  │       └─ CreateSnapshot(folder.Path) → 返回快照路径
  │
  ├─ [Host 内置引擎] 从快照目录执行 7z 压缩
  │
  └─ [备份后钩子] OnAfterBackupFolder
      └─ 清理快照目录
```

这与 MineBackup（FolderRewind 的前身）的 `DoBackup()` 流程完全一致：

```cpp
// MineBackup C++ 版本（供参考）
if (config.hotBackup || IsFileLocked(sourcePath + L"/level.dat")) {
    BroadcastEvent("event=pre_hot_backup;");
    wstring snapshotPath = CreateWorldSnapshot(sourcePath, config.snapshotPath);
    // ... 备份快照 ...
}
```

### 15.3 热键备份流程

```csharp
public async Task OnHotkeyInvokedAsync(string hotkeyId, ..., PluginHostContext hostContext)
{
    // 1. 查找当前正在运行的世界
    var active = TryFindOccupiedWorld();
    if (active == null) return;

    // 2. 通过 KnotLink 广播备份触发事件
    hostContext?.BroadcastEvent("event=hotkey_backup_triggered;...");

    // 3. 发送 pre_hot_backup 信号
    if (hostContext?.IsKnotLinkAvailable == true)
    {
        await hostContext.BroadcastEventAsync("event=pre_hot_backup;...");
        await Task.Delay(_snapshotDelayMs);
    }

    // 4. 触发备份（走 Host 的标准流程，OnBeforeBackupFolderAsync 会自动创建快照）
    await BackupService.BackupFolderAsync(config, folder, "[热键] MineRewind");
}
```

### 15.4 世界占用检测

```csharp
private static bool IsWorldOccupied(string worldPath)
{
    // Java 版：检测 session.lock 文件锁
    var sessionLock = Path.Combine(worldPath, "session.lock");
    if (File.Exists(sessionLock) && IsFileLocked(sessionLock)) return true;

    // 基岩版：检测 db/ 目录下的文件锁
    var dbDir = Path.Combine(worldPath, "db");
    if (Directory.Exists(dbDir))
    {
        foreach (var entry in Directory.EnumerateFiles(dbDir))
        {
            if (IsFileLocked(entry)) return true;
        }
    }

    return false;
}
```

---

## 16. 常见问题 FAQ

### Q: 插件可以引用第三方 NuGet 包吗？

**可以**。在 `.csproj` 中正常添加 `PackageReference`。编译后将依赖 DLL 一并打入 zip 即可。`PluginLoadContext` 会优先从插件目录解析依赖。

但请注意：不要引用与 FolderRewind 宿主已加载的程序集冲突的版本。

### Q: 插件能访问 UI 线程吗？

不建议直接操作 UI。如果确实需要，可以使用 `DispatcherQueue`，但这增加了复杂性和崩溃风险。推荐通过返回值和回调与 Host 交互。

### Q: 如何调试插件？

1. 将 FolderRewind 和插件项目放在同一个解决方案中
2. 在 FolderRewind 的输出目录下创建 `plugins\YourPlugin\` 目录
3. 将编译后的插件 DLL 和 manifest.json 复制到该目录
4. 以调试模式启动 FolderRewind
5. 在插件代码中设置断点

或者使用 `LogService` 输出调试信息。

### Q: 插件设置在哪里存储？

Host 将所有插件设置持久化在 FolderRewind 的 `config.json` 中，路径为 `GlobalSettings.Plugins.PluginSettings[PluginId]`。插件不需要自己管理配置文件。

### Q: Initialize 什么时候会被调用？

- FolderRewind 启动时（如果插件已启用）
- 用户通过设置页面启用插件时
- 用户修改插件设置后自动重新初始化

`Initialize()` 应是幂等的，可被多次安全调用。

### Q: 如何让 FolderRewind 的黑名单规则也应用到我的快照路径？

当 `OnBeforeBackupFolder` 返回快照路径后，Host 会使用该路径作为备份源。Host 的黑名单系统（`BackupService.IsBlacklisted`）会自动将排除规则从原始路径映射到快照路径。

### Q: KnotLink 不可用时怎么办？

始终检查 `hostContext.IsKnotLinkAvailable` 再使用 KnotLink 功能。KnotLink 需要外部 KnotLink 服务端正在运行且用户在设置中启用了此功能。KnotLink 不可用时，插件应正常降级运行。

---

## 17. 附录：数据模型参考

### BackupConfig

| 属性 | 类型 | 说明 |
|------|------|------|
| `Id` | `string` | 配置唯一标识符（GUID） |
| `Name` | `string` | 配置显示名称 |
| `DestinationPath` | `string` | 备份目标路径 |
| `ConfigType` | `string` | 配置类型（默认 `"Default"`，插件可自定义） |
| `IconGlyph` | `string` | Segoe Fluent Icons 字符 |
| `SourceFolders` | `ObservableCollection<ManagedFolder>` | 源文件夹列表 |
| `Archive` | `ArchiveSettings` | 归档设置（格式、压缩级别、保留数量等） |
| `Automation` | `AutomationSettings` | 自动化设置 |
| `Filters` | `FilterSettings` | 过滤器设置（黑名单/白名单） |
| `ExtendedProperties` | `Dictionary<string, string>` | 扩展属性（供插件使用） |

### ManagedFolder

| 属性 | 类型 | 说明 |
|------|------|------|
| `Path` | `string` | 文件夹完整路径 |
| `DisplayName` | `string` | 显示名称 |
| `Description` | `string` | 描述文本 |
| `IsFavorite` | `bool` | 是否收藏 |
| `LastBackupTime` | `string` | 上次备份时间 |
| `StatusText` | `string` | 运行时状态文本（不序列化） |
| `CoverImagePath` | `string` | 封面图片路径（可选） |

### PluginInstallManifest

| 属性 | 类型 | 说明 |
|------|------|------|
| `Id` | `string` | 插件唯一标识符 |
| `Name` | `string` | 插件名称 |
| `Version` | `string` | 版本号 |
| `Author` | `string` | 作者 |
| `Description` | `string` | 描述 |
| `LocalizedName` | `Dictionary<string, string>?` | 多语言名称 |
| `LocalizedDescription` | `Dictionary<string, string>?` | 多语言描述 |
| `EntryAssembly` | `string` | 入口 DLL 文件名 |
| `EntryType` | `string` | 入口类全名 |
| `MinHostVersion` | `string?` | 最低宿主版本 |
| `Homepage` | `string?` | 主页链接 |
| `Repository` | `string?` | GitHub 仓库 |

### PluginSettingDefinition

| 属性 | 类型 | 说明 |
|------|------|------|
| `Key` | `string` | 设置键名 |
| `DisplayName` | `string` | 显示名称 |
| `Description` | `string?` | 描述 |
| `Type` | `PluginSettingType` | 类型（String/Boolean/Integer/Path） |
| `DefaultValue` | `string?` | 默认值 |
| `IsRequired` | `bool` | 是否必填 |

### PluginBackupResult / PluginRestoreResult

```csharp
public class PluginBackupResult
{
    bool Success { get; set; }
    string? GeneratedFileName { get; set; }  // 用于历史记录
    string? Message { get; set; }
}

public class PluginRestoreResult
{
    bool Success { get; set; }
    string? Message { get; set; }
}

public class PluginCreateConfigResult
{
    bool Handled { get; set; }
    IReadOnlyList<BackupConfig>? CreatedConfigs { get; set; }
    string? Message { get; set; }
}
```

---

## 版权声明

FolderRewind 及其插件系统遵循 GPL-3.0 许可证。使用本文档中的代码示例开发插件无需额外授权。

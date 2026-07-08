# ConfigSettingsDialog UI 重影问题 & CPU 线程数设置问题修复设计

**日期**：2026-06-11
**状态**：已批准
**作者**：Claude

---

## 问题描述

### 问题1：ConfigSettingsDialog UI 重影问题

**现象**：
1. 首次打开配置设置界面
2. 点击"保存"或"取消"关闭
3. 再次打开，发现"常规"页面和上次关闭时的页面产生重叠"重影"
4. 例如：关闭时是"备份"设置页，再次打开时会发现"常规"和"备份"两个页面的重叠状态
5. 上方栏目显示激活的是"备份"页面，需要再次点击"备份"页面，再点其他页面，重影问题才会消失

**根因**：
- `ConfigSettingsDialog` 使用单例模式（`_instance`）
- 当用户切换到非"常规"选项卡（如"备份"）后关闭对话框，`Rebind` 方法被调用
- `Rebind` 方法重置了 `_tabLoaded` 和 `_currentTabContent`，并将 ConfigSelectorBar 设置为第一个选项卡
- 但没有重置之前加载的 ScrollViewer（如 BackupTabScrollViewer）的可见性状态
- 导致 GeneralTabScrollViewer 和之前的 ScrollViewer 同时可见，产生"重影"

---

### 问题2：CPU 线程数设置问题

**现象**：
- 当前 CPU 线程数的设置仅作用于备份时的 7z 命令
- "安全删除"步骤进行压缩包的解压、合并、再压缩过程仍然占用过多的资源
- 即使用户限制了 CPU 线程数，解压操作仍使用所有可用线程

**根因**：
- `CreateArchiveFromDirectorySync` 方法正确应用了 CPU 线程限制（第35-43行）
- 但 `ExtractArchiveToDirectorySync` 方法没有应用 CPU 线程限制（缺少 `-mmt` 参数）
- 这导致安全删除过程中的解压操作使用所有可用线程

---

## 解决方案

### 问题1：ConfigSettingsDialog UI 重影问题

**方案**：在 Rebind 中重置所有 ScrollViewer 可见性

**修改文件**：`FolderRewind\Views\ConfigSettingsDialog.xaml.cs`

**修改位置**：`Rebind` 方法（第732-782行）

**修改内容**：
在 `_tabLoaded.Clear()` 之后，添加代码重置所有 ScrollViewer 的可见性为 Collapsed：

```csharp
// 重置所有 tab ScrollViewer 的可见性
GeneralTabScrollViewer.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
BackupTabScrollViewer.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
RestoreTabScrollViewer.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
AutomationTabScrollViewer.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
CloudTabScrollViewer.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
FilterTabScrollViewer.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
```

然后 `LoadTabContent("General")` 会将 GeneralTabScrollViewer 设为 Visible。

**优点**：
- 简单直接，只需修改 `Rebind` 方法
- 不影响其他逻辑

---

### 问题2：CPU 线程数设置问题

**方案**：修改 ExtractArchiveToDirectorySync 添加 CPU 线程参数

**修改文件**：`FolderRewind\Services\BackupService.Archive.cs`

**修改1**：修改 `ExtractArchiveToDirectorySync` 方法签名（第19行）

从：
```csharp
private static bool ExtractArchiveToDirectorySync(string sevenZipExe, string archivePath, string targetDir, string? password, bool runAtLowPriority = false)
```

改为：
```csharp
private static bool ExtractArchiveToDirectorySync(string sevenZipExe, string archivePath, string targetDir, string? password, int cpuThreads = 0, bool runAtLowPriority = false)
```

**修改2**：在方法内部添加 CPU 线程参数（第21-26行）

在构建 `extractArgs` 时添加 `-mmt` 参数：

```csharp
string extractArgs = $"x \"{archivePath}\" -o\"{targetDir}\" -y -aoa";
if (!string.IsNullOrWhiteSpace(password))
{
    extractArgs += $" -p\"{password}\"";
}

// 添加 CPU 线程限制
int normalizedThreads = NormalizeCpuThreadCount(cpuThreads);
if (normalizedThreads > 0)
{
    extractArgs += $" -mmt{normalizedThreads}";
}
else
{
    extractArgs += " -mmt";
}
```

**修改3**：更新 `TrySafeDeleteArchive` 中的调用（第397、404行）

从：
```csharp
ExtractArchiveToDirectorySync(sevenZipExe, fileToDelete.FullName, mergeDir, safeDeletePassword, archiveSettings.RunCompressionAtLowPriority)
```

改为：
```csharp
ExtractArchiveToDirectorySync(sevenZipExe, fileToDelete.FullName, mergeDir, safeDeletePassword, archiveSettings.CpuThreads, archiveSettings.RunCompressionAtLowPriority)
```

**优点**：
- 保持一致性，解压和压缩都应用 CPU 线程限制
- 修改范围小

---

## 测试计划

### 问题1测试步骤

1. 打开 FolderRewind 应用
2. 点击配置设置按钮，打开 ConfigSettingsDialog
3. 切换到"备份"选项卡
4. 点击"保存"或"取消"关闭对话框
5. 再次打开配置设置对话框
6. 验证：应该只显示"常规"页面，没有重影
7. 切换到其他选项卡，验证切换正常

### 问题2测试步骤

1. 打开 FolderRewind 应用
2. 创建一个备份配置，设置 CPU 线程数为 2
3. 执行备份，创建多个增量备份
4. 删除一个中间的增量备份（触发安全删除）
5. 打开任务管理器，观察 7z 进程的 CPU 使用率
6. 验证：解压操作应该使用限制的线程数（约 2 个核心）

---

## 相关文件

- `FolderRewind\Views\ConfigSettingsDialog.xaml` - 对话框 XAML 定义
- `FolderRewind\Views\ConfigSettingsDialog.xaml.cs` - 对话框代码
- `FolderRewind\Services\BackupService.Archive.cs` - 7z 归档操作
- `FolderRewind\Models\BackupModels.cs` - ArchiveSettings 模型定义

---

## 变更历史

- 2026-06-11：初始设计文档

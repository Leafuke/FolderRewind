# ConfigSettingsDialog UI 重影问题 & CPU 线程数设置问题修复实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复配置设置对话框 UI 重影问题和安全删除操作 CPU 线程数限制问题

**Architecture:** 两个独立的 bug 修复，分别修改 ConfigSettingsDialog 的 Rebind 方法和 BackupService.Archive 的 ExtractArchiveToDirectorySync 方法

**Tech Stack:** C# / WinUI 3 / .NET 10

---

## 文件结构

### 问题1：ConfigSettingsDialog UI 重影问题
- **Modify:** `FolderRewind\Views\ConfigSettingsDialog.xaml.cs` (第732-782行)
  - 修改 `Rebind` 方法，重置所有 ScrollViewer 可见性

### 问题2：CPU 线程数设置问题
- **Modify:** `FolderRewind\Services\BackupService.Archive.cs` (第19-27行)
  - 修改 `ExtractArchiveToDirectorySync` 方法签名和实现
- **Modify:** `FolderRewind\Services\BackupService.Pruning.cs` (第397、404行)
  - 更新 `TrySafeDeleteArchive` 中的调用

---

### Task 1: 修复 ConfigSettingsDialog UI 重影问题

**Files:**
- Modify: `FolderRewind\Views\ConfigSettingsDialog.xaml.cs:732-782`

- [ ] **Step 1: 在 Rebind 方法中添加 ScrollViewer 可见性重置代码**

在 `_tabLoaded.Clear()` 之后，添加以下代码：

```csharp
// Reset tab state
_tabLoaded.Clear();
_currentTabContent = null;

// 重置所有 tab ScrollViewer 的可见性，防止重影
GeneralTabScrollViewer.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
BackupTabScrollViewer.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
RestoreTabScrollViewer.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
AutomationTabScrollViewer.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
CloudTabScrollViewer.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
FilterTabScrollViewer.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
```

- [ ] **Step 2: 验证修改**

打开 FolderRewind 应用，执行以下测试步骤：
1. 点击配置设置按钮，打开 ConfigSettingsDialog
2. 切换到"备份"选项卡
3. 点击"保存"或"取消"关闭对话框
4. 再次打开配置设置对话框
5. 验证：应该只显示"常规"页面，没有重影
6. 切换到其他选项卡，验证切换正常

- [ ] **Step 3: Commit**

```bash
cd D:\Programs\FolderRewind
git add FolderRewind/Views/ConfigSettingsDialog.xaml.cs
git commit -m "fix(config): reset ScrollViewer visibility in Rebind to prevent ghosting"
```

---

### Task 2: 修复 CPU 线程数设置问题

**Files:**
- Modify: `FolderRewind\Services\BackupService.Archive.cs:19-27`
- Modify: `FolderRewind\Services\BackupService.Pruning.cs:397,404`

- [ ] **Step 1: 修改 ExtractArchiveToDirectorySync 方法签名**

将方法签名从：
```csharp
private static bool ExtractArchiveToDirectorySync(string sevenZipExe, string archivePath, string targetDir, string? password, bool runAtLowPriority = false)
```

改为：
```csharp
private static bool ExtractArchiveToDirectorySync(string sevenZipExe, string archivePath, string targetDir, string? password, int cpuThreads = 0, bool runAtLowPriority = false)
```

- [ ] **Step 2: 在方法内部添加 CPU 线程参数**

将方法实现从：
```csharp
string extractArgs = $"x \"{archivePath}\" -o\"{targetDir}\" -y -aoa";
if (!string.IsNullOrWhiteSpace(password))
{
    extractArgs += $" -p\"{password}\"";
}
return RunSevenZipProcessSync(sevenZipExe, extractArgs, runAtLowPriority: runAtLowPriority);
```

改为：
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

return RunSevenZipProcessSync(sevenZipExe, extractArgs, runAtLowPriority: runAtLowPriority);
```

- [ ] **Step 3: 更新 TrySafeDeleteArchive 中的调用**

将第397行从：
```csharp
if (!ExtractArchiveToDirectorySync(sevenZipExe, fileToDelete.FullName, mergeDir, safeDeletePassword, archiveSettings.RunCompressionAtLowPriority))
```

改为：
```csharp
if (!ExtractArchiveToDirectorySync(sevenZipExe, fileToDelete.FullName, mergeDir, safeDeletePassword, archiveSettings.CpuThreads, archiveSettings.RunCompressionAtLowPriority))
```

将第404行从：
```csharp
if (!ExtractArchiveToDirectorySync(sevenZipExe, nextFile.FullName, mergeDir, safeDeletePassword, archiveSettings.RunCompressionAtLowPriority))
```

改为：
```csharp
if (!ExtractArchiveToDirectorySync(sevenZipExe, nextFile.FullName, mergeDir, safeDeletePassword, archiveSettings.CpuThreads, archiveSettings.RunCompressionAtLowPriority))
```

- [ ] **Step 4: 验证修改**

打开 FolderRewind 应用，执行以下测试步骤：
1. 创建一个备份配置，设置 CPU 线程数为 2
2. 执行备份，创建多个增量备份
3. 删除一个中间的增量备份（触发安全删除）
4. 打开任务管理器，观察 7z 进程的 CPU 使用率
5. 验证：解压操作应该使用限制的线程数（约 2 个核心）

- [ ] **Step 5: Commit**

```bash
cd D:\Programs\FolderRewind
git add FolderRewind/Services/BackupService.Archive.cs FolderRewind/Services/BackupService.Pruning.cs
git commit -m "fix(backup): apply CPU thread limit to extract operations in safe delete"
```

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

## 变更历史

- 2026-06-11：初始实现计划

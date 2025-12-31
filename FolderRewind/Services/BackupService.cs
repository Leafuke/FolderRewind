using FolderRewind.Models;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public static class BackupService
    {
        public static ObservableCollection<BackupTask> ActiveTasks { get; } = new();

        private static DispatcherQueue? UiQueue => App._window?.DispatcherQueue;

        private static Task RunOnUIAsync(Action action)
        {
            var queue = UiQueue;
            if (queue == null || queue.HasThreadAccess)
            {
                action();
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource<object?>();

            if (!queue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    tcs.SetResult(null);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }))
            {
                action();
                tcs.TrySetResult(null);
            }

            return tcs.Task;
        }

        /// <summary>
        /// 备份配置下的所有文件夹
        /// </summary>
        public static async Task BackupConfigAsync(BackupConfig config)
        {
            if (config == null) return;
            Log($"=== 开始执行配置任务: {config.Name} ===");

            foreach (var folder in config.SourceFolders)
            {
                await BackupFolderAsync(config, folder);
            }

            Log($"=== 任务结束 ===");
        }

        /// <summary>
        /// 备份单个文件夹
        /// </summary>
        public static async Task BackupFolderAsync(BackupConfig config, ManagedFolder folder, string comment = "")
        {
            if (config == null || folder == null) return;

            // 1. 创建任务对象并确保在 UI 线程添加到集合
            var task = new BackupTask
            {
                FolderName = folder.DisplayName,
                Status = "准备中...",
                Progress = 0
            };

            await RunOnUIAsync(() => ActiveTasks.Insert(0, task));

            string sourcePath = folder.Path;
            // 按照要求：备份路径 = 用户设置的目标路径 \ 文件夹名
            string backupSubDir = Path.Combine(config.DestinationPath, folder.DisplayName);
            string metadataDir = Path.Combine(config.DestinationPath, "_metadata", folder.DisplayName);

            if (!Directory.Exists(sourcePath))
            {
                Log($"[错误] 源文件夹不存在: {sourcePath}");
                await RunOnUIAsync(() =>
                {
                    folder.StatusText = "源不存在";
                    task.Status = "失败";
                    task.IsCompleted = true;
                });
                return;
            }

            if (string.IsNullOrEmpty(config.DestinationPath))
            {
                Log("错误：未设置备份目标路径！");
                await RunOnUIAsync(() =>
                {
                    folder.StatusText = "未设置目标";
                    task.Status = "失败";
                    task.IsCompleted = true;
                });
                return;
            }

            // 创建必要的目录
            if (!Directory.Exists(backupSubDir)) Directory.CreateDirectory(backupSubDir);
            if (!Directory.Exists(metadataDir)) Directory.CreateDirectory(metadataDir);

            Log($"[处理中] {folder.DisplayName}");
            await RunOnUIAsync(() => folder.StatusText = "正在备份...");

            bool success = false;
            string generatedFileName = null;
            try
            {

                await RunOnUIAsync(() =>
                {
                    task.Status = "正在处理...";
                    folder.StatusText = "备份中...";
                });

                // 调用核心逻辑，传入 task 以便更新进度

                // 根据模式分发逻辑
                switch (config.Archive.Mode)
                {
                    case BackupMode.Incremental:
                    {
                        var res = await DoSmartBackupAsync(sourcePath, backupSubDir, metadataDir, folder.DisplayName, config, comment);
                        success = res.Success;
                        generatedFileName = res.FileName;
                        break;
                    }
                    case BackupMode.Overwrite:
                    {
                        var res = await DoOverwriteBackupAsync(sourcePath, backupSubDir, folder.DisplayName, config, comment);
                        success = res.Success;
                        generatedFileName = res.FileName;
                        break;
                    }
                    case BackupMode.Full:
                    default:
                    {
                        var res = await DoFullBackupAsync(sourcePath, backupSubDir, metadataDir, folder.DisplayName, config, comment);
                        success = res.Success;
                        generatedFileName = res.FileName;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[异常] {ex.Message}");
                success = false;
            }

            if (success)
            {
                bool hasNewFile = !string.IsNullOrWhiteSpace(generatedFileName);

                await RunOnUIAsync(() =>
                {
                    task.Status = hasNewFile ? "完成" : "无变更";
                    task.Progress = 100;
                    task.IsCompleted = true;

                    folder.StatusText = hasNewFile ? "备份完成" : "无变更";
                    if (hasNewFile)
                    {
                        folder.LastBackupTime = DateTime.Now.ToString("yyyy/MM/dd HH:mm");
                    }
                });

                if (hasNewFile)
                {
                    ConfigService.Save();

                    string typeStr = config.Archive.Mode.ToString();
                    HistoryService.AddEntry(config, folder, generatedFileName, typeStr, comment);

                    PruneOldArchives(backupSubDir, config.Archive.Format, config.Archive.KeepCount, config.Archive.Mode);
                }

                Log(hasNewFile
                    ? $"[完成] {folder.DisplayName} 备份成功"
                    : $"[跳过] {folder.DisplayName} 无文件变更");
            }
            else
            {
                await RunOnUIAsync(() =>
                {
                    task.Status = "失败";
                    task.IsCompleted = true;
                    folder.StatusText = "备份失败";
                });
                Log($"[失败] {folder.DisplayName} 备份未完成");
            }
        }


        private static string GenerateFileName(string baseName, string format, string prefix, string comment)
        {
            string timeStr = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string safeComment = SanitizeFileName(comment);

            // 格式: [Full][2025-01-01_12-00-00]WorldName [Comment].7z
            string commentPart = string.IsNullOrEmpty(safeComment) ? "" : $" [{safeComment}]";
            return $"[{prefix}][{timeStr}]{baseName}{commentPart}.{format}";
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var invalid = Path.GetInvalidFileNameChars();
            // 额外过滤掉中括号，以免破坏解析逻辑
            var sb = new StringBuilder();
            foreach (char c in name)
            {
                if (!invalid.Contains(c) && c != '[' && c != ']')
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static void PruneOldArchives(string destDir, string format, int keepCount, BackupMode mode)
        {
            if (keepCount <= 0) return;
            if (mode == BackupMode.Incremental) return; // 保留增量链，避免破坏恢复
            try
            {
                var di = new DirectoryInfo(destDir);
                if (!di.Exists) return;

                var files = di.GetFiles($"*.{format}")
                              .OrderByDescending(f => f.LastWriteTimeUtc)
                              .ToList();

                if (files.Count <= keepCount) return;

                foreach (var file in files.Skip(keepCount))
                {
                    try { file.Delete(); }
                    catch { /* ignore single delete failure */ }
                }
            }
            catch
            {
                // ignore pruning errors
            }
        }

        // --- 模式 1: 全量备份 ---
        // 返回 (Success, FileName)
        private static async Task<(bool Success, string FileName)> DoFullBackupAsync(string source, string destDir, string metaDir, string baseName, BackupConfig config, string comment = "")
        {
            string fileName = GenerateFileName(baseName, config.Archive.Format, "Full", comment);
            string destFile = Path.Combine(destDir, fileName);

            // 1. 直接压缩
            bool result = await Run7zCommandAsync("a", source, destFile, config.Archive);

            // 2. 如果成功，生成新的元数据（为后续可能的增量备份做基准）
            if (result)
            {
                await UpdateMetadataAsync(source, metaDir, fileName, fileName); // 基准是自己
                return (true, fileName);
            }
            return (false, null);
        }

        // --- 模式 2: 智能增量备份 ---
        // 返回 (Success, FileName)
        private static async Task<(bool Success, string FileName)> DoSmartBackupAsync(string source, string destDir, string metaDir, string baseName, BackupConfig config, string comment = "")
        {
            string metadataPath = Path.Combine(metaDir, "metadata.json");
            BackupMetadata oldMeta = null;

            // 1. 读取旧元数据
            if (File.Exists(metadataPath))
            {
                try { oldMeta = JsonSerializer.Deserialize(File.ReadAllText(metadataPath), AppJsonContext.Default.BackupMetadata); }
                catch { Log("[警告] 元数据损坏，将执行全量备份"); }
            }

            // 如果没有元数据，强制全量
            if (oldMeta == null)
            {
                Log("[信息] 未找到基准元数据，转为全量备份");
                return await DoFullBackupAsync(source, destDir, metaDir, baseName, config, comment);
            }

            // 2. 扫描并对比文件
            Log("[分析] 正在对比文件差异...");
            var currentStates = ScanDirectory(source);
            var changedFiles = new List<string>();

            foreach (var kvp in currentStates)
            {
                string relPath = kvp.Key;
                FileState curState = kvp.Value;

                if (oldMeta.FileStates.TryGetValue(relPath, out var oldState))
                {
                    // 对比 Size 和 Time (快速) 或 Hash (精确)
                    // 为了性能，先比 Size 和 Time，如果一致则认为没变
                    // 如果你需要绝对精确，可以强制算 Hash
                    if (curState.Size != oldState.Size || curState.LastWriteTimeUtc != oldState.LastWriteTimeUtc)
                    {
                        changedFiles.Add(relPath);
                    }
                    else
                    {
                        // 如果想模仿 MineBackup 严格模式，这里可以加 Hash 对比
                        // changedFiles.Add(relPath); 
                    }
                }
                else
                {
                    // 新文件
                    changedFiles.Add(relPath);
                }
            }

            if (changedFiles.Count == 0)
            {
                Log("[跳过] 没有检测到文件变更");
                return (true, null);
            }

            Log($"[信息] 检测到 {changedFiles.Count} 个文件变更");

            // 3. 生成文件列表文件
            string listFile = Path.GetTempFileName();
            File.WriteAllLines(listFile, changedFiles);

            string fileName = GenerateFileName(baseName, config.Archive.Format, "Smart", comment);
            string destFile = Path.Combine(destDir, fileName);

            // 4. 执行压缩 (使用 @listfile)
            // 注意：7z 需要工作目录在 source 下，才能正确识别相对路径列表
            bool result = await Run7zCommandAsync("a", source, destFile, config.Archive, listFile);

            // 5. 更新元数据
            if (result)
            {
                File.Delete(listFile);
                // 更新元数据：基准文件保持不变（指向最初的Full），LastBackup指向自己
                await UpdateMetadataAsync(source, metaDir, fileName, oldMeta.BasedOnFullBackup, currentStates);
                return (true, fileName);
            }
            else
            {
                try { File.Delete(listFile); } catch { }
                return (false, null);
            }
        }

        // --- 模式 3: 覆写备份 ---
        // 返回 (Success, FileName)
        private static async Task<(bool Success, string FileName)> DoOverwriteBackupAsync(string source, string destDir, string baseName, BackupConfig config, string comment = "")
        {
            // 1. 寻找最近的备份文件
            var dirInfo = new DirectoryInfo(destDir);
            var files = dirInfo.GetFiles($"*.{config.Archive.Format}")
                               .OrderByDescending(f => f.LastWriteTime)
                               .ToList();

            if (files.Count == 0)
            {
                Log("[信息] 未找到已有备份，将执行全量备份");
                return await DoFullBackupAsync(source, destDir, "", baseName, config, comment); // 覆写模式不需要元数据
            }

            FileInfo targetFile = files[0];
            Log($"[覆写] 正在更新: {targetFile.Name}");

            // 2. 执行 update 命令 (u)
            // 7z u <archive_name> <file_names>
            // u 指令会更新已存在的文件并添加新文件
            bool result = await Run7zCommandAsync("u", source, targetFile.FullName, config.Archive);

            string resultingFileName = null;

            if (result)
            {
                // 3. 重命名文件以更新时间戳 (参考 MineBackup 逻辑)
                // 假设文件名格式包含 [YYYY-MM-DD...]，我们要替换它
                string oldName = targetFile.Name;
                string newTimeStr = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");

                // 简单的正则或字符串替换：查找第二个方括号内时间并替换
                string newName = oldName;
                int firstBracket = oldName.IndexOf('[');
                int secondBracket = oldName.IndexOf(']', firstBracket + 1);
                int thirdBracket = oldName.IndexOf('[', secondBracket + 1);
                int fourthBracket = thirdBracket >= 0 ? oldName.IndexOf(']', thirdBracket + 1) : -1;

                // 尝试找到时间段（第二个方括号里的内容）
                int timeStart = -1;
                int timeEnd = -1;
                // 通mon pattern: [Type][Time]Name.ext  -> time is between second '[' and following ']'
                int idx = 0;
                // 找第二个 '['
                int nth = 0;
                for (int i = 0; i < oldName.Length; i++)
                {
                    if (oldName[i] == '[') nth++;
                    if (nth == 2) { timeStart = i + 1; break; }
                }
                if (timeStart != -1)
                {
                    for (int i = timeStart; i < oldName.Length; i++)
                    {
                        if (oldName[i] == ']') { timeEnd = i; break; }
                    }
                }

                if (timeStart != -1 && timeEnd != -1)
                {
                    newName = oldName.Substring(0, timeStart) + newTimeStr + oldName.Substring(timeEnd);
                }
                else
                {
                    // 如果格式不对，就重新构造名字，保留类型前缀与后缀
                    string extension = Path.GetExtension(oldName);
                    string prefix = "[Overwrite]";
                    string nameWithoutExt = Path.GetFileNameWithoutExtension(oldName);
                    // 去掉已存在的方括号信息尽量简化构造
                    string simpleBase = baseName;
                    newName = GenerateFileName(simpleBase, config.Archive.Format, "Overwrite", comment);
                }

                resultingFileName = newName;

                if (newName != oldName)
                {
                    string newPath = Path.Combine(destDir, newName);
                    try
                    {
                        File.Move(targetFile.FullName, newPath);
                        Log($"[重命名] 更新为: {newName}");
                    }
                    catch { /* 忽略重命名错误 */ resultingFileName = targetFile.Name; }
                }
                else
                {
                    resultingFileName = oldName;
                }
            }

            return (result, resultingFileName ?? targetFile.Name);
        }


        public enum RestoreMode
        {
            Clean = 0,      // 清空目标后还原 (最安全)
            Overwrite = 1   // 直接覆盖 (保留未被覆盖的文件)
        }

        public static async Task RestoreBackupAsync(BackupConfig config, ManagedFolder folder, HistoryItem historyItem, RestoreMode mode)
        {
            string backupFilePath = Path.Combine(config.DestinationPath, folder.DisplayName, historyItem.FileName);
            string targetDir = folder.Path; // 还原回源目录

            if (!File.Exists(backupFilePath))
            {
                Log($"[错误] 备份文件未找到: {backupFilePath}");
                return;
            }

            string sevenZipExe = ResolveSevenZipExecutable();
            if (string.IsNullOrEmpty(sevenZipExe)) return;
            
            var backupDir = new DirectoryInfo(Path.GetDirectoryName(backupFilePath)!);
            var targetFile = new FileInfo(backupFilePath);
            var chain = BuildRestoreChain(backupDir, targetFile, historyItem.BackupType);
            if (chain.Count == 0)
            {
                Log("[失败] 找不到可用于还原的备份链。");
                return;
            }

            Log($"=== 开始还原: {folder.DisplayName} ===");
            Log($" -> 目标备份: {historyItem.FileName}");
            Log($" -> 目标路径: {targetDir}");

            // 1. Clean 模式先清空目标
            if (mode == RestoreMode.Clean)
            {
                Log("[清理] 正在清空目标目录...");
                try
                {
                    // 简单粗暴清空，生产环境建议做白名单检查 (参考 MineBackup whitelist)
                    DirectoryInfo di = new DirectoryInfo(targetDir);
                    foreach (FileInfo file in di.GetFiles()) file.Delete();
                    foreach (DirectoryInfo dir in di.GetDirectories()) dir.Delete(true);
                }
                catch (Exception ex)
                {
                    Log($"[警告] 清理目录失败: {ex.Message}，尝试继续覆盖...");
                }
            }

            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            // 2. 按链顺序依次解压（Full + Smart）
            foreach (var file in chain)
            {
                Log($"[还原] 应用 {file.Name}");
                bool ok = await RunSevenZipProcessAsync(sevenZipExe, $"x \"{file.FullName}\" -o\"{targetDir}\" -y", file.DirectoryName);
                if (!ok)
                {
                    Log("[失败] 7z 解压出错，请检查日志。");
                    return;
                }
            }

            Log("[成功] 还原操作完成！");
        }


        // --- 辅助：元数据处理 ---
        private static Dictionary<string, FileState> ScanDirectory(string path)
        {
            var result = new Dictionary<string, FileState>();
            var dirInfo = new DirectoryInfo(path);

            // 获取所有文件，使用相对路径作为 Key
            foreach (var file in dirInfo.GetFiles("*", SearchOption.AllDirectories))
            {
                string relPath = Path.GetRelativePath(path, file.FullName);
                result[relPath] = new FileState
                {
                    Size = file.Length,
                    LastWriteTimeUtc = file.LastWriteTimeUtc,
                    // 只有在真正需要的时候才算 Hash，因为很慢。
                    // 这里暂且留空或仅在严格模式计算。MineBackup 默认也是优先比对 Time/Size
                    Hash = ""
                };
            }
            return result;
        }

        private static async Task UpdateMetadataAsync(string sourceDir, string metaDir, string currentBackupFile, string baseBackupFile, Dictionary<string, FileState> states = null)
        {
            if (states == null) states = ScanDirectory(sourceDir);

            var meta = new BackupMetadata
            {
                LastBackupTime = DateTime.Now,
                LastBackupFileName = currentBackupFile,
                BasedOnFullBackup = baseBackupFile,
                FileStates = states
            };

            string json = JsonSerializer.Serialize(meta, AppJsonContext.Default.BackupMetadata);
            await File.WriteAllTextAsync(Path.Combine(metaDir, "metadata.json"), json);
        }

        // --- 核心：7z 进程调用 ---
        private static async Task<bool> Run7zCommandAsync(string commandMode, string sourceDir, string archivePath, ArchiveSettings settings, string listFile = null)
        {
            string sevenZipExe = ResolveSevenZipExecutable();
            if (string.IsNullOrEmpty(sevenZipExe)) return false;

            // 构建参数
            // -mx: 压缩等级
            // -ssw: 即使打开也压缩
            // -m0: 算法
            var sb = new StringBuilder();
            sb.Append($"{commandMode} -t{settings.Format} \"{archivePath}\"");

            if (listFile != null)
            {
                // 使用文件列表
                sb.Append($" @\"{listFile}\"");
            }
            else
            {
                // 直接指定源目录 (加通配符以包含内容而非目录本身，视需求而定)
                sb.Append($" \"{sourceDir}\\*\"");
            }

            sb.Append($" -mx={settings.CompressionLevel} -m0={settings.Method} -ssw");
            if (!string.IsNullOrWhiteSpace(settings.Password))
            {
                sb.Append($" -p\"{settings.Password}\" -mhe=on");
            }
            sb.Append(" -bsp1"); // 开启进度输出到 stderr/stdout
            // 排除 (稍后实现)
            // sb.Append(" -xr!*.tmp");

            string args = sb.ToString();
            string safeArgs = string.IsNullOrWhiteSpace(settings.Password) ? args : args.Replace(settings.Password, "***");

            return await RunSevenZipProcessAsync(sevenZipExe, args, sourceDir, safeArgs);
        }

        private static List<FileInfo> BuildRestoreChain(DirectoryInfo backupDir, FileInfo targetFile, string backupType)
        {
            var chain = new List<FileInfo>();
            if (!backupDir.Exists) return chain;

            bool isIncremental =
                (!string.IsNullOrWhiteSpace(backupType) && backupType.Equals("Incremental", StringComparison.OrdinalIgnoreCase)) ||
                targetFile.Name.Contains("[Smart]", StringComparison.OrdinalIgnoreCase);

            if (!isIncremental)
            {
                chain.Add(targetFile);
                return chain;
            }

            var baseFull = backupDir
                .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .Where(f => f.Name.Contains("[Full]", StringComparison.OrdinalIgnoreCase) && f.LastWriteTime <= targetFile.LastWriteTime)
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();

            if (baseFull == null)
            {
                Log("[警告] 找不到对应的全量备份，将仅尝试还原所选增量包。");
                chain.Add(targetFile);
                return chain;
            }

            chain.Add(baseFull);

            var increments = backupDir
                .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .Where(f => f.Name.Contains("[Smart]", StringComparison.OrdinalIgnoreCase)
                            && f.LastWriteTime >= baseFull.LastWriteTime
                            && f.LastWriteTime <= targetFile.LastWriteTime)
                .OrderBy(f => f.LastWriteTime);

            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            added.Add(baseFull.FullName);

            foreach (var inc in increments)
            {
                if (added.Add(inc.FullName))
                {
                    chain.Add(inc);
                }
            }

            if (added.Add(targetFile.FullName))
            {
                chain.Add(targetFile);
            }

            return chain;
        }

        private static string ResolveSevenZipExecutable()
        {
            var candidates = new List<string>();
            var configPath = ConfigService.CurrentConfig.GlobalSettings?.SevenZipPath;

            void AddCandidate(string path)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                try { candidates.Add(Path.GetFullPath(path)); }
                catch { candidates.Add(path); }
            }

            // 1) 用户配置
            AddCandidate(configPath);
            if (!string.IsNullOrWhiteSpace(configPath) && !Path.IsPathRooted(configPath))
            {
                AddCandidate(Path.Combine(AppContext.BaseDirectory, configPath));
            }

            // 2) 应用目录和常见安装目录
            string[] exeNames = { "7z.exe", "7zz.exe", "7za.exe" };
            foreach (var exe in exeNames)
            {
                AddCandidate(Path.Combine(AppContext.BaseDirectory, exe));

                var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (!string.IsNullOrWhiteSpace(pf)) AddCandidate(Path.Combine(pf, "7-Zip", exe));

                var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                if (!string.IsNullOrWhiteSpace(pf86)) AddCandidate(Path.Combine(pf86, "7-Zip", exe));
            }

            // 3) PATH
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(pathEnv))
            {
                foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = dir.Trim();
                    foreach (var exe in exeNames)
                    {
                        AddCandidate(Path.Combine(trimmed, exe));
                    }
                }
            }

            foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(path)) return path;
            }

            Log("严重错误：找不到 7z 可执行文件，请在设置中指定 7z.exe/7zz.exe。");
            return null;
        }

        private static async Task<bool> RunSevenZipProcessAsync(string sevenZipExe, string arguments, string workingDirectory = null, string logArguments = null)
        {
            var pInfo = new ProcessStartInfo
            {
                FileName = sevenZipExe,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                pInfo.WorkingDirectory = workingDirectory;
            }

            Log($"[CMD] {Path.GetFileName(sevenZipExe)} {(logArguments ?? arguments)}", LogLevel.Debug);

            try
            {
                using var p = new Process { StartInfo = pInfo };
                p.OutputDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log($"[7z] {e.Data}"); };
                p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log($"[7z Err] {e.Data}", LogLevel.Error); };

                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                await p.WaitForExitAsync();

                return p.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Log($"[系统错误] {ex.Message}");
                return false;
            }
        }

        private static void Log(string message)
        {
            System.Diagnostics.Debug.WriteLine(message);
            LogService.Log(message, InferLevel(message));
        }

        private static void Log(string message, LogLevel level)
        {
            System.Diagnostics.Debug.WriteLine(message);
            LogService.Log(message, level);
        }

        private static LogLevel InferLevel(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return LogLevel.Info;

            var lower = message.ToLowerInvariant();

            if (lower.Contains("[7z err]") || lower.Contains("[错误]") || lower.Contains("[失败]") || lower.Contains("[异常]") || lower.Contains("严重错误") || lower.Contains("[系统错误]"))
                return LogLevel.Error;

            if (lower.Contains("[警告]") || lower.Contains("[warning]"))
                return LogLevel.Warning;

            if (lower.Contains("[debug]") || lower.Contains("[调试]") || lower.Contains("[cmd]"))
                return LogLevel.Debug;

            return LogLevel.Info;
        }
    }
}
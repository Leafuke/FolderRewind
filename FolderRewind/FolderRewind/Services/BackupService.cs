using FolderRewind.Models;
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
            // 1. 创建任务对象
            var task = new BackupTask
            {
                FolderName = folder.DisplayName,
                Status = "准备中...",
                Progress = 0
            };

            // 必须在 UI 线程添加 (如果你在非 UI 线程调用 BackupFolderAsync，这里需要 Dispatcher)
            // 假设我们都在 UI 线程触发，或者使用 BindingOperations.EnableCollectionSynchronization
            ActiveTasks.Insert(0, task); // 新任务放前面


            if (config == null || folder == null) return;

            string sourcePath = folder.Path;
            // 按照要求：备份路径 = 用户设置的目标路径 \ 文件夹名
            string backupSubDir = Path.Combine(config.DestinationPath, folder.DisplayName);
            string metadataDir = Path.Combine(config.DestinationPath, "_metadata", folder.DisplayName);

            if (!Directory.Exists(sourcePath))
            {
                Log($"[错误] 源文件夹不存在: {sourcePath}");
                folder.StatusText = "源不存在";
                return;
            }

            if (string.IsNullOrEmpty(config.DestinationPath))
            {
                Log("错误：未设置备份目标路径！");
                return;
            }

            // 创建必要的目录
            if (!Directory.Exists(backupSubDir)) Directory.CreateDirectory(backupSubDir);
            if (!Directory.Exists(metadataDir)) Directory.CreateDirectory(metadataDir);

            Log($"[处理中] {folder.DisplayName}");
            folder.StatusText = "正在备份...";

            bool success = false;
            string generatedFileName = null;
            try
            {

                task.Status = "正在处理...";
                folder.StatusText = "备份中...";

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

                task.Status = "完成";
                task.Progress = 100;
                task.IsCompleted = true;

                folder.StatusText = "备份完成";
                folder.LastBackupTime = DateTime.Now.ToString("yyyy/MM/dd HH:mm");
                ConfigService.Save();

                // === 新增：写入历史记录 ===
                string typeStr = config.Archive.Mode.ToString(); // Full, Incremental, Overwrite

                // 使用刚刚得到的文件名
                HistoryService.AddEntry(config, folder, generatedFileName, typeStr, comment);

                Log($"[完成] {folder.DisplayName} 备份成功");
            }
            else
            {
                task.Status = "失败";
                folder.StatusText = "备份失败";
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
                try { oldMeta = JsonSerializer.Deserialize<BackupMetadata>(File.ReadAllText(metadataPath)); }
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

            Log($"=== 开始还原: {folder.DisplayName} ===");
            Log($" -> 备份文件: {historyItem.FileName}");
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

            // 2. 调用 7z 解压
            // 7z x "archive.7z" -o"target" -y
            string sevenZipExe = ConfigService.CurrentConfig.GlobalSettings.SevenZipPath;
            string args = $"x \"{backupFilePath}\" -o\"{targetDir}\" -y";

            var pInfo = new ProcessStartInfo
            {
                FileName = sevenZipExe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
                using var p = new Process { StartInfo = pInfo };
                p.OutputDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log($"[7z] {e.Data}"); };
                p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log($"[7z Err] {e.Data}"); };

                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                await p.WaitForExitAsync();

                if (p.ExitCode == 0)
                {
                    Log("[成功] 还原操作完成！");
                }
                else
                {
                    Log("[失败] 7z 解压出错，请检查日志。");
                }
            }
            catch (Exception ex)
            {
                Log($"[异常] {ex.Message}");
            }
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

            string json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(metaDir, "metadata.json"), json);
        }

        // --- 核心：7z 进程调用 ---
        private static async Task<bool> Run7zCommandAsync(string commandMode, string sourceDir, string archivePath, ArchiveSettings settings, string listFile = null)
        {

            string sevenZipExe = ConfigService.CurrentConfig.GlobalSettings.SevenZipPath;
            if (!File.Exists(sevenZipExe)) sevenZipExe = Path.Combine(AppContext.BaseDirectory, "7z.exe");
            if (!File.Exists(sevenZipExe))
            {
                Log("严重错误：找不到 7z.exe");
                return false;
            }

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
            sb.Append(" -bsp1"); // 开启进度输出到 stderr/stdout
            // 排除 (稍后实现)
            // sb.Append(" -xr!*.tmp");

            var progressRegex = new Regex(@"\s+(\d+)%");


            var pInfo = new ProcessStartInfo
            {
                FileName = sevenZipExe,
                Arguments = sb.ToString(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                // 重要：设置工作目录，否则相对路径列表会失效
                WorkingDirectory = sourceDir
            };

            Log($"[CMD] 7z {commandMode} ...");

            try
            {
                using var p = new Process { StartInfo = pInfo };
                p.OutputDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log($"[7z] {e.Data}"); };
                p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log($"[7z Err] {e.Data}"); };

                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                await p.WaitForExitAsync();

                return p.ExitCode == 0;

                //p.OutputDataReceived += (s, e) =>
                //{
                //    if (e.Data != null)
                //    {
                //        // 解析进度
                //        var match = progressRegex.Match(e.Data);
                //        if (match.Success && double.TryParse(match.Groups[1].Value, out double p))
                //        {
                //            task.Progress = p;
                //            task.Status = $"正在压缩 {p}%";
                //        }
                //        // 解析速度和大小比较复杂，7z 输出格式多变，这里暂略，可解析 "12 MiB/s"
                //    }
                //};
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
            LogService.Log(message);
        }
    }
}
using FolderRewind.Models;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public static partial class BackupService
    {
        // 7-Zip 解析与压缩执行集中在这里，备份、还原、安全删除共用同一套进程封装。

        private static bool ExtractArchiveToDirectorySync(string sevenZipExe, string archivePath, string targetDir, string? password, int cpuThreads = 0, bool runAtLowPriority = false)
        {
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
        }

        private static bool CreateArchiveFromDirectorySync(string sevenZipExe, string sourceDir, string archivePath, ArchiveSettings settings, string? password)
        {
            var sb = new StringBuilder();
            sb.Append($"a -t{settings.Format} \"{archivePath}\" .\\*");
            sb.Append($" -mx={settings.CompressionLevel} -m0={settings.Method} -ssw");

            int cpuThreads = NormalizeCpuThreadCount(settings.CpuThreads);
            if (cpuThreads > 0)
            {
                sb.Append($" -mmt{cpuThreads}");
            }
            else
            {
                sb.Append(" -mmt");
            }

            if (!string.IsNullOrWhiteSpace(password))
            {
                sb.Append($" -p\"{password}\" -mhe=on");
            }

            sb.Append(" -bsp1");
            return RunSevenZipProcessSync(sevenZipExe, sb.ToString(), sourceDir, settings.RunCompressionAtLowPriority);
        }

        private static ArchiveSettings CreateArchiveSettingsForSafeDelete(ArchiveSettings? sourceSettings, string format)
        {
            return new ArchiveSettings
            {
                Format = string.IsNullOrWhiteSpace(format) ? (sourceSettings?.Format ?? "7z") : format,
                CompressionLevel = sourceSettings?.CompressionLevel ?? 5,
                Method = string.IsNullOrWhiteSpace(sourceSettings?.Method) ? "LZMA2" : sourceSettings.Method,
                CpuThreads = sourceSettings?.CpuThreads ?? 0,
                RunCompressionAtLowPriority = sourceSettings?.RunCompressionAtLowPriority ?? false
            };
        }

        private static void CleanupArchiveTempArtifacts(DirectoryInfo backupDir, string format)
        {
            if (!backupDir.Exists) return;

            try
            {
                foreach (var file in backupDir.GetFiles())
                {
                    if (!IsArchiveTempArtifact(file, format))
                    {
                        continue;
                    }

                    try
                    {
                        if ((file.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        {
                            file.Attributes &= ~FileAttributes.ReadOnly;
                        }
                        file.Delete();
                    }
                    catch
                    {
                    }
                }

                foreach (var dir in backupDir.GetDirectories("__FolderRewind_SafeDelete_*"))
                {
                    try
                    {
                        ClearReadonlyAttributes(dir.FullName);
                        dir.Delete(true);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private static bool IsArchiveTempArtifact(FileInfo file, string format)
        {
            if (file == null || string.IsNullOrWhiteSpace(format))
            {
                return false;
            }

            string pattern = $@"\.{Regex.Escape(format)}\.tmp\d*$";
            return Regex.IsMatch(file.Name, pattern, RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// 同步方式运行 7z 进程（用于安全删除等非异步场景）
        /// </summary>
        private static bool RunSevenZipProcessSync(string sevenZipExe, string arguments, string? workingDirectory = null, bool runAtLowPriority = false)
        {
            try
            {
                arguments = EnsureSswArgument(arguments);

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
                    pInfo.WorkingDirectory = workingDirectory;

                // 修复进程未退出读取 ExitCode 抛出系统错误的神秘问题。
                using var p = new Process { StartInfo = pInfo };
                string? lastErrorLine = null;

                p.OutputDataReceived += (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    Log($"[7z] {e.Data}");
                };
                p.ErrorDataReceived += (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    lastErrorLine = e.Data;
                    Log($"[7z Err] {e.Data}", LogLevel.Error);
                };

                if (!p.Start())
                {
                    Log(I18n.Format("BackupService_Log_SystemError", I18n.GetString("BackupService_Log_SevenZipStartFailed")), LogLevel.Error);
                    return false;
                }

                ApplyLowPriorityIfRequested(p, runAtLowPriority);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                const int timeoutMs =300_000; // 最长等待 5 分钟
                bool exited = p.WaitForExit(timeoutMs);
                if (!exited)
                {
                    Log(I18n.Format("BackupService_Log_SystemError", I18n.Format("BackupService_Log_SevenZipTimeout", timeoutMs / 1000)), LogLevel.Error);
                    try
                    {
                        p.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                    }
                    return false;
                }

                p.WaitForExit();
                if (p.ExitCode != 0 && !string.IsNullOrWhiteSpace(lastErrorLine))
                {
                    Log($"[7z Exit={p.ExitCode}] {lastErrorLine}", LogLevel.Error);
                }

                return p.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Log(I18n.Format("BackupService_Log_SystemError", ex.Message), LogLevel.Error);
                return false;
            }
        }

    }
}

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
        private static string? ResolveSevenZipExecutable()
        {
            var configPath = ConfigService.CurrentConfig.GlobalSettings?.SevenZipPath;
            var executable = SevenZipExecutableLocator.Resolve(configPath);
            if (string.IsNullOrWhiteSpace(executable))
            {
                Log(I18n.Format("BackupService_Log_SevenZipNotFound"), LogLevel.Error);
            }

            return executable;
        }

        /// <summary>
        /// 匹配 7z 输出中的百分比进度（如 " 42%" 或 "100%"）
        /// </summary>
        private static readonly Regex _progressRegex = new(@"^\s*(\d{1,3})%", RegexOptions.Compiled);

        // 200ms 固定刷新：避免每个 stdout 行都向 UI 线程排队，减轻 DispatcherQueue 压力。
        private static long _lastProgressUpdateTimestamp;
        private const long ProgressUpdateIntervalMs = 200;

        private static bool ShouldUpdateProgress()
        {
            var now = Stopwatch.GetTimestamp();
            var elapsedMs = (now - _lastProgressUpdateTimestamp) * 1000.0 / Stopwatch.Frequency;
            if (elapsedMs < ProgressUpdateIntervalMs && _lastProgressUpdateTimestamp != 0)
                return false;
            _lastProgressUpdateTimestamp = now;
            return true;
        }

        private static async Task<bool> RunSevenZipProcessAsync(
            string sevenZipExe, string arguments,
            string? workingDirectory = null, string? logArguments = null,
            BackupTask? taskToUpdate = null,
            double progressBase = 0, double progressRange = 100,
            bool runAtLowPriority = false)
        {
            arguments = EnsureSswArgument(arguments);
            logArguments = EnsureSswArgument(logArguments ?? arguments);

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

            Log($"[CMD] {Path.GetFileName(sevenZipExe)} {logArguments}", LogLevel.Debug);

            string? lastErrorLine = null;

            try
            {
                using var p = new Process { StartInfo = pInfo };
                p.OutputDataReceived += (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    Log($"[7z] {e.Data}");

                    // 解析 7z 的百分比进度输出（如 " 42%" 或 " 15% 3 + file.txt"）
                    if (taskToUpdate != null)
                    {
                        var match = _progressRegex.Match(e.Data);
                        if (match.Success && int.TryParse(match.Groups[1].Value, out int percent) && percent >= 0 && percent <= 100)
                        {
                            double mapped = progressBase + (double)percent / 100.0 * progressRange;
                            // 每次 stdout 行都计算进度值，但每 200ms 才向 UI 线程排队一次，避免
                            // 大量 Enqueue 调用造成 DispatcherQueue 积压。
                            if (ShouldUpdateProgress())
                            {
                                UiDispatcherService.Enqueue(() =>
                                {
                                    if (taskToUpdate.IsIndeterminate) taskToUpdate.IsIndeterminate = false;
                                    taskToUpdate.Progress = Math.Min(mapped, 100);
                                });
                            }
                        }
                    }
                };
                p.ErrorDataReceived += (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    Log($"[7z Err] {e.Data}", LogLevel.Error);
                    lastErrorLine = e.Data; // 保留最后一条错误信息用于显示
                };

                p.Start();
                ApplyLowPriorityIfRequested(p, runAtLowPriority);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                await p.WaitForExitAsync();

                // 7z 返回非零退出码且有 stderr 输出时，将错误信息写入任务
                if (p.ExitCode != 0 && taskToUpdate != null && !string.IsNullOrWhiteSpace(lastErrorLine))
                {
                    UiDispatcherService.Enqueue(() =>
                    {
                        if (string.IsNullOrEmpty(taskToUpdate.ErrorMessage))
                            taskToUpdate.ErrorMessage = lastErrorLine;
                    });
                }

                return p.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Log(I18n.Format("BackupService_Log_SystemError", ex.Message), LogLevel.Error);
                if (taskToUpdate != null)
                {
                    UiDispatcherService.Enqueue(() =>
                    {
                        if (string.IsNullOrEmpty(taskToUpdate.ErrorMessage))
                            taskToUpdate.ErrorMessage = ex.Message;
                    });
                }
                return false;
            }
        }

        private static string EnsureSswArgument(string? arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments))
                return "-ssw";

            if (Regex.IsMatch(arguments, @"(?:^|\s)-ssw(?:\s|$)", RegexOptions.IgnoreCase))
                return arguments;

            return arguments + " -ssw";
        }
    }
}

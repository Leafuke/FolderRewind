using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public sealed class CoreFeatureValidationStepResult
    {
        public string Name { get; init; } = string.Empty;
        public bool Success { get; init; }
        public string Details { get; init; } = string.Empty;
        public TimeSpan Duration { get; init; }
    }

    public sealed class CoreFeatureValidationReport
    {
        public bool Automatic { get; init; }
        public bool Success { get; set; }
        public string Summary { get; set; } = string.Empty;
        public DateTime StartedAtUtc { get; init; }
        public DateTime FinishedAtUtc { get; set; }
        public IReadOnlyList<CoreFeatureValidationStepResult> Steps { get; set; } = Array.Empty<CoreFeatureValidationStepResult>();

        public TimeSpan Duration => FinishedAtUtc >= StartedAtUtc
            ? FinishedAtUtc - StartedAtUtc
            : TimeSpan.Zero;

        public string ToDisplayText()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{I18n.GetString("CoreValidation_Report_Result")}: {(Success ? I18n.GetString("CoreValidation_Report_Result_Passed") : I18n.GetString("CoreValidation_Report_Result_Failed"))}");
            sb.AppendLine($"{I18n.GetString("CoreValidation_Report_Mode")}: {(Automatic ? I18n.GetString("CoreValidation_Report_Mode_Automatic") : I18n.GetString("CoreValidation_Report_Mode_Manual"))}");
            sb.AppendLine($"{I18n.GetString("CoreValidation_Report_Started")}: {StartedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"{I18n.GetString("CoreValidation_Report_Finished")}: {FinishedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"{I18n.GetString("CoreValidation_Report_Duration")}: {Duration.TotalSeconds:F1}s");
            sb.AppendLine($"{I18n.GetString("CoreValidation_Report_Summary")}: {Summary}");
            sb.AppendLine();

            foreach (var step in Steps)
            {
                sb.AppendLine($"[{(step.Success ? "OK" : "FAIL")}] {step.Name} ({step.Duration.TotalSeconds:F1}s)");
                if (!string.IsNullOrWhiteSpace(step.Details))
                {
                    sb.AppendLine(step.Details);
                }
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }
    }

    public static class CoreFeatureValidationService
    {
        // 全局串行执行门闩：同一时间只允许一个自检任务运行。
        private static readonly SemaphoreSlim RunGate = new(1, 1);
        // 自动触发去重标记，避免配置连续保存时排队多个自动自检。
        private static int _autoRunQueued;

        public static event Action? StateChanged;

        public static bool IsRunning { get; private set; }

        public static string StatusText { get; private set; } = I18n.GetString("CoreValidation_Status_Idle");

        public static CoreFeatureValidationReport? LastReport { get; private set; }

        public static bool ShouldRunInitialValidation()
        {
            var settings = ConfigService.CurrentConfig?.GlobalSettings;
            var configs = ConfigService.CurrentConfig?.BackupConfigs;
            if (settings == null || settings.HasTriggeredInitialCoreValidation)
            {
                return false;
            }

            if (configs == null || configs.Count == 0)
            {
                return false;
            }

            return configs.Any(config =>
                !string.IsNullOrWhiteSpace(config.DestinationPath)
                && config.SourceFolders.Any(folder => !string.IsNullOrWhiteSpace(folder.Path)));
        }

        public static void TryScheduleInitialValidation()
        {
            if (!ShouldRunInitialValidation() || IsRunning)
            {
                return;
            }

            // 已有自动任务排队时直接返回。
            if (Interlocked.Exchange(ref _autoRunQueued, 1) != 0)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    // 留一点启动缓冲，避免和首屏初始化/插件加载抢资源。
                    await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    if (!ShouldRunInitialValidation() || IsRunning)
                    {
                        return;
                    }

                    await RunValidationAsync(true).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogService.Log($"[CoreValidation] Automatic validation scheduling failed: {ex.Message}", LogLevel.Error);
                }
                finally
                {
                    Interlocked.Exchange(ref _autoRunQueued, 0);
                }
            });
        }

        public static async Task<CoreFeatureValidationReport> RunValidationAsync(bool automatic = false)
        {
            await RunGate.WaitAsync().ConfigureAwait(false);

            IsRunning = true;
            UpdateStatus(I18n.GetString("CoreValidation_Status_Preparing"));

            try
            {
                var report = await RunValidationInternalAsync(automatic).ConfigureAwait(false);
                LastReport = report;
                PersistReport(report, automatic);

                IsRunning = false;
                UpdateStatus(report.Success
                    ? I18n.GetString("CoreValidation_Status_Succeeded")
                    : I18n.GetString("CoreValidation_Status_Failed"));

                if (automatic)
                {
                    NotifyAutomaticResult(report);
                }

                RaiseStateChanged();
                return report;
            }
            catch (Exception ex)
            {
                var report = new CoreFeatureValidationReport
                {
                    Automatic = automatic,
                    StartedAtUtc = DateTime.UtcNow,
                    FinishedAtUtc = DateTime.UtcNow,
                    Success = false,
                    Summary = ex.Message,
                    Steps = new[]
                    {
                        new CoreFeatureValidationStepResult
                        {
                            Name = I18n.GetString("CoreValidation_Step_FatalError"),
                            Success = false,
                            Details = ex.ToString(),
                            Duration = TimeSpan.Zero
                        }
                    }
                };

                LastReport = report;
                PersistReport(report, automatic);

                IsRunning = false;
                UpdateStatus(I18n.GetString("CoreValidation_Status_Failed"));
                if (automatic)
                {
                    NotifyAutomaticResult(report);
                }

                RaiseStateChanged();
                return report;
            }
            finally
            {
                RunGate.Release();
            }
        }

        private static async Task<CoreFeatureValidationReport> RunValidationInternalAsync(bool automatic)
        {
            var report = new CoreFeatureValidationReport
            {
                Automatic = automatic,
                StartedAtUtc = DateTime.UtcNow
            };

            var steps = new List<CoreFeatureValidationStepResult>();
            string validationRoot = Path.Combine(Path.GetTempPath(), "FolderRewind-CoreValidation-" + Guid.NewGuid().ToString("N"));
            string mainSourceDir = Path.Combine(validationRoot, "main-source");
            string mainRestoreDir = Path.Combine(validationRoot, "main-restore");
            string mainBackupRoot = Path.Combine(validationRoot, "main-backups");
            string pruneSourceDir = Path.Combine(validationRoot, "prune-source");
            string pruneRestoreDir = Path.Combine(validationRoot, "prune-restore");
            string pruneBackupRoot = Path.Combine(validationRoot, "prune-backups");

            BackupConfig? mainConfig = null;
            ManagedFolder? mainSourceFolder = null;
            ManagedFolder? mainRestoreFolder = null;
            BackupConfig? pruneConfig = null;
            ManagedFolder? pruneSourceFolder = null;
            ManagedFolder? pruneRestoreFolder = null;

            HistoryItem? fullEntry = null;
            HistoryItem? smartEntryOne = null;
            HistoryItem? smartEntryTwo = null;
            HistoryItem? deletionOnlyEntry = null;

            Dictionary<string, string>? snapshotSmartOne = null;
            Dictionary<string, string>? snapshotDeletionOnly = null;
            Dictionary<string, string>? pruneLatestSnapshot = null;

            FileStream? sharedLockHandle = null;
            using var notificationSuppression = NotificationService.SuppressNotifications();

            try
            {
                bool continueValidation = await RunStepAsync(
                    I18n.GetString("CoreValidation_Step_PrepareWorkspace"),
                    async () =>
                    {
                        Directory.CreateDirectory(validationRoot);
                        Directory.CreateDirectory(mainSourceDir);
                        Directory.CreateDirectory(mainRestoreDir);
                        Directory.CreateDirectory(mainBackupRoot);
                        Directory.CreateDirectory(pruneSourceDir);
                        Directory.CreateDirectory(pruneRestoreDir);
                        Directory.CreateDirectory(pruneBackupRoot);

                        WriteTextFile(Path.Combine(mainSourceDir, "alpha.txt"), BuildValidationPayload("alpha-init", 320));
                        WriteTextFile(Path.Combine(mainSourceDir, "sub", "beta.txt"), BuildValidationPayload("beta-init", 240));

                        mainConfig = CreateValidationConfig("CoreValidation-Main", mainBackupRoot, keepCount: 0);
                        mainSourceFolder = new ManagedFolder { Path = mainSourceDir, DisplayName = "MainScenario" };
                        mainRestoreFolder = new ManagedFolder { Path = mainRestoreDir, DisplayName = "MainScenario" };
                        mainConfig.SourceFolders.Add(mainSourceFolder);

                        pruneConfig = CreateValidationConfig("CoreValidation-Prune", pruneBackupRoot, keepCount: 2);
                        pruneSourceFolder = new ManagedFolder { Path = pruneSourceDir, DisplayName = "PruneScenario" };
                        pruneRestoreFolder = new ManagedFolder { Path = pruneRestoreDir, DisplayName = "PruneScenario" };
                        pruneConfig.SourceFolders.Add(pruneSourceFolder);

                        WriteTextFile(Path.Combine(pruneSourceDir, "counter.txt"), BuildValidationPayload("prune-init", 220));
                        pruneLatestSnapshot = CaptureSnapshot(pruneSourceDir);

                        return await Task.FromResult($"Workspace: {validationRoot}");
                    },
                    steps).ConfigureAwait(false);

                if (continueValidation)
                {
                    continueValidation = await RunStepAsync(
                        I18n.GetString("CoreValidation_Step_InitialFullBackup"),
                        async () =>
                        {
                            fullEntry = await BackupExpectNewEntryAsync(mainConfig!, mainSourceFolder!, "CoreValidation Full", "Full").ConfigureAwait(false);
                            return fullEntry.FileName;
                        },
                        steps).ConfigureAwait(false);
                }

                if (continueValidation)
                {
                    continueValidation = await RunStepAsync(
                        I18n.GetString("CoreValidation_Step_LegacyMetadataMigration"),
                        async () =>
                        {
                            RewriteMetadataAsLegacyOnly(mainConfig!, mainSourceFolder!);

                            var beforeEntries = GetHistoryEntries(mainConfig!.Id, mainSourceFolder!.DisplayName);
                            bool created = await BackupService.BackupFolderAsync(mainConfig, mainSourceFolder, "CoreValidation Legacy Metadata").ConfigureAwait(false);
                            if (created)
                            {
                                throw new InvalidOperationException("Legacy metadata migration probe unexpectedly created a new archive.");
                            }

                            var afterEntries = GetHistoryEntries(mainConfig.Id, mainSourceFolder.DisplayName);
                            if (afterEntries.Count != beforeEntries.Count)
                            {
                                throw new InvalidOperationException("Legacy metadata migration probe changed history entries.");
                            }

                            AssertMetadataStoreMigrated(mainConfig, mainSourceFolder, fullEntry!.FileName);
                            return "Legacy metadata was migrated lazily and kept skip-if-unchanged behavior.";
                        },
                        steps).ConfigureAwait(false);
                }

                if (continueValidation)
                {
                    continueValidation = await RunStepAsync(
                        I18n.GetString("CoreValidation_Step_NoChangeSkip"),
                        async () =>
                        {
                            await BackupExpectNoNewEntryAsync(mainConfig!, mainSourceFolder!, "CoreValidation NoChange").ConfigureAwait(false);
                            return "No extra archive created when source state was unchanged.";
                        },
                        steps).ConfigureAwait(false);
                }

                if (continueValidation)
                {
                    continueValidation = await RunStepAsync(
                        I18n.GetString("CoreValidation_Step_SmartIncrementalBackup"),
                        async () =>
                        {
                            WriteTextFile(Path.Combine(mainSourceDir, "alpha.txt"), BuildValidationPayload("alpha-smart-1", 360));
                            WriteTextFile(Path.Combine(mainSourceDir, "gamma.txt"), BuildValidationPayload("gamma-smart-1", 200));
                            snapshotSmartOne = CaptureSnapshot(mainSourceDir);

                            smartEntryOne = await BackupExpectNewEntryAsync(mainConfig!, mainSourceFolder!, "CoreValidation Smart 1", "Smart").ConfigureAwait(false);
                            return smartEntryOne.FileName;
                        },
                        steps).ConfigureAwait(false);
                }

                if (continueValidation)
                {
                    continueValidation = await RunStepAsync(
                        I18n.GetString("CoreValidation_Step_SharedLockBackup"),
                        async () =>
                        {
                            string lockedFilePath = Path.Combine(mainSourceDir, "region", "0.0.mca");
                            WriteTextFile(lockedFilePath, BuildValidationPayload("region-locked", 260));

                            sharedLockHandle?.Dispose();
                            sharedLockHandle = new FileStream(lockedFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                            if (!FileLockService.IsFileLocked(lockedFilePath))
                            {
                                throw new InvalidOperationException("Shared-lock validation file was not observed as locked.");
                            }

                            smartEntryTwo = await BackupExpectNewEntryAsync(mainConfig!, mainSourceFolder!, "CoreValidation Smart 2", "Smart").ConfigureAwait(false);
                            return smartEntryTwo.FileName;
                        },
                        steps).ConfigureAwait(false);
                }

                if (continueValidation)
                {
                    continueValidation = await RunStepAsync(
                        I18n.GetString("CoreValidation_Step_DeletionOnlyBackup"),
                        async () =>
                        {
                            sharedLockHandle?.Dispose();
                            sharedLockHandle = null;

                            string deletionTarget = Path.Combine(mainSourceDir, "gamma.txt");
                            if (!File.Exists(deletionTarget))
                            {
                                throw new InvalidOperationException("Deletion-only validation file was not found.");
                            }

                            File.Delete(deletionTarget);
                            snapshotDeletionOnly = CaptureSnapshot(mainSourceDir);

                            deletionOnlyEntry = await BackupExpectNewEntryAsync(mainConfig!, mainSourceFolder!, "CoreValidation DeleteOnly", "Smart").ConfigureAwait(false);
                            return deletionOnlyEntry.FileName;
                        },
                        steps).ConfigureAwait(false);
                }

                if (continueValidation)
                {
                    continueValidation = await RunStepAsync(
                        I18n.GetString("CoreValidation_Step_CleanRestore"),
                        async () =>
                        {
                            await BackupService.RestoreBackupAsync(mainConfig!, mainRestoreFolder!, deletionOnlyEntry!, BackupService.RestoreMode.Clean).ConfigureAwait(false);
                            AssertSnapshotEquals(mainRestoreDir, snapshotDeletionOnly!, "Clean restore latest snapshot");
                            return "Latest Smart restore matched the expected file set.";
                        },
                        steps).ConfigureAwait(false);
                }

                if (continueValidation)
                {
                    continueValidation = await RunStepAsync(
                        I18n.GetString("CoreValidation_Step_OverwriteRestore"),
                        async () =>
                        {
                            string extraFilePath = Path.Combine(mainRestoreDir, "manual-extra.txt");
                            WriteTextFile(extraFilePath, "manual-extra");

                            await BackupService.RestoreBackupAsync(mainConfig!, mainRestoreFolder!, smartEntryOne!, BackupService.RestoreMode.Overwrite).ConfigureAwait(false);

                            var overwriteExpected = MergeSnapshots(snapshotDeletionOnly!, snapshotSmartOne!);
                            overwriteExpected[NormalizeRelativePath("manual-extra.txt")] = "manual-extra";
                            AssertSnapshotEquals(mainRestoreDir, overwriteExpected, "Overwrite restore intermediate snapshot");
                            return "Overwrite restore preserved extra file while restoring archive contents.";
                        },
                        steps).ConfigureAwait(false);
                }

                if (continueValidation)
                {
                    continueValidation = await RunStepAsync(
                        I18n.GetString("CoreValidation_Step_SafeDelete"),
                        async () =>
                        {
                            var deleteResult = await BackupService.DeleteBackupAsync(mainConfig!, mainSourceFolder!, smartEntryOne!, BackupDeleteMode.LocalArchiveAndRecord).ConfigureAwait(false);
                            if (!deleteResult.Success)
                            {
                                throw new InvalidOperationException($"Safe delete failed: {deleteResult.Message}");
                            }

                            if (!deleteResult.ArchiveDeleted)
                            {
                                throw new InvalidOperationException("Safe delete reported success but no archive was removed.");
                            }

                            await BackupService.RestoreBackupAsync(mainConfig!, mainRestoreFolder!, deletionOnlyEntry!, BackupService.RestoreMode.Clean).ConfigureAwait(false);
                            AssertSnapshotEquals(mainRestoreDir, snapshotDeletionOnly!, "Restore after safe delete");
                            return "Deleting an incremental archive preserved latest restore correctness.";
                        },
                        steps).ConfigureAwait(false);
                }

                if (continueValidation)
                {
                    continueValidation = await RunStepAsync(
                        I18n.GetString("CoreValidation_Step_KeepCountPrune"),
                        async () =>
                        {
                            await RunPruneScenarioAsync(pruneConfig!, pruneSourceFolder!, pruneRestoreFolder!, pruneSourceDir, pruneBackupRoot, snapshot => pruneLatestSnapshot = snapshot).ConfigureAwait(false);

                            AssertSnapshotEquals(pruneRestoreDir, pruneLatestSnapshot!, "Restore after keep-count pruning");
                            return "Keep-count pruning retained a restorable latest snapshot.";
                        },
                        steps).ConfigureAwait(false);
                }
            }
            finally
            {
                sharedLockHandle?.Dispose();

                if (mainConfig != null)
                {
                    HistoryService.RemoveEntriesForConfig(mainConfig.Id);
                }

                if (pruneConfig != null)
                {
                    HistoryService.RemoveEntriesForConfig(pruneConfig.Id);
                }

                DeleteDirectorySafe(validationRoot);
            }

            report.FinishedAtUtc = DateTime.UtcNow;
            report.Steps = steps;
            report.Success = steps.All(step => step.Success);
            report.Summary = report.Success
                ? I18n.GetString("CoreValidation_Summary_Passed")
                : steps.FirstOrDefault(step => !step.Success)?.Details ?? I18n.GetString("CoreValidation_Summary_Failed");

            return report;
        }

        private static async Task RunPruneScenarioAsync(
            BackupConfig pruneConfig,
            ManagedFolder pruneSourceFolder,
            ManagedFolder pruneRestoreFolder,
            string pruneSourceDir,
            string pruneBackupRoot,
            Action<Dictionary<string, string>> updateLatestSnapshot)
        {
            async Task<HistoryItem> BackupPruneAsync(string comment, string expectedType)
            {
                var entry = await BackupExpectNewEntryAsync(pruneConfig, pruneSourceFolder, comment, expectedType).ConfigureAwait(false);
                string archiveDir = Path.Combine(pruneBackupRoot, pruneSourceFolder.DisplayName);
                AssertArchiveCountAtMost(archiveDir, pruneConfig.Archive.Format, pruneConfig.Archive.KeepCount);
                return entry;
            }

            updateLatestSnapshot(CaptureSnapshot(pruneSourceDir));
            await BackupPruneAsync("Prune Full", "Full").ConfigureAwait(false);

            WriteTextFile(Path.Combine(pruneSourceDir, "counter.txt"), BuildValidationPayload("prune-smart-1", 240));
            updateLatestSnapshot(CaptureSnapshot(pruneSourceDir));
            await BackupPruneAsync("Prune Smart 1", "Smart").ConfigureAwait(false);

            WriteTextFile(Path.Combine(pruneSourceDir, "counter.txt"), BuildValidationPayload("prune-smart-2", 260));
            updateLatestSnapshot(CaptureSnapshot(pruneSourceDir));
            await BackupPruneAsync("Prune Smart 2", "Smart").ConfigureAwait(false);

            WriteTextFile(Path.Combine(pruneSourceDir, "counter.txt"), BuildValidationPayload("prune-smart-3", 280));
            updateLatestSnapshot(CaptureSnapshot(pruneSourceDir));
            await BackupPruneAsync("Prune Smart 3", "Smart").ConfigureAwait(false);

            var latestEntry = GetHistoryEntries(pruneConfig.Id, pruneSourceFolder.DisplayName)
                .OrderByDescending(item => item.Timestamp)
                .FirstOrDefault();
            if (latestEntry == null)
            {
                throw new InvalidOperationException("Prune scenario could not locate the latest history entry.");
            }

            await BackupService.RestoreBackupAsync(pruneConfig, pruneRestoreFolder, latestEntry, BackupService.RestoreMode.Clean).ConfigureAwait(false);
        }

        private static void NotifyAutomaticResult(CoreFeatureValidationReport report)
        {
            if (report.Success)
            {
                NotificationService.ShowSuccess(report.Summary, I18n.GetString("CoreValidation_AutoNotificationTitle"), 8000);
                return;
            }

            NotificationService.ShowInfoBar(
                I18n.GetString("CoreValidation_AutoNotificationTitle"),
                report.Summary,
                NotificationSeverity.Error,
                0,
                () => NavigationService.NavigateTo("Settings"));
        }

        private static void PersistReport(CoreFeatureValidationReport report, bool automatic)
        {
            var settings = ConfigService.CurrentConfig?.GlobalSettings;
            if (settings == null)
            {
                return;
            }

            if (automatic || report.Success)
            {
                settings.HasTriggeredInitialCoreValidation = true;
            }

            settings.LastCoreValidationPassed = report.Success;
            settings.LastCoreValidationUtc = report.FinishedAtUtc;
            settings.LastCoreValidationSummary = report.Summary;
            ConfigService.Save();
        }

        private static BackupConfig CreateValidationConfig(string name, string destinationPath, int keepCount)
        {
            return new BackupConfig
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                DestinationPath = destinationPath,
                ConfigType = "Default",
                IsEncrypted = false,
                Archive = new ArchiveSettings
                {
                    Mode = BackupMode.Incremental,
                    Format = "7z",
                    CompressionLevel = 1,
                    Method = "LZMA2",
                    SkipIfUnchanged = true,
                    BackupBeforeRestore = false,
                    SafeRestoreEnabled = true,
                    VerifyArchiveBeforeRestore = true,
                    MaxSmartBackupsPerFull = 12,
                    SafeDeleteEnabled = true,
                    KeepCount = keepCount,
                    CpuThreads = 1,
                    FileTypeHandlingEnabled = false
                },
                Filters = new FilterSettings()
            };
        }

        private static async Task<bool> RunStepAsync(string name, Func<Task<string>> action, List<CoreFeatureValidationStepResult> steps)
        {
            UpdateStatus(I18n.Format("CoreValidation_Status_Step", name));

            var stopwatch = Stopwatch.StartNew();
            try
            {
                string details = await action().ConfigureAwait(false);
                steps.Add(new CoreFeatureValidationStepResult
                {
                    Name = name,
                    Success = true,
                    Details = details,
                    Duration = stopwatch.Elapsed
                });
                LogService.Log($"[CoreValidation] {name}: {details}", LogLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                steps.Add(new CoreFeatureValidationStepResult
                {
                    Name = name,
                    Success = false,
                    Details = ex.Message,
                    Duration = stopwatch.Elapsed
                });
                LogService.Log($"[CoreValidation] {name} failed: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        private static async Task<HistoryItem> BackupExpectNewEntryAsync(
            BackupConfig config,
            ManagedFolder folder,
            string comment,
            string expectedBackupType)
        {
            await DelayBeforeBackupAsync().ConfigureAwait(false);

            var beforeEntries = GetHistoryEntries(config.Id, folder.DisplayName);
            var knownFiles = new HashSet<string>(beforeEntries.Select(item => item.FileName), StringComparer.OrdinalIgnoreCase);
            var baselineTimestamp = beforeEntries.Count == 0
                ? DateTime.MinValue
                : beforeEntries.Max(item => item.Timestamp);

            bool created = await BackupService.BackupFolderAsync(config, folder, comment).ConfigureAwait(false);
            if (!created)
            {
                LogService.Log($"[CoreValidation] Backup '{comment}' did not create archive. beforeEntries={beforeEntries.Count}.", LogLevel.Warning);
                throw new InvalidOperationException($"Backup '{comment}' did not generate a new archive.");
            }

            var afterEntries = GetHistoryEntries(config.Id, folder.DisplayName);
            var newEntriesByTimestamp = afterEntries
                .Where(item => item.Timestamp > baselineTimestamp)
                .OrderByDescending(item => item.Timestamp)
                .ToList();

            var newEntriesByFileName = afterEntries
                .Where(item => !knownFiles.Contains(item.FileName))
                .OrderByDescending(item => item.Timestamp)
                .ToList();

            if (newEntriesByTimestamp.Count != 1)
            {
                LogService.Log(
                    $"[CoreValidation] Backup '{comment}' history delta mismatch. before={beforeEntries.Count}, after={afterEntries.Count}, baseline={baselineTimestamp:O}, byTimestamp={newEntriesByTimestamp.Count}, byFileName={newEntriesByFileName.Count}, byTimestampEntries={FormatHistoryEntriesForLog(newEntriesByTimestamp)}, byFileNameEntries={FormatHistoryEntriesForLog(newEntriesByFileName)}",
                    LogLevel.Warning);
                throw new InvalidOperationException($"Backup '{comment}' produced {newEntriesByTimestamp.Count} new history entries instead of exactly 1.");
            }

            var newEntry = newEntriesByTimestamp[0];
            if (!string.Equals(newEntry.BackupType, expectedBackupType, StringComparison.OrdinalIgnoreCase))
            {
                LogService.Log(
                    $"[CoreValidation] Backup '{comment}' type mismatch. expected={expectedBackupType}, actual={newEntry.BackupType}, file={newEntry.FileName}, timestamp={newEntry.Timestamp:O}.",
                    LogLevel.Warning);
                throw new InvalidOperationException($"Backup '{comment}' expected type '{expectedBackupType}' but received '{newEntry.BackupType}'.");
            }

            string? archivePath = HistoryService.GetBackupFilePath(config, folder, newEntry);
            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            {
                LogService.Log(
                    $"[CoreValidation] Backup '{comment}' archive missing on disk. file={newEntry.FileName}, resolvedPath={archivePath ?? "<null>"}.",
                    LogLevel.Warning);
                throw new InvalidOperationException($"Archive for backup '{comment}' was not found on disk.");
            }

            return newEntry;
        }

        private static async Task BackupExpectNoNewEntryAsync(
            BackupConfig config,
            ManagedFolder folder,
            string comment)
        {
            await DelayBeforeBackupAsync().ConfigureAwait(false);

            var beforeEntries = GetHistoryEntries(config.Id, folder.DisplayName);
            bool created = await BackupService.BackupFolderAsync(config, folder, comment).ConfigureAwait(false);
            if (created)
            {
                throw new InvalidOperationException($"Backup '{comment}' unexpectedly created a new archive.");
            }

            var afterEntries = GetHistoryEntries(config.Id, folder.DisplayName);
            if (afterEntries.Count != beforeEntries.Count)
            {
                throw new InvalidOperationException($"Backup '{comment}' changed history count from {beforeEntries.Count} to {afterEntries.Count}.");
            }
        }

        private static async Task DelayBeforeBackupAsync()
        {
            await Task.Delay(1100).ConfigureAwait(false);
        }

        private static IReadOnlyList<HistoryItem> GetHistoryEntries(string configId, string folderName)
        {
            return HistoryService.GetEntriesForFolder(configId, folderName)
                .OrderBy(item => item.Timestamp)
                .ToList();
        }

        private static string FormatHistoryEntriesForLog(IReadOnlyList<HistoryItem> entries, int maxItems = 6)
        {
            if (entries.Count == 0)
            {
                return "[]";
            }

            var sample = entries
                .Take(maxItems)
                .Select(item => $"{item.Timestamp:yyyy-MM-dd HH:mm:ss.fff}|{item.BackupType}|{item.FileName}");

            string suffix = entries.Count > maxItems ? ", ..." : string.Empty;
            return $"[{string.Join(", ", sample)}{suffix}]";
        }

        private static Dictionary<string, string> CaptureSnapshot(string rootDir)
        {
            var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(rootDir))
            {
                return snapshot;
            }

            foreach (var filePath in Directory.EnumerateFiles(rootDir, "*", SearchOption.AllDirectories))
            {
                string relativePath = NormalizeRelativePath(Path.GetRelativePath(rootDir, filePath));
                snapshot[relativePath] = ReadTextFileForSnapshot(filePath);
            }

            return snapshot;
        }

        private static string ReadTextFileForSnapshot(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        private static Dictionary<string, string> MergeSnapshots(
            IDictionary<string, string> baseSnapshot,
            IDictionary<string, string> overlaySnapshot)
        {
            var merged = new Dictionary<string, string>(baseSnapshot, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in overlaySnapshot)
            {
                merged[pair.Key] = pair.Value;
            }

            return merged;
        }

        private static void AssertSnapshotEquals(string targetDir, IDictionary<string, string> expectedSnapshot, string scenario)
        {
            var actualSnapshot = CaptureSnapshot(targetDir);
            if (actualSnapshot.Count != expectedSnapshot.Count)
            {
                throw new InvalidOperationException($"{scenario} file count mismatch. Expected {expectedSnapshot.Count}, got {actualSnapshot.Count}.");
            }

            foreach (var expectedPair in expectedSnapshot)
            {
                if (!actualSnapshot.TryGetValue(expectedPair.Key, out var actualContent))
                {
                    throw new InvalidOperationException($"{scenario} is missing file '{expectedPair.Key}'.");
                }

                if (!string.Equals(actualContent, expectedPair.Value, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"{scenario} content mismatch for '{expectedPair.Key}'.");
                }
            }
        }

        private static void AssertArchiveCountAtMost(string archiveDir, string format, int keepCount)
        {
            if (keepCount <= 0)
            {
                return;
            }

            int archiveCount = GetArchiveFiles(archiveDir, format).Count;
            bool hasTempArtifacts = Directory.Exists(archiveDir)
                && (Directory.EnumerateDirectories(archiveDir, "__FolderRewind_SafeDelete_*", SearchOption.TopDirectoryOnly).Any()
                    || Directory.EnumerateFiles(archiveDir, $"*.{format}.tmp*", SearchOption.TopDirectoryOnly).Any());

            if (archiveCount <= keepCount && !hasTempArtifacts)
            {
                return;
            }

            throw new InvalidOperationException($"Archive pruning did not settle before backup completion. Remaining archives: {archiveCount}, temp artifacts: {hasTempArtifacts}.");
        }

        private static IReadOnlyList<string> GetArchiveFiles(string archiveDir, string format)
        {
            if (!Directory.Exists(archiveDir))
            {
                return Array.Empty<string>();
            }

            return Directory.EnumerateFiles(archiveDir, $"*.{format}", SearchOption.TopDirectoryOnly)
                .ToList();
        }

        private static string BuildValidationPayload(string label, int lineCount)
        {
            return string.Join(
                Environment.NewLine,
                Enumerable.Range(0, Math.Max(lineCount, 1)).Select(index => $"{label}:{index:D4}:{Guid.NewGuid():N}"));
        }

        private static void WriteTextFile(string path, string content)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, content);
        }

        private static void RewriteMetadataAsLegacyOnly(BackupConfig config, ManagedFolder folder)
        {
            if (!BackupStoragePathService.TryResolveBackupStoragePaths(
                config.DestinationPath ?? string.Empty,
                folder.DisplayName,
                folder.Path,
                out _,
                out _,
                out var metadataDir))
            {
                throw new InvalidOperationException("Validation metadata directory could not be resolved.");
            }

            var loadResult = BackupMetadataStoreService.LoadAsync(metadataDir).GetAwaiter().GetResult();
            if (loadResult.State == null)
            {
                throw new InvalidOperationException("Validation metadata state was not found.");
            }

            var legacyMetadata = new BackupMetadata
            {
                Version = "2.0",
                LastBackupTime = loadResult.State.LastBackupTime,
                LastBackupFileName = loadResult.State.LastBackupFileName,
                BasedOnFullBackup = loadResult.State.BasedOnFullBackup,
                FileStates = new Dictionary<string, FileState>(loadResult.State.FileStates, StringComparer.OrdinalIgnoreCase),
                BackupRecords = loadResult.Records.Values
                    .Select(record => new BackupChangeRecord
                    {
                        ArchiveFileName = record.ArchiveFileName,
                        BackupType = record.BackupType,
                        BasedOnFullBackup = record.BasedOnFullBackup,
                        PreviousBackupFileName = record.PreviousBackupFileName,
                        CreatedAtUtc = record.CreatedAtUtc,
                        AddedFiles = record.AddedFiles.ToList(),
                        ModifiedFiles = record.ModifiedFiles.ToList(),
                        DeletedFiles = record.DeletedFiles.ToList(),
                        FullFileList = record.FullFileList.ToList()
                    })
                    .OrderBy(record => record.CreatedAtUtc)
                    .ThenBy(record => record.ArchiveFileName, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };

            Directory.CreateDirectory(metadataDir);
            File.WriteAllText(
                Path.Combine(metadataDir, "metadata.json"),
                JsonSerializer.Serialize(legacyMetadata, AppJsonContext.Default.BackupMetadata));

            string statePath = Path.Combine(metadataDir, "state.json");
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }

            string legacyBackupPath = Path.Combine(metadataDir, "metadata.legacy.json");
            if (File.Exists(legacyBackupPath))
            {
                File.Delete(legacyBackupPath);
            }

            string recordsDir = Path.Combine(metadataDir, "records");
            if (Directory.Exists(recordsDir))
            {
                Directory.Delete(recordsDir, true);
            }
        }

        private static void AssertMetadataStoreMigrated(BackupConfig config, ManagedFolder folder, string archiveFileName)
        {
            if (!BackupStoragePathService.TryResolveBackupStoragePaths(
                config.DestinationPath ?? string.Empty,
                folder.DisplayName,
                folder.Path,
                out _,
                out _,
                out var metadataDir))
            {
                throw new InvalidOperationException("Validation metadata directory could not be resolved.");
            }

            string statePath = Path.Combine(metadataDir, "state.json");
            if (!File.Exists(statePath))
            {
                throw new InvalidOperationException("Lazy migration did not recreate state.json.");
            }

            string recordPath = Path.Combine(metadataDir, "records", archiveFileName + ".json");
            if (!File.Exists(recordPath))
            {
                throw new InvalidOperationException("Lazy migration did not recreate the archive record file.");
            }

            string legacySourcePath = Path.Combine(metadataDir, "metadata.json");
            if (File.Exists(legacySourcePath))
            {
                throw new InvalidOperationException("Lazy migration left the legacy metadata.json in place.");
            }
        }

        private static string NormalizeRelativePath(string relativePath)
        {
            return relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        }

        private static void DeleteDirectorySafe(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return;
            }

            try
            {
                foreach (var filePath in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.SetAttributes(filePath, FileAttributes.Normal);
                    }
                    catch
                    {
                    }
                }

                Directory.Delete(path, true);
            }
            catch (Exception ex)
            {
                LogService.Log($"[CoreValidation] Cleanup failed for '{path}': {ex.Message}", LogLevel.Warning);
            }
        }

        private static void UpdateStatus(string statusText)
        {
            StatusText = string.IsNullOrWhiteSpace(statusText)
                ? I18n.GetString("CoreValidation_Status_Idle")
                : statusText;
            RaiseStateChanged();
        }

        private static void RaiseStateChanged()
        {
            try
            {
                StateChanged?.Invoke();
            }
            catch
            {
            }
        }
    }
}

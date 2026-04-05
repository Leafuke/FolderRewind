using FolderRewind.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public static class BackupMetadataStoreService
    {
        public sealed class BackupMetadataLoadResult
        {
            public BackupMetadataState? State { get; init; }
            public IReadOnlyDictionary<string, BackupChangeRecord> Records { get; init; } = new Dictionary<string, BackupChangeRecord>(StringComparer.OrdinalIgnoreCase);
            public bool MetadataExists { get; init; }
            public bool StateLoadFailed { get; init; }
            public bool RecordLoadFailed { get; init; }
            public bool HasMissingRequestedRecords { get; init; }
            public bool UsedLegacyMigration { get; init; }
            public bool UsedLegacyFallback { get; init; }
        }

        private const string ServiceName = nameof(BackupMetadataStoreService);
        private const string StateFileName = "state.json";
        private const string RecordsDirectoryName = "records";
        private const string LegacyMetadataFileName = "metadata.json";
        private const string LegacyBackupMetadataFileName = "metadata.legacy.json";
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> MetadataLocks = new(StringComparer.OrdinalIgnoreCase);

        public static async Task<BackupMetadataLoadResult> LoadAsync(string metadataDir, IEnumerable<string>? archiveFileNames = null)
        {
            if (string.IsNullOrWhiteSpace(metadataDir))
            {
                return new BackupMetadataLoadResult();
            }

            var gate = GetGate(metadataDir);
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await LoadCoreAsync(metadataDir, archiveFileNames, logMissingRequestedRecords: true).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        public static async Task<bool> SaveAsync(string metadataDir, BackupMetadataState state, BackupChangeRecord record)
        {
            if (string.IsNullOrWhiteSpace(metadataDir))
            {
                return false;
            }

            var gate = GetGate(metadataDir);
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var existing = await LoadCoreAsync(metadataDir, archiveFileNames: null, logMissingRequestedRecords: false).ConfigureAwait(false);
                var normalizedState = NormalizeState(state);
                var normalizedRecord = NormalizeRecord(record);

                Directory.CreateDirectory(metadataDir);
                Directory.CreateDirectory(GetRecordsDirectoryPath(metadataDir));

                if (!await WriteStateAsync(GetStatePath(metadataDir), normalizedState).ConfigureAwait(false))
                {
                    return false;
                }

                if (existing.Records.Count > 0)
                {
                    foreach (var existingRecord in existing.Records.Values)
                    {
                        if (!await WriteRecordAsync(metadataDir, existingRecord).ConfigureAwait(false))
                        {
                            return false;
                        }
                    }
                }

                if (!await WriteRecordAsync(metadataDir, normalizedRecord).ConfigureAwait(false))
                {
                    return false;
                }

                TryArchiveLegacyMetadata(metadataDir);
                return true;
            }
            finally
            {
                gate.Release();
            }
        }

        public static bool SynchronizeAfterArchiveDeletion(
            string metadataDir,
            string deletedFileName,
            string? renamedOldFileName,
            string? renamedNewFileName,
            string? renamedBackupType)
        {
            if (string.IsNullOrWhiteSpace(metadataDir) || string.IsNullOrWhiteSpace(deletedFileName))
            {
                return true;
            }

            var gate = GetGate(metadataDir);
            gate.Wait();
            try
            {
                var loadResult = LoadCoreAsync(metadataDir, archiveFileNames: null, logMissingRequestedRecords: false)
                    .GetAwaiter()
                    .GetResult();
                var state = loadResult.State;
                if (state == null)
                {
                    return !loadResult.MetadataExists || !loadResult.StateLoadFailed;
                }

                var orderedRecords = loadResult.Records.Values
                    .Select(CloneRecord)
                    .OrderBy(r => r.CreatedAtUtc)
                    .ThenBy(r => r.ArchiveFileName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                bool invalidateState = false;
                int deletedIndex = orderedRecords.FindIndex(r => string.Equals(r.ArchiveFileName, deletedFileName, StringComparison.OrdinalIgnoreCase));
                BackupChangeRecord? deletedRecord = deletedIndex >= 0 ? orderedRecords[deletedIndex] : null;
                BackupChangeRecord? previousRecord = deletedIndex > 0 ? orderedRecords[deletedIndex - 1] : null;

                BackupChangeRecord? successorRecord = null;
                if (!string.IsNullOrWhiteSpace(renamedOldFileName))
                {
                    successorRecord = orderedRecords.FirstOrDefault(r => string.Equals(r.ArchiveFileName, renamedOldFileName, StringComparison.OrdinalIgnoreCase));
                }
                else if (deletedIndex >= 0 && deletedIndex + 1 < orderedRecords.Count)
                {
                    successorRecord = orderedRecords[deletedIndex + 1];
                }

                if (deletedRecord != null && successorRecord != null)
                {
                    RebaseSuccessorBackupRecord(previousRecord, deletedRecord, successorRecord, renamedNewFileName, renamedBackupType);
                }

                if (!string.IsNullOrWhiteSpace(renamedOldFileName) && !string.IsNullOrWhiteSpace(renamedNewFileName))
                {
                    foreach (var current in orderedRecords)
                    {
                        if (string.Equals(current.ArchiveFileName, renamedOldFileName, StringComparison.OrdinalIgnoreCase))
                        {
                            current.ArchiveFileName = renamedNewFileName;
                            if (!string.IsNullOrWhiteSpace(renamedBackupType))
                            {
                                current.BackupType = renamedBackupType;
                            }
                        }

                        if (string.Equals(current.PreviousBackupFileName, renamedOldFileName, StringComparison.OrdinalIgnoreCase))
                        {
                            current.PreviousBackupFileName = renamedNewFileName;
                        }

                        if (string.Equals(current.BasedOnFullBackup, renamedOldFileName, StringComparison.OrdinalIgnoreCase))
                        {
                            current.BasedOnFullBackup = renamedNewFileName;
                        }
                    }

                    if (string.Equals(state.LastBackupFileName, renamedOldFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        state.LastBackupFileName = renamedNewFileName;
                    }

                    if (string.Equals(state.BasedOnFullBackup, renamedOldFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        state.BasedOnFullBackup = renamedNewFileName;
                    }
                }

                orderedRecords.RemoveAll(r => string.Equals(r.ArchiveFileName, deletedFileName, StringComparison.OrdinalIgnoreCase));

                foreach (var current in orderedRecords)
                {
                    if (string.Equals(current.PreviousBackupFileName, deletedFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        current.PreviousBackupFileName = previousRecord?.ArchiveFileName ?? string.Empty;
                    }

                    if (string.Equals(current.BasedOnFullBackup, deletedFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(renamedNewFileName)
                            && string.Equals(renamedBackupType, "Full", StringComparison.OrdinalIgnoreCase))
                        {
                            current.BasedOnFullBackup = renamedNewFileName;
                        }
                        else
                        {
                            invalidateState = true;
                        }
                    }
                }

                if (string.Equals(state.LastBackupFileName, deletedFileName, StringComparison.OrdinalIgnoreCase))
                {
                    invalidateState = true;
                }

                if (string.Equals(state.BasedOnFullBackup, deletedFileName, StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(renamedNewFileName)
                        && string.Equals(renamedBackupType, "Full", StringComparison.OrdinalIgnoreCase))
                    {
                        state.BasedOnFullBackup = renamedNewFileName;
                    }
                    else
                    {
                        invalidateState = true;
                    }
                }

                if (invalidateState)
                {
                    TryDeleteFile(GetStatePath(metadataDir));
                }
                else if (!WriteStateAsync(GetStatePath(metadataDir), state).GetAwaiter().GetResult())
                {
                    return false;
                }

                if (!PersistRecordSnapshotAsync(metadataDir, orderedRecords).GetAwaiter().GetResult())
                {
                    return false;
                }

                TryArchiveLegacyMetadata(metadataDir);
                return true;
            }
            finally
            {
                gate.Release();
            }
        }

        private static async Task<BackupMetadataLoadResult> LoadCoreAsync(
            string metadataDir,
            IEnumerable<string>? archiveFileNames,
            bool logMissingRequestedRecords)
        {
            string statePath = GetStatePath(metadataDir);
            string legacyPath = GetLegacyMetadataPath(metadataDir);
            string recordsDir = GetRecordsDirectoryPath(metadataDir);

            bool stateExists = File.Exists(statePath);
            bool legacyExists = File.Exists(legacyPath);
            bool recordsDirExists = Directory.Exists(recordsDir);
            bool metadataExists = stateExists
                || legacyExists
                || File.Exists(GetLegacyBackupMetadataPath(metadataDir))
                || (recordsDirExists && Directory.EnumerateFiles(recordsDir, "*.json", SearchOption.TopDirectoryOnly).Any());

            bool usedLegacyMigration = false;
            bool usedLegacyFallback = false;

            if (legacyExists)
            {
                bool migrated = await TryMigrateLegacyAsync(metadataDir).ConfigureAwait(false);
                if (migrated)
                {
                    usedLegacyMigration = true;
                    stateExists = File.Exists(statePath);
                    recordsDirExists = Directory.Exists(recordsDir);
                    metadataExists = true;
                }
                else
                {
                    var legacyMetadata = await TryLoadLegacyMetadataAsync(legacyPath).ConfigureAwait(false);
                    if (legacyMetadata != null)
                    {
                        usedLegacyFallback = true;
                        return CreateLegacyLoadResult(legacyMetadata, archiveFileNames, metadataExists, usedLegacyMigration, usedLegacyFallback);
                    }
                }
            }

            if (!stateExists)
            {
                return new BackupMetadataLoadResult
                {
                    MetadataExists = metadataExists
                };
            }

            var state = await TryLoadStateAsync(statePath).ConfigureAwait(false);
            if (state == null)
            {
                return new BackupMetadataLoadResult
                {
                    MetadataExists = true,
                    StateLoadFailed = true,
                    UsedLegacyMigration = usedLegacyMigration,
                    UsedLegacyFallback = usedLegacyFallback
                };
            }

            var requestedArchiveFileNames = NormalizeRequestedArchiveNames(archiveFileNames);
            var recordMap = await LoadRecordMapAsync(metadataDir, requestedArchiveFileNames).ConfigureAwait(false);

            bool hasMissingRequestedRecords = false;
            if (requestedArchiveFileNames != null)
            {
                hasMissingRequestedRecords = requestedArchiveFileNames.Any(name => !recordMap.ContainsKey(name));
                if (hasMissingRequestedRecords && logMissingRequestedRecords)
                {
                    foreach (var missing in requestedArchiveFileNames.Where(name => !recordMap.ContainsKey(name)))
                    {
                        LogService.LogWarning(I18n.Format("BackupMetadataStore_Log_RecordMissingFallback", missing), ServiceName);
                    }
                }
            }

            bool recordLoadFailed = requestedArchiveFileNames == null
                ? recordsDirExists && recordMap.Count == 0 && Directory.EnumerateFiles(recordsDir, "*.json", SearchOption.TopDirectoryOnly).Any()
                : false;

            return new BackupMetadataLoadResult
            {
                State = state,
                Records = recordMap,
                MetadataExists = true,
                StateLoadFailed = false,
                RecordLoadFailed = recordLoadFailed,
                HasMissingRequestedRecords = hasMissingRequestedRecords,
                UsedLegacyMigration = usedLegacyMigration,
                UsedLegacyFallback = usedLegacyFallback
            };
        }

        private static BackupMetadataLoadResult CreateLegacyLoadResult(
            BackupMetadata metadata,
            IEnumerable<string>? archiveFileNames,
            bool metadataExists,
            bool usedLegacyMigration,
            bool usedLegacyFallback)
        {
            var normalizedMetadata = NormalizeLegacyMetadata(metadata);
            var state = ConvertToState(normalizedMetadata);
            var requestedArchiveFileNames = NormalizeRequestedArchiveNames(archiveFileNames);

            var records = normalizedMetadata.BackupRecords
                .Where(r => !string.IsNullOrWhiteSpace(r.ArchiveFileName))
                .GroupBy(r => r.ArchiveFileName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => CloneRecord(group.OrderByDescending(r => r.CreatedAtUtc).First()),
                    StringComparer.OrdinalIgnoreCase);

            if (requestedArchiveFileNames != null)
            {
                records = records
                    .Where(kvp => requestedArchiveFileNames.Contains(kvp.Key))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
            }

            return new BackupMetadataLoadResult
            {
                State = state,
                Records = records,
                MetadataExists = metadataExists,
                UsedLegacyMigration = usedLegacyMigration,
                UsedLegacyFallback = usedLegacyFallback,
                HasMissingRequestedRecords = requestedArchiveFileNames != null
                    && requestedArchiveFileNames.Any(name => !records.ContainsKey(name))
            };
        }

        private static async Task<bool> TryMigrateLegacyAsync(string metadataDir)
        {
            string legacyPath = GetLegacyMetadataPath(metadataDir);
            if (!File.Exists(legacyPath))
            {
                return true;
            }

            try
            {
                var legacy = await TryLoadLegacyMetadataAsync(legacyPath).ConfigureAwait(false);
                if (legacy == null)
                {
                    LogService.LogWarning(I18n.Format("BackupMetadataStore_Log_LegacyMigrationFailedFallback", I18n.GetString("BackupService_Log_MetadataCorruptedFallbackFull")), ServiceName);
                    return false;
                }

                Directory.CreateDirectory(metadataDir);
                Directory.CreateDirectory(GetRecordsDirectoryPath(metadataDir));

                if (!await WriteStateAsync(GetStatePath(metadataDir), ConvertToState(legacy)).ConfigureAwait(false))
                {
                    return false;
                }

                foreach (var record in NormalizeLegacyMetadata(legacy).BackupRecords)
                {
                    if (!await WriteRecordAsync(metadataDir, record).ConfigureAwait(false))
                    {
                        return false;
                    }
                }

                TryArchiveLegacyMetadata(metadataDir);
                return true;
            }
            catch (Exception ex)
            {
                LogService.LogWarning(I18n.Format("BackupMetadataStore_Log_LegacyMigrationFailedFallback", ex.Message), ServiceName);
                return false;
            }
        }

        private static async Task<BackupMetadata?> TryLoadLegacyMetadataAsync(string legacyPath)
        {
            try
            {
                string json = await File.ReadAllTextAsync(legacyPath).ConfigureAwait(false);
                return NormalizeLegacyMetadata(JsonSerializer.Deserialize(json, AppJsonContext.Default.BackupMetadata));
            }
            catch
            {
                return null;
            }
        }

        private static async Task<BackupMetadataState?> TryLoadStateAsync(string statePath)
        {
            try
            {
                string json = await File.ReadAllTextAsync(statePath).ConfigureAwait(false);
                return NormalizeState(JsonSerializer.Deserialize(json, AppJsonContext.Default.BackupMetadataState));
            }
            catch
            {
                return null;
            }
        }

        private static async Task<Dictionary<string, BackupChangeRecord>> LoadRecordMapAsync(string metadataDir, HashSet<string>? requestedArchiveFileNames)
        {
            string recordsDir = GetRecordsDirectoryPath(metadataDir);
            var result = new Dictionary<string, BackupChangeRecord>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(recordsDir))
            {
                return result;
            }

            IEnumerable<string> recordPaths;
            if (requestedArchiveFileNames == null)
            {
                recordPaths = Directory.EnumerateFiles(recordsDir, "*.json", SearchOption.TopDirectoryOnly);
            }
            else
            {
                recordPaths = requestedArchiveFileNames
                    .Select(name => TryGetRecordPath(metadataDir, name, out var recordPath) ? recordPath : null)
                    .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))!;
            }

            foreach (var recordPath in recordPaths)
            {
                var record = await TryLoadRecordAsync(recordPath!).ConfigureAwait(false);
                if (record == null || string.IsNullOrWhiteSpace(record.ArchiveFileName))
                {
                    continue;
                }

                result[record.ArchiveFileName] = record;
            }

            return result;
        }

        private static async Task<BackupChangeRecord?> TryLoadRecordAsync(string recordPath)
        {
            try
            {
                string json = await File.ReadAllTextAsync(recordPath).ConfigureAwait(false);
                return NormalizeRecord(JsonSerializer.Deserialize(json, AppJsonContext.Default.BackupChangeRecord));
            }
            catch
            {
                return null;
            }
        }

        private static async Task<bool> PersistRecordSnapshotAsync(string metadataDir, IReadOnlyCollection<BackupChangeRecord> records)
        {
            try
            {
                string recordsDir = GetRecordsDirectoryPath(metadataDir);
                Directory.CreateDirectory(recordsDir);

                var expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var record in records)
                {
                    if (!await WriteRecordAsync(metadataDir, record).ConfigureAwait(false))
                    {
                        return false;
                    }

                    if (TryGetRecordPath(metadataDir, record.ArchiveFileName, out var recordPath))
                    {
                        expectedPaths.Add(recordPath);
                    }
                }

                foreach (var existingPath in Directory.EnumerateFiles(recordsDir, "*.json", SearchOption.TopDirectoryOnly))
                {
                    if (!expectedPaths.Contains(existingPath))
                    {
                        TryDeleteFile(existingPath);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("BackupMetadataStore_Log_WriteFailed", ex.Message), ServiceName, ex);
                return false;
            }
        }

        private static async Task<bool> WriteStateAsync(string statePath, BackupMetadataState state)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
                string json = JsonSerializer.Serialize(NormalizeState(state), AppJsonContext.Default.BackupMetadataState);
                await WriteAtomicAsync(statePath, json).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("BackupMetadataStore_Log_WriteFailed", ex.Message), ServiceName, ex);
                return false;
            }
        }

        private static async Task<bool> WriteRecordAsync(string metadataDir, BackupChangeRecord record)
        {
            var normalizedRecord = NormalizeRecord(record);
            if (!TryGetRecordPath(metadataDir, normalizedRecord.ArchiveFileName, out var recordPath))
            {
                LogService.LogWarning(I18n.Format("BackupMetadataStore_Log_WriteFailed", normalizedRecord.ArchiveFileName), ServiceName);
                return false;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(recordPath)!);
                string json = JsonSerializer.Serialize(normalizedRecord, AppJsonContext.Default.BackupChangeRecord);
                await WriteAtomicAsync(recordPath, json).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("BackupMetadataStore_Log_WriteFailed", ex.Message), ServiceName, ex);
                return false;
            }
        }

        private static async Task WriteAtomicAsync(string filePath, string content)
        {
            string tempPath = filePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, content).ConfigureAwait(false);
            File.Move(tempPath, filePath, true);
        }

        private static void TryArchiveLegacyMetadata(string metadataDir)
        {
            try
            {
                string legacyPath = GetLegacyMetadataPath(metadataDir);
                if (!File.Exists(legacyPath))
                {
                    return;
                }

                File.Move(legacyPath, GetLegacyBackupMetadataPath(metadataDir), true);
            }
            catch (Exception ex)
            {
                LogService.LogWarning(I18n.Format("BackupMetadataStore_Log_LegacyMigrationFailedFallback", ex.Message), ServiceName);
            }
        }

        private static BackupMetadata NormalizeLegacyMetadata(BackupMetadata? metadata)
        {
            metadata ??= new BackupMetadata();
            metadata.FileStates = metadata.FileStates != null
                ? new Dictionary<string, FileState>(metadata.FileStates, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
            metadata.BackupRecords ??= new List<BackupChangeRecord>();
            metadata.BackupRecords = metadata.BackupRecords
                .Select(NormalizeRecord)
                .OrderBy(r => r.CreatedAtUtc)
                .ThenBy(r => r.ArchiveFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return metadata;
        }

        private static BackupMetadataState NormalizeState(BackupMetadataState? state)
        {
            state ??= new BackupMetadataState();
            state.Version = string.IsNullOrWhiteSpace(state.Version) ? "3.0" : state.Version;
            state.LastBackupFileName ??= string.Empty;
            state.BasedOnFullBackup ??= string.Empty;
            state.FileStates = state.FileStates != null
                ? new Dictionary<string, FileState>(state.FileStates, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
            return state;
        }

        private static BackupChangeRecord NormalizeRecord(BackupChangeRecord? record)
        {
            record ??= new BackupChangeRecord();
            record.ArchiveFileName ??= string.Empty;
            record.BackupType ??= string.Empty;
            record.BasedOnFullBackup ??= string.Empty;
            record.PreviousBackupFileName ??= string.Empty;
            record.AddedFiles ??= new List<string>();
            record.ModifiedFiles ??= new List<string>();
            record.DeletedFiles ??= new List<string>();
            record.FullFileList ??= new List<string>();
            return record;
        }

        private static BackupMetadataState ConvertToState(BackupMetadata metadata)
        {
            var normalized = NormalizeLegacyMetadata(metadata);
            return new BackupMetadataState
            {
                Version = "3.0",
                LastBackupTime = normalized.LastBackupTime,
                LastBackupFileName = normalized.LastBackupFileName ?? string.Empty,
                BasedOnFullBackup = normalized.BasedOnFullBackup ?? string.Empty,
                FileStates = new Dictionary<string, FileState>(normalized.FileStates, StringComparer.OrdinalIgnoreCase)
            };
        }

        private static BackupChangeRecord CloneRecord(BackupChangeRecord record)
        {
            var normalized = NormalizeRecord(record);
            return new BackupChangeRecord
            {
                ArchiveFileName = normalized.ArchiveFileName,
                BackupType = normalized.BackupType,
                BasedOnFullBackup = normalized.BasedOnFullBackup,
                PreviousBackupFileName = normalized.PreviousBackupFileName,
                CreatedAtUtc = normalized.CreatedAtUtc,
                AddedFiles = normalized.AddedFiles.ToList(),
                ModifiedFiles = normalized.ModifiedFiles.ToList(),
                DeletedFiles = normalized.DeletedFiles.ToList(),
                FullFileList = normalized.FullFileList.ToList()
            };
        }

        private static SemaphoreSlim GetGate(string metadataDir)
        {
            string key = Path.GetFullPath(metadataDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return MetadataLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        }

        private static HashSet<string>? NormalizeRequestedArchiveNames(IEnumerable<string>? archiveFileNames)
        {
            if (archiveFileNames == null)
            {
                return null;
            }

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in archiveFileNames.Where(n => !string.IsNullOrWhiteSpace(n)))
            {
                result.Add(name.Trim());
            }

            return result;
        }

        private static bool TryGetRecordPath(string metadataDir, string archiveFileName, out string recordPath)
        {
            recordPath = string.Empty;
            if (!IsSafeSinglePathSegment(archiveFileName))
            {
                return false;
            }

            string recordsDir = GetRecordsDirectoryPath(metadataDir);
            return BackupStoragePathService.TryBuildPathWithinRoot(recordsDir, archiveFileName + ".json", out recordPath);
        }

        private static string GetStatePath(string metadataDir) => Path.Combine(metadataDir, StateFileName);
        private static string GetRecordsDirectoryPath(string metadataDir) => Path.Combine(metadataDir, RecordsDirectoryName);
        private static string GetLegacyMetadataPath(string metadataDir) => Path.Combine(metadataDir, LegacyMetadataFileName);
        private static string GetLegacyBackupMetadataPath(string metadataDir) => Path.Combine(metadataDir, LegacyBackupMetadataFileName);

        private static bool IsSafeSinglePathSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (Path.IsPathRooted(value))
            {
                return false;
            }

            if (value.Equals(".", StringComparison.Ordinal) || value.Equals("..", StringComparison.Ordinal))
            {
                return false;
            }

            if (value.IndexOf(Path.DirectorySeparatorChar) >= 0 || value.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
            {
                return false;
            }

            if (value.IndexOf('\0') >= 0)
            {
                return false;
            }

            try
            {
                return string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private static void TryDeleteFile(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch
            {
            }
        }

        private static void RebaseSuccessorBackupRecord(
            BackupChangeRecord? previousRecord,
            BackupChangeRecord deletedRecord,
            BackupChangeRecord successorRecord,
            string? renamedNewFileName,
            string? renamedBackupType)
        {
            successorRecord.ArchiveFileName = string.IsNullOrWhiteSpace(renamedNewFileName)
                ? successorRecord.ArchiveFileName
                : renamedNewFileName;
            successorRecord.BackupType = string.IsNullOrWhiteSpace(renamedBackupType)
                ? successorRecord.BackupType
                : renamedBackupType;

            var finalSet = new HashSet<string>(
                (successorRecord.FullFileList ?? new List<string>()).Where(f => !string.IsNullOrWhiteSpace(f)),
                StringComparer.OrdinalIgnoreCase);

            if (string.Equals(successorRecord.BackupType, "Full", StringComparison.OrdinalIgnoreCase) || previousRecord == null)
            {
                successorRecord.BasedOnFullBackup = successorRecord.ArchiveFileName;
                successorRecord.PreviousBackupFileName = string.Empty;
                successorRecord.AddedFiles = finalSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                successorRecord.ModifiedFiles = new List<string>();
                successorRecord.DeletedFiles = new List<string>();
                successorRecord.FullFileList = finalSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                return;
            }

            var previousSet = new HashSet<string>(
                (previousRecord.FullFileList ?? new List<string>()).Where(f => !string.IsNullOrWhiteSpace(f)),
                StringComparer.OrdinalIgnoreCase);
            var ownerMap = previousSet.ToDictionary(path => path, _ => string.Empty, StringComparer.OrdinalIgnoreCase);

            foreach (var deleted in (deletedRecord.DeletedFiles ?? new List<string>()).Where(f => !string.IsNullOrWhiteSpace(f)))
            {
                ownerMap.Remove(deleted);
            }
            foreach (var added in (deletedRecord.AddedFiles ?? new List<string>()).Where(f => !string.IsNullOrWhiteSpace(f)))
            {
                ownerMap[added] = deletedRecord.ArchiveFileName;
            }
            foreach (var modified in (deletedRecord.ModifiedFiles ?? new List<string>()).Where(f => !string.IsNullOrWhiteSpace(f)))
            {
                ownerMap[modified] = deletedRecord.ArchiveFileName;
            }

            foreach (var deleted in (successorRecord.DeletedFiles ?? new List<string>()).Where(f => !string.IsNullOrWhiteSpace(f)))
            {
                ownerMap.Remove(deleted);
            }
            foreach (var added in (successorRecord.AddedFiles ?? new List<string>()).Where(f => !string.IsNullOrWhiteSpace(f)))
            {
                ownerMap[added] = successorRecord.ArchiveFileName;
            }
            foreach (var modified in (successorRecord.ModifiedFiles ?? new List<string>()).Where(f => !string.IsNullOrWhiteSpace(f)))
            {
                ownerMap[modified] = successorRecord.ArchiveFileName;
            }

            successorRecord.PreviousBackupFileName = previousRecord.ArchiveFileName;
            successorRecord.BasedOnFullBackup = previousRecord.BasedOnFullBackup;
            successorRecord.AddedFiles = finalSet
                .Where(path => !previousSet.Contains(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            successorRecord.DeletedFiles = previousSet
                .Where(path => !finalSet.Contains(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            successorRecord.ModifiedFiles = finalSet
                .Where(path => previousSet.Contains(path)
                    && ownerMap.TryGetValue(path, out var owner)
                    && !string.IsNullOrWhiteSpace(owner))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            successorRecord.FullFileList = finalSet.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}

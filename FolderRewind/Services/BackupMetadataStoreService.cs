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

        internal static Task<BackupMetadataLoadResult> LoadStateAsync(string metadataDir)
        {
            return LoadAsync(metadataDir, Array.Empty<string>());
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
                bool legacyMetadataExists = File.Exists(GetLegacyMetadataPath(metadataDir));
                if (legacyMetadataExists
                    && !await TryMigrateLegacyAsync(metadataDir, archiveLegacyMetadata: false).ConfigureAwait(false))
                {
                    return false;
                }

                var normalizedState = NormalizeState(state);
                var normalizedRecord = NormalizeRecord(record);

                Directory.CreateDirectory(metadataDir);
                Directory.CreateDirectory(GetRecordsDirectoryPath(metadataDir));

                if (!await WriteRecordAsync(metadataDir, normalizedRecord).ConfigureAwait(false))
                {
                    return false;
                }

                if (!await WriteStateAsync(GetStatePath(metadataDir), normalizedState).ConfigureAwait(false))
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

        public static bool TryGetStateFilePath(string metadataDir, out string statePath)
        {
            statePath = string.Empty;
            if (string.IsNullOrWhiteSpace(metadataDir))
            {
                return false;
            }

            try
            {
                string normalizedMetadataDir = Path.GetFullPath(metadataDir);
                return BackupStoragePathService.TryBuildPathWithinRoot(normalizedMetadataDir, StateFileName, out statePath);
            }
            catch
            {
                return false;
            }
        }

        public static bool TryGetRecordFilePath(string metadataDir, string archiveFileName, out string recordPath)
        {
            if (string.IsNullOrWhiteSpace(metadataDir))
            {
                recordPath = string.Empty;
                return false;
            }

            return TryGetRecordPath(metadataDir, archiveFileName, out recordPath);
        }

        public static async Task<bool> SynchronizeAfterArchiveDeletionAsync(
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
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var loadResult = await LoadCoreAsync(metadataDir, archiveFileNames: null, logMissingRequestedRecords: false)
                    .ConfigureAwait(false);
                var state = loadResult.State;
                if (state == null)
                {
                    return !loadResult.MetadataExists || !loadResult.StateLoadFailed;
                }

                var deletionResult = BackupMetadataDeletionPolicy.Apply(
                    state,
                    loadResult.Records.Values,
                    deletedFileName,
                    renamedOldFileName,
                    renamedNewFileName,
                    renamedBackupType);

                if (!await PersistRecordSnapshotAsync(metadataDir, deletionResult.Records).ConfigureAwait(false))
                {
                    return false;
                }

                if (deletionResult.InvalidateState)
                {
                    TryDeleteFile(GetStatePath(metadataDir));
                }
                else if (!await WriteStateAsync(GetStatePath(metadataDir), deletionResult.State).ConfigureAwait(false))
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

            if (legacyExists)
            {
                bool migrated = await TryMigrateLegacyAsync(metadataDir).ConfigureAwait(false);
                if (migrated)
                {
                    stateExists = File.Exists(statePath);
                    recordsDirExists = Directory.Exists(recordsDir);
                    metadataExists = true;
                }
                else
                {
                    var legacyMetadata = await TryLoadLegacyMetadataAsync(legacyPath).ConfigureAwait(false);
                    if (legacyMetadata != null)
                    {
                        return CreateLegacyLoadResult(legacyMetadata, archiveFileNames, metadataExists);
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
                    StateLoadFailed = true
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
                HasMissingRequestedRecords = hasMissingRequestedRecords
            };
        }

        private static BackupMetadataLoadResult CreateLegacyLoadResult(
            BackupMetadata metadata,
            IEnumerable<string>? archiveFileNames,
            bool metadataExists)
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
                HasMissingRequestedRecords = requestedArchiveFileNames != null
                    && requestedArchiveFileNames.Any(name => !records.ContainsKey(name))
            };
        }

        private static async Task<bool> TryMigrateLegacyAsync(string metadataDir, bool archiveLegacyMetadata = true)
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

                foreach (var record in NormalizeLegacyMetadata(legacy).BackupRecords)
                {
                    if (!await WriteRecordAsync(metadataDir, record).ConfigureAwait(false))
                    {
                        return false;
                    }
                }

                if (!await WriteStateAsync(GetStatePath(metadataDir), ConvertToState(legacy)).ConfigureAwait(false))
                {
                    return false;
                }

                if (archiveLegacyMetadata)
                {
                    TryArchiveLegacyMetadata(metadataDir);
                }

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
                await using var stream = OpenReadStream(legacyPath);
                var metadata = await JsonSerializer.DeserializeAsync(
                    stream,
                    AppJsonContext.Default.BackupMetadata).ConfigureAwait(false);
                return NormalizeLegacyMetadata(metadata);
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
                await using var stream = OpenReadStream(statePath);
                var state = await JsonSerializer.DeserializeAsync(
                    stream,
                    AppJsonContext.Default.BackupMetadataState).ConfigureAwait(false);
                return NormalizeState(state);
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
                await using var stream = OpenReadStream(recordPath);
                var record = await JsonSerializer.DeserializeAsync(
                    stream,
                    AppJsonContext.Default.BackupChangeRecord).ConfigureAwait(false);
                return NormalizeRecord(record);
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
                var normalizedState = NormalizeState(state);
                await AtomicFileService.WriteAsync(
                    statePath,
                    (stream, cancellationToken) => JsonSerializer.SerializeAsync(
                        stream,
                        normalizedState,
                        AppJsonContext.Default.BackupMetadataState,
                        cancellationToken)).ConfigureAwait(false);
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
                await AtomicFileService.WriteAsync(
                    recordPath,
                    (stream, cancellationToken) => JsonSerializer.SerializeAsync(
                        stream,
                        normalizedRecord,
                        AppJsonContext.Default.BackupChangeRecord,
                        cancellationToken)).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("BackupMetadataStore_Log_WriteFailed", ex.Message), ServiceName, ex);
                return false;
            }
        }

        private static FileStream OpenReadStream(string filePath)
        {
            return new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
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
            if (state.FileStates == null)
            {
                state.FileStates = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
            }
            else if (!ReferenceEquals(state.FileStates.Comparer, StringComparer.OrdinalIgnoreCase))
            {
                state.FileStates = new Dictionary<string, FileState>(state.FileStates, StringComparer.OrdinalIgnoreCase);
            }
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
            if (!BackupStoragePathService.IsSafeSinglePathSegment(archiveFileName))
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

    }
}

using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services
{
    internal sealed class BackupMetadataDeletionResult
    {
        public BackupMetadataState State { get; init; } = new();
        public IReadOnlyList<BackupChangeRecord> Records { get; init; } = Array.Empty<BackupChangeRecord>();
        public bool InvalidateState { get; init; }
    }

    internal static class BackupMetadataDeletionPolicy
    {
        public static BackupMetadataDeletionResult Apply(
            BackupMetadataState sourceState,
            IEnumerable<BackupChangeRecord> sourceRecords,
            string deletedFileName,
            string? renamedOldFileName,
            string? renamedNewFileName,
            string? renamedBackupType)
        {
            var state = CloneState(sourceState);
            var orderedRecords = sourceRecords
                .Select(CloneRecord)
                .OrderBy(record => record.CreatedAtUtc)
                .ThenBy(record => record.ArchiveFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            bool invalidateState = false;
            int deletedIndex = orderedRecords.FindIndex(record =>
                string.Equals(record.ArchiveFileName, deletedFileName, StringComparison.OrdinalIgnoreCase));
            BackupChangeRecord? deletedRecord = deletedIndex >= 0 ? orderedRecords[deletedIndex] : null;
            BackupChangeRecord? previousRecord = deletedIndex > 0 ? orderedRecords[deletedIndex - 1] : null;

            BackupChangeRecord? successorRecord = null;
            if (!string.IsNullOrWhiteSpace(renamedOldFileName))
            {
                successorRecord = orderedRecords.FirstOrDefault(record =>
                    string.Equals(record.ArchiveFileName, renamedOldFileName, StringComparison.OrdinalIgnoreCase));
            }
            else if (deletedIndex >= 0 && deletedIndex + 1 < orderedRecords.Count)
            {
                successorRecord = orderedRecords[deletedIndex + 1];
            }

            if (deletedRecord != null && successorRecord != null)
            {
                int successorIndex = orderedRecords.IndexOf(successorRecord);
                string successorBackupType = string.IsNullOrWhiteSpace(renamedBackupType)
                    ? GetEffectiveBackupType(successorRecord)
                    : renamedBackupType;

                if (!TryReconstructFileSets(
                        orderedRecords,
                        deletedIndex,
                        successorIndex,
                        out var previousSet,
                        out var finalSet)
                    || (previousRecord == null
                        && !string.Equals(successorBackupType, "Full", StringComparison.OrdinalIgnoreCase)))
                {
                    invalidateState = true;
                }
                else
                {
                    RebaseSuccessor(
                        previousRecord,
                        deletedRecord,
                        successorRecord,
                        previousSet,
                        finalSet,
                        renamedNewFileName,
                        renamedBackupType);
                }
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

            orderedRecords.RemoveAll(record =>
                string.Equals(record.ArchiveFileName, deletedFileName, StringComparison.OrdinalIgnoreCase));

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

            return new BackupMetadataDeletionResult
            {
                State = state,
                Records = orderedRecords,
                InvalidateState = invalidateState
            };
        }

        private static void RebaseSuccessor(
            BackupChangeRecord? previousRecord,
            BackupChangeRecord deletedRecord,
            BackupChangeRecord successorRecord,
            HashSet<string> previousSet,
            HashSet<string> finalSet,
            string? renamedNewFileName,
            string? renamedBackupType)
        {
            successorRecord.ArchiveFileName = string.IsNullOrWhiteSpace(renamedNewFileName)
                ? successorRecord.ArchiveFileName
                : renamedNewFileName;
            successorRecord.BackupType = string.IsNullOrWhiteSpace(renamedBackupType)
                ? successorRecord.BackupType
                : renamedBackupType;

            if (IsFullRecord(successorRecord) || previousRecord == null)
            {
                var sortedFinalSet = finalSet.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
                successorRecord.BasedOnFullBackup = successorRecord.ArchiveFileName;
                successorRecord.PreviousBackupFileName = string.Empty;
                successorRecord.AddedFiles = sortedFinalSet.ToList();
                successorRecord.ModifiedFiles = new List<string>();
                successorRecord.DeletedFiles = new List<string>();
                successorRecord.FullFileList = sortedFinalSet;
                return;
            }

            var ownerMap = previousSet.ToDictionary(path => path, _ => string.Empty, StringComparer.OrdinalIgnoreCase);

            ApplyOwnershipChanges(ownerMap, deletedRecord);
            ApplyOwnershipChanges(ownerMap, successorRecord);

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
            successorRecord.FullFileList = new List<string>();
        }

        private static bool TryReconstructFileSets(
            IReadOnlyList<BackupChangeRecord> orderedRecords,
            int deletedIndex,
            int successorIndex,
            out HashSet<string> previousSet,
            out HashSet<string> finalSet)
        {
            previousSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            finalSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (deletedIndex < 0 || successorIndex <= deletedIndex || successorIndex >= orderedRecords.Count)
            {
                return false;
            }

            int fullIndex = -1;
            for (int i = deletedIndex; i >= 0; i--)
            {
                if (IsFullRecord(orderedRecords[i]) && orderedRecords[i].FullFileList.Count > 0)
                {
                    fullIndex = i;
                    break;
                }
            }

            if (fullIndex < 0)
            {
                return false;
            }

            var workingSet = new HashSet<string>(
                orderedRecords[fullIndex].FullFileList.Where(path => !string.IsNullOrWhiteSpace(path)),
                StringComparer.OrdinalIgnoreCase);
            if (fullIndex == deletedIndex - 1)
            {
                previousSet = new HashSet<string>(workingSet, StringComparer.OrdinalIgnoreCase);
            }

            for (int i = fullIndex + 1; i <= successorIndex; i++)
            {
                ApplyFileChanges(workingSet, orderedRecords[i]);
                if (i == deletedIndex - 1)
                {
                    previousSet = new HashSet<string>(workingSet, StringComparer.OrdinalIgnoreCase);
                }
            }

            var successorSnapshot = orderedRecords[successorIndex].FullFileList;
            if (successorSnapshot.Count > 0)
            {
                workingSet.Clear();
                workingSet.UnionWith(successorSnapshot.Where(path => !string.IsNullOrWhiteSpace(path)));
            }

            finalSet = workingSet;
            return true;
        }

        private static void ApplyFileChanges(ISet<string> fileSet, BackupChangeRecord record)
        {
            if (IsFullRecord(record) && record.FullFileList.Count > 0)
            {
                fileSet.Clear();
                fileSet.UnionWith(record.FullFileList.Where(path => !string.IsNullOrWhiteSpace(path)));
                return;
            }

            foreach (var deleted in record.DeletedFiles.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                fileSet.Remove(deleted);
            }

            foreach (var added in record.AddedFiles.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                fileSet.Add(added);
            }

            foreach (var modified in record.ModifiedFiles.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                fileSet.Add(modified);
            }
        }

        private static bool IsFullRecord(BackupChangeRecord record)
        {
            return string.Equals(GetEffectiveBackupType(record), "Full", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetEffectiveBackupType(BackupChangeRecord record)
        {
            return string.IsNullOrWhiteSpace(record.BackupType)
                ? BackupArchiveTypePolicy.InferFromFileName(record.ArchiveFileName)
                : record.BackupType;
        }

        private static void ApplyOwnershipChanges(
            IDictionary<string, string> ownerMap,
            BackupChangeRecord record)
        {
            foreach (var deleted in record.DeletedFiles.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                ownerMap.Remove(deleted);
            }

            foreach (var added in record.AddedFiles.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                ownerMap[added] = record.ArchiveFileName;
            }

            foreach (var modified in record.ModifiedFiles.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                ownerMap[modified] = record.ArchiveFileName;
            }
        }

        private static BackupMetadataState CloneState(BackupMetadataState source)
        {
            return new BackupMetadataState
            {
                Version = source.Version,
                LastBackupTime = source.LastBackupTime,
                LastBackupFileName = source.LastBackupFileName,
                BasedOnFullBackup = source.BasedOnFullBackup,
                FileStates = source.FileStates.ToDictionary(
                    pair => pair.Key,
                    pair => new FileState
                    {
                        Size = pair.Value.Size,
                        LastWriteTimeUtc = pair.Value.LastWriteTimeUtc,
                        Hash = pair.Value.Hash
                    },
                    StringComparer.OrdinalIgnoreCase)
            };
        }

        private static BackupChangeRecord CloneRecord(BackupChangeRecord source)
        {
            return new BackupChangeRecord
            {
                ArchiveFileName = source.ArchiveFileName ?? string.Empty,
                BackupType = source.BackupType ?? string.Empty,
                BasedOnFullBackup = source.BasedOnFullBackup ?? string.Empty,
                PreviousBackupFileName = source.PreviousBackupFileName ?? string.Empty,
                CreatedAtUtc = source.CreatedAtUtc,
                AddedFiles = source.AddedFiles?.ToList() ?? new List<string>(),
                ModifiedFiles = source.ModifiedFiles?.ToList() ?? new List<string>(),
                DeletedFiles = source.DeletedFiles?.ToList() ?? new List<string>(),
                FullFileList = source.FullFileList?.ToList() ?? new List<string>()
            };
        }
    }
}

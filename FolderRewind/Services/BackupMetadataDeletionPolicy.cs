using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services
{
    /// <summary>
    /// 删除策略的计算结果：重写后的状态与记录集。InvalidateState=true 表示状态无法修补，
    /// 调用方应删除 state.json（下次增量将因基线缺失而强制全量重建）。
    /// </summary>
    internal sealed class BackupMetadataDeletionResult
    {
        public BackupMetadataState State { get; init; } = new();
        public IReadOnlyList<BackupChangeRecord> Records { get; init; } = Array.Empty<BackupChangeRecord>();
        public bool InvalidateState { get; init; }
    }

    /// <summary>
    /// 纯逻辑策略：归档删除（可选伴随后继增量改名转全量的"安全删除"）之后，
    /// 计算元数据记录集与状态的重写方案。无 IO、无 UI 依赖，可直接单元测试。
    /// </summary>
    internal static class BackupMetadataDeletionPolicy
    {
        /// <summary>
        /// 应用一次归档删除，返回重写后的记录集与状态。函数式语义：克隆输入后计算，不改调用方对象。
        /// </summary>
        /// <remarks>
        /// 流程：定位被删记录与其前驱/后继（安全删除时后继由改名参数显式指定）；
        /// 若存在后继则重建删除前后的文件集并把后继重定基到前驱（或整体转全量）；
        /// 随后把记录与状态中指向旧文件名的引用（记录的 ArchiveFileName/PreviousBackupFileName/
        /// BasedOnFullBackup 与状态的两字段）改写为新名；最后移除被删记录。
        /// 状态仍指向被删文件、或链首 Full 被删而无人接替时置 InvalidateState。
        /// </remarks>
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

        /// <summary>
        /// 把后继记录重定基，吸收被删记录的变更。两条分支：
        /// 后继已是（或将被改为）Full、或没有前驱时，直接把删除后的文件集写成它的全量快照；
        /// 否则基于前驱集合重算 Added/Deleted/Modified——其中 Modified 由所有权映射推断，
        /// 只保留"被删记录或后继记录声明过修改"的文件，其余前驱已有文件视为未变更。
        /// </summary>
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

        /// <summary>
        /// 回放重建两个文件集合：previousSet=被删记录生效前的文件集，finalSet=后继记录生效后的文件集。
        /// 从被删记录向前找最近的带完整清单的 Full 作为起点，依次应用各记录的增删改；
        /// 后继自身带完整清单时以其为准（比回放更可信）。找不到可用 Full 基准即失败。
        /// </summary>
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

        /// <summary>
        /// 把一条记录的变更应用到工作集：Full 记录直接替换为完整清单，
        /// 否则依次移除删除、并入新增与修改。
        /// </summary>
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

        /// <summary>
        /// 判断是否为 Full 记录；BackupType 为空时按归档文件名前缀推断。
        /// </summary>
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

        /// <summary>
        /// 维护"文件→最后声明修改它的归档"所有权映射，供重定基时判定哪些文件应算作 Modified。
        /// </summary>
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

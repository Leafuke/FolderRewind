using FolderRewind.History.Capture;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace FolderRewind.Services
{
    public static partial class BackupService
    {
        private static BackupChangeSet CompareFileStates(
            IReadOnlyDictionary<string, SourceCaptureFileState> currentStates,
            IReadOnlyDictionary<string, SourceCaptureFileState>? previousStates)
        {
            using var timing = HistoryOperationDiagnostics.Current?.Measure("source-diff");
            var result = new BackupChangeSet();
            previousStates ??= new Dictionary<string, SourceCaptureFileState>(StringComparer.OrdinalIgnoreCase);
            // Preserve the parent's comparer (notably Ordinal for persisted baselines).
            // A case-only rename must keep the existing capture semantics.
            if (previousStates is ImmutableSortedDictionary<string, SourceCaptureFileState> sorted
                && sorted.KeyComparer is IEqualityComparer<string> comparer)
                previousStates = sorted.ToDictionary(pair => pair.Key, pair => pair.Value, comparer);

            foreach (var pair in currentStates)
            {
                if (!previousStates.TryGetValue(pair.Key, out var previous))
                {
                    result.AddedFiles.Add(pair.Key);
                    continue;
                }

                if (pair.Value.Size != previous.Size
                    || pair.Value.LastWriteTimeUtc != previous.LastWriteTimeUtc)
                {
                    result.ModifiedFiles.Add(pair.Key);
                }
            }

            foreach (var pair in previousStates)
            {
                if (!currentStates.ContainsKey(pair.Key))
                {
                    result.DeletedFiles.Add(pair.Key);
                }
            }

            result.AddedFiles.Sort(StringComparer.OrdinalIgnoreCase);
            result.ModifiedFiles.Sort(StringComparer.OrdinalIgnoreCase);
            result.DeletedFiles.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }
    }
}

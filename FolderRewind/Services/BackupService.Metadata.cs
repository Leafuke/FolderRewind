using FolderRewind.History.Capture;
using System;
using System.Collections.Generic;

namespace FolderRewind.Services
{
    public static partial class BackupService
    {
        private static BackupChangeSet CompareFileStates(
            IReadOnlyDictionary<string, SourceCaptureFileState> currentStates,
            IReadOnlyDictionary<string, SourceCaptureFileState>? previousStates)
        {
            var result = new BackupChangeSet();
            previousStates ??= new Dictionary<string, SourceCaptureFileState>(StringComparer.OrdinalIgnoreCase);

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

// Frozen 4c857a1 algorithm, retained only as an independent differential-test oracle.
using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.History.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FolderRewind.Services;




/// <summary>仅证明充分的 Native 线性链。新增/修改先应用，删除后应用，保持顺序物化语义。</summary>
internal static class NativeSmartOwnerPlannerReference
{
    internal static NativeSmartOwnerPlan? TryPlan(IReadOnlyList<ArchiveMaterializationInput> inputs,
        IReadOnlyList<ArchiveSafeListing> listings, string marker, out string fallback)
    {
        fallback = "";
        if (inputs.Any(input => input.Representation.RepresentationSpecificMetadata.ContainsKey("legacyFileName")
                || input.Representation.Kind is not (RepresentationKind.CoreFull or RepresentationKind.CoreSmartDelta)))
        { fallback = "non-native-chain"; return null; }
        var deletions = inputs.Select(input => input.Representation.RepresentationSpecificMetadata
            .GetValueOrDefault("deletedFiles", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        foreach (var paths in deletions)
            foreach (var path in paths)
                if (!HistoryRepositoryPaths.IsSafeRepositoryRelativePath(path) || path.Contains('\\')
                    || path.Split('/').Any(s => s.EndsWith(' ') || s.EndsWith('.')))
                    throw new InvalidDataException("Native Smart tombstone path is unsafe or ambiguous.");
        if (inputs.Count < 2) { fallback = "single-archive"; return null; }
        if (listings.Count != inputs.Count) throw new InvalidDataException("Smart listing closure is incomplete.");
        for (int i = 0; i < inputs.Count; i++)
        {
            var rep = inputs[i].Representation;
            if (rep.RepresentationSpecificMetadata.ContainsKey("legacyFileName")
                || rep.Fidelity != MaterializationFidelity.Exact
                || (i == 0 ? rep.Kind != RepresentationKind.CoreFull || rep.DependencyRepresentationIds.Length != 0
                    : rep.Kind != RepresentationKind.CoreSmartDelta || rep.DependencyRepresentationIds.Length != 1
                        || rep.DependencyRepresentationIds[0] != inputs[i - 1].Representation.RepresentationId))
            { fallback = "non-native-or-nonlinear-chain"; return null; }
            if (listings[i].Directories.Any(dir => !IsMarker(dir, marker)
                    && !listings[i].Files.Keys.Any(file => Under(file, dir))))
            { fallback = "explicit-empty-directory"; return null; }
            // 当前 7z 的 @listfile 会裁掉行首空白；合法文件名保留原样并走顺序提取。
            if (listings[i].Files.Keys.Any(path => char.IsWhiteSpace(path[0])))
            { fallback = "listfile-leading-whitespace"; return null; }
        }
        if (deletions[0].Length != 0) { fallback = "full-has-tombstones"; return null; }
        var owners = new Dictionary<string, NativeSmartOwnedFile>(StringComparer.OrdinalIgnoreCase);
        var finalDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < inputs.Count; i++)
        {
            var listing = listings[i];
            foreach (var directory in listing.Directories.Where(path => !IsMarker(path, marker))) finalDirectories.Add(directory);
            if (deletions[i].Any(deleted => listing.Files.Keys.Any(file => !IsMarker(file, marker)
                    && (file.Equals(deleted, StringComparison.OrdinalIgnoreCase) || Under(file, deleted)))))
            { fallback = "same-delta-add-delete-conflict"; return null; }
            foreach (var file in listing.Files.Values.Where(file => !IsMarker(file.RelativePath, marker)))
            {
                var parent = file.RelativePath;
                while (parent.LastIndexOf('/') is var separator && separator >= 0)
                { parent = parent[..separator]; finalDirectories.Add(parent); }
                if (owners.Keys.Any(existing => Under(file.RelativePath, existing) || Under(existing, file.RelativePath)))
                { fallback = "file-directory-transition"; return null; }
                owners.Remove(file.RelativePath);
                owners.Add(file.RelativePath, new(i, file));
            }
            foreach (var deleted in deletions[i])
            {
                foreach (var path in owners.Keys.Where(path => path.Equals(deleted, StringComparison.OrdinalIgnoreCase)
                    || Under(path, deleted)).ToArray()) owners.Remove(path);
                finalDirectories.RemoveWhere(path => path.Equals(deleted, StringComparison.OrdinalIgnoreCase) || Under(path, deleted));
            }
        }
        if (finalDirectories.Any(directory => !owners.Keys.Any(path => Under(path, directory))))
        { fallback = "final-empty-directory"; return null; }
        return new(owners);
    }
    private static bool IsMarker(string path, string marker) => path.Equals(marker, StringComparison.OrdinalIgnoreCase) || Under(path, marker);
    private static bool Under(string path, string parent) => path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);
}

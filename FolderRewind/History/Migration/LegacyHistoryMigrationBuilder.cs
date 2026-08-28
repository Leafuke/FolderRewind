using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using FolderRewind.Plugin.Runtime.Configuration;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

namespace FolderRewind.History.Migration;

public sealed class LegacyHistoryMigrationBuilder
{
    private readonly HistoryPackCodec _codec;
    private readonly Func<string, bool> _fileExists;

    public LegacyHistoryMigrationBuilder(HistoryPackCodec? codec = null, Func<string, bool>? fileExists = null)
    {
        _codec = codec ?? new HistoryPackCodec();
        _fileExists = fileExists ?? File.Exists;
    }

    public LegacyHistoryMigrationBuild Build(LegacyHistoryMigrationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Sources.GroupBy(item => item.SourceId).Any(group => group.Count() > 1))
            throw new InvalidDataException("Legacy migration Source roster contains duplicate SourceId values.");
        var sourceMap = input.Sources.ToDictionary(item => item.SourceId);
        if (input.Entries.Any(item => !sourceMap.ContainsKey(item.SourceId)))
            throw new InvalidDataException("Legacy history references a Source absent from the migrated Config roster.");

        var entries = input.Entries
            .Select(item => new EntryState(item, OriginKey(input.ConfigId, item)))
            .OrderBy(item => item.OriginKey, StringComparer.Ordinal)
            .ToArray();
        var conflictingOrigin = entries.GroupBy(item => item.OriginKey, StringComparer.Ordinal)
            .FirstOrDefault(group => group.DistinctBy(item => item.Entry).Count() > 1);
        if (conflictingOrigin is not null)
            throw new InvalidDataException($"Legacy history identity conflict: {conflictingOrigin.Key}");
        entries = entries.DistinctBy(item => item.OriginKey, StringComparer.Ordinal).ToArray();

        var smartMap = input.SmartRecords
            .GroupBy(item => (item.SourceId, File: NormalizeFile(item.ArchiveFileName)))
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(item => NormalizeFile(item.PreviousBackupFileName), StringComparer.Ordinal)
                    .ThenBy(item => NormalizeFile(item.BasedOnFullBackup), StringComparer.Ordinal).First());
        var factsByOrigin = new SortedDictionary<string, List<object>>(StringComparer.Ordinal);
        var localEntries = new List<LocalReplicaCatalogEntry>();
        var representations = new Dictionary<string, VersionRepresentation>(StringComparer.Ordinal);
        var versions = new Dictionary<string, SourceVersion>(StringComparer.Ordinal);

        foreach (var state in entries)
        {
            var entry = state.Entry;
            var diagnostics = new List<HistoryDiagnostic>();
            if (!SafeFileName(entry.FileName))
                diagnostics.Add(Warning("legacy.file.invalid", "Legacy archive filename is unsafe; no Representation was imported."));
            var version = new SourceVersion(
                LegacyHistoryMigrationIdentityV1.Version(state.OriginKey),
                input.ConfigId,
                entry.SourceId,
                [],
                ToUtc(entry.Timestamp),
                null,
                entry.IsPartialBackup ? CaptureScope.PartialSource : CaptureScope.FullSource,
                CaptureOutcome.Recovered,
                [],
                new SourceDescriptorSnapshot(
                    string.IsNullOrWhiteSpace(entry.FolderName) ? sourceMap[entry.SourceId].DisplayName : entry.FolderName,
                    entry.OriginalFolderPath),
                null,
                new HistoryProvenance(HistoryOrigin.LegacyMigration, string.Empty, state.OriginKey));
            versions[state.OriginKey] = version;
            var facts = new List<object> { version };
            string archivePath = SafeFileName(entry.FileName)
                ? Path.Combine(sourceMap[entry.SourceId].ArchiveDirectory, entry.FileName)
                : string.Empty;
            bool localExists = archivePath.Length > 0 && _fileExists(archivePath);
            bool cloudDeclared = entry.IsCloudArchived
                && HistoryRepositoryPaths.IsSafeRepositoryRelativePath(entry.LegacyCloudRelativeLocator);
            if (entry.IsCloudArchived && !cloudDeclared)
                diagnostics.Add(Warning("legacy.cloud.locator", "Legacy Cloud declaration had no safe relative locator and was not imported."));

            if (localExists || cloudDeclared)
            {
                var dependencies = new List<RepresentationId>();
                bool dependencyResolutionFailed = false;
                if (IsSmart(entry.BackupType))
                {
                    if (!smartMap.TryGetValue((entry.SourceId, NormalizeFile(entry.FileName)), out var smart))
                    {
                        diagnostics.Add(Warning(
                            "legacy.smart.metadata-missing",
                            "Smart archive has no released dependency metadata; no Representation was fabricated."));
                        dependencyResolutionFailed = true;
                    }
                    foreach (var dependencyFile in new[] { smart?.PreviousBackupFileName, smart?.BasedOnFullBackup }
                                 .Where(value => !string.IsNullOrWhiteSpace(value))
                                 .Select(value => value!)
                                 .Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        var dependency = ResolveDependency(
                            input,
                            entry.SourceId,
                            dependencyFile,
                            entries,
                            factsByOrigin,
                            localEntries,
                            representations,
                            versions);
                        if (dependency is null)
                        {
                            dependencyResolutionFailed = true;
                        }
                        else
                        {
                            dependencies.Add(dependency.Value);
                        }
                    }
                }
                if (!dependencyResolutionFailed)
                {
                    var representation = new VersionRepresentation(
                        LegacyHistoryMigrationIdentityV1.Representation(state.OriginKey),
                        version.VersionId,
                        Kind(entry.BackupType),
                        Format(entry.FileName),
                        dependencies.Distinct(),
                        entry.IsPartialBackup ? RestoreStrategy.Overlay : RestoreStrategy.Exact,
                        null,
                        null,
                        new Dictionary<string, string>
                        {
                            ["legacyFileName"] = entry.FileName,
                            ["legacyBackupType"] = entry.BackupType
                        });
                    representations[state.OriginKey] = representation;
                    facts.Add(representation);
                    if (localExists)
                    {
                        localEntries.Add(new LocalReplicaCatalogEntry(
                            representation.RepresentationId,
                            LegacyHistoryMigrationIdentityV1.LocalReplica(state.OriginKey),
                            LocalReplicaLocator.ControlledAbsolute(archivePath),
                            ToUtc(entry.Timestamp)));
                    }
                    if (cloudDeclared)
                    {
                        var replica = new StorageReplica(
                            LegacyHistoryMigrationIdentityV1.CloudReplica(state.OriginKey),
                            representation.RepresentationId,
                            ReplicaProviderKind.LegacyCloud,
                            "legacy-declarations/" + entry.LegacyCloudRelativeLocator.Trim('/'),
                            null,
                            null,
                            new HistoryProvenance(
                                HistoryOrigin.LegacyDeclaration,
                                string.Empty,
                                entry.LegacyCloudRelativeLocator));
                        facts.Add(replica);
                        facts.Add(new ReplicaLifecycleUpdate(
                            LegacyHistoryMigrationIdentityV1.CloudLifecycle(state.OriginKey),
                            replica.ReplicaId,
                            [],
                            ReplicaLifecycleState.Active,
                            ToUtc(entry.Timestamp),
                            "Imported unverified Legacy declaration."));
                    }
                }
            }
            if (!string.IsNullOrWhiteSpace(entry.Comment))
                facts.Add(Annotation(state, version, HistoryAnnotationKind.Comment, entry.Comment));
            if (entry.IsImportant)
                facts.Add(Annotation(state, version, HistoryAnnotationKind.Pin, "true"));
            if (entry.IsPartialBackup)
                diagnostics.Add(Warning("legacy.partial.overlay", "Partial Legacy backup was imported with Overlay fidelity."));
            facts.Add(new LegacyMigrationRecord(
                LegacyHistoryMigrationIdentityV1.Record(state.OriginKey),
                state.OriginKey,
                version.VersionId,
                LegacyMigrationVisibility.Timeline,
                ToUtc(entry.Timestamp),
                diagnostics));
            factsByOrigin[state.OriginKey] = facts;
        }

        var checkpointSources = new List<CheckpointSource>();
        foreach (var source in input.Sources.OrderBy(item => item.SourceId.ToString(), StringComparer.Ordinal))
        {
            var latest = entries.Where(item => item.Entry.SourceId == source.SourceId)
                .OrderByDescending(item => ToUtc(item.Entry.Timestamp))
                .ThenByDescending(item => item.OriginKey, StringComparer.Ordinal)
                .FirstOrDefault();
            checkpointSources.Add(new CheckpointSource(
                source.SourceId,
                new SourceDescriptorSnapshot(source.DisplayName, source.OriginalPath),
                latest is null ? null : versions[latest.OriginKey].VersionId,
                latest is null ? CheckpointSourceDisposition.Unavailable : CheckpointSourceDisposition.CarriedForward));
        }
        var vector = string.Join("|", checkpointSources.Select(item => $"{item.SourceId}:{item.VersionId?.ToString() ?? "missing"}"));
        var checkpointId = LegacyHistoryMigrationIdentityV1.Checkpoint(input.ConfigId + "|" + vector);
        var created = entries.Length == 0 ? DateTimeOffset.UnixEpoch : entries.Max(item => ToUtc(item.Entry.Timestamp));
        var checkpoint = new ConfigurationCheckpoint(
            checkpointId,
            input.ConfigId,
            created,
            null,
            new HistoryProvenance(HistoryOrigin.LegacyMigration, string.Empty, "bootstrap"),
            checkpointSources);
        var branchId = LegacyHistoryMigrationIdentityV1.LegacyMain(input.ConfigId);
        var branch = new BranchUpdate(
            LegacyHistoryMigrationIdentityV1.BranchUpdate(branchId, checkpointId),
            branchId,
            [],
            "legacy-main",
            checkpointId,
            false,
            created,
            BranchUpdateReason.Migration);
        factsByOrigin["~bootstrap"] = [checkpoint, branch];

        var packs = factsByOrigin.Select(pair => Pack(pair.Value, pair.Key == "~bootstrap" ? created : RecordTime(pair.Value)))
            .ToImmutableArray();
        var workspace = new HistoryWorkspace(
            input.ConfigId,
            0,
            branchId,
            branch.UpdateId,
            checkpointSources.Select(item => new WorkspaceSourceBaseline(
                item.SourceId,
                item.VersionId,
                WorkspaceBaselineRelation.Unknown)));
        return new LegacyHistoryMigrationBuild(
            packs,
            new LocalReplicaCatalog(input.ConfigId, 0, localEntries.DistinctBy(item => item.LocalReplicaId)),
            workspace,
            checkpointId,
            branch.UpdateId);
    }

    private RepresentationId? ResolveDependency(
        LegacyHistoryMigrationInput input,
        SourceId sourceId,
        string dependencyFile,
        IReadOnlyList<EntryState> entries,
        SortedDictionary<string, List<object>> factsByOrigin,
        List<LocalReplicaCatalogEntry> localEntries,
        Dictionary<string, VersionRepresentation> representations,
        Dictionary<string, SourceVersion> versions)
    {
        var known = entries.Where(item => item.Entry.SourceId == sourceId
                && StringComparer.OrdinalIgnoreCase.Equals(item.Entry.FileName, dependencyFile))
            .OrderByDescending(item => ToUtc(item.Entry.Timestamp))
            .ThenByDescending(item => item.OriginKey, StringComparer.Ordinal)
            .FirstOrDefault();
        if (known is not null)
        {
            var knownSource = input.Sources.Single(item => item.SourceId == sourceId);
            var knownPath = SafeFileName(known.Entry.FileName)
                ? Path.Combine(knownSource.ArchiveDirectory, known.Entry.FileName)
                : string.Empty;
            bool declared = known.Entry.IsCloudArchived
                && HistoryRepositoryPaths.IsSafeRepositoryRelativePath(known.Entry.LegacyCloudRelativeLocator);
            return (knownPath.Length > 0 && _fileExists(knownPath)) || declared
                ? LegacyHistoryMigrationIdentityV1.Representation(known.OriginKey)
                : null;
        }

        var source = input.Sources.Single(item => item.SourceId == sourceId);
        var origin = string.Join('|',
            "legacy-support-v1",
            input.ConfigId,
            sourceId,
            NormalizeFile(dependencyFile));
        if (representations.TryGetValue(origin, out var existing)) return existing.RepresentationId;
        var version = new SourceVersion(
            LegacyHistoryMigrationIdentityV1.Version(origin), input.ConfigId, sourceId, [], DateTimeOffset.UnixEpoch,
            null, CaptureScope.FullSource, CaptureOutcome.Recovered, [],
            new SourceDescriptorSnapshot(source.DisplayName, source.OriginalPath), null,
            new HistoryProvenance(HistoryOrigin.LegacyMetadataRecovery, string.Empty, origin));
        var representation = new VersionRepresentation(
            LegacyHistoryMigrationIdentityV1.Representation(origin), version.VersionId,
            RepresentationKind.LegacyArchive, Format(dependencyFile), [], RestoreStrategy.Exact,
            null, null, new Dictionary<string, string> { ["legacyFileName"] = dependencyFile });
        versions[origin] = version;
        representations[origin] = representation;
        var diagnostics = new List<HistoryDiagnostic>
        {
            Warning(
                "legacy.support.recovered",
                "Support-only Version was recovered from an explicit released Smart dependency reference.")
        };
        var path = SafeFileName(dependencyFile) ? Path.Combine(source.ArchiveDirectory, dependencyFile) : string.Empty;
        if (path.Length > 0 && _fileExists(path))
        {
            localEntries.Add(new LocalReplicaCatalogEntry(
                representation.RepresentationId,
                LegacyHistoryMigrationIdentityV1.LocalReplica(origin),
                LocalReplicaLocator.ControlledAbsolute(path),
                DateTimeOffset.UnixEpoch));
        }
        factsByOrigin[origin] =
        [
            version,
            representation,
            new LegacyMigrationRecord(
                LegacyHistoryMigrationIdentityV1.Record(origin), origin, version.VersionId,
                LegacyMigrationVisibility.SupportOnly, DateTimeOffset.UnixEpoch, diagnostics)
        ];
        return representation.RepresentationId;
    }

    private HistoryCommitPack Pack(IEnumerable<object> facts, DateTimeOffset timestamp)
        => new(
            PackId.New(),
            HistoryTransactionId.New(),
            timestamp,
            facts.Select(fact => _codec.CreateObject(fact))
                .OrderBy(item => item.Kind, StringComparer.Ordinal)
                .ThenBy(item => item.Id, StringComparer.Ordinal));

    private static HistoryAnnotationUpdate Annotation(
        EntryState state,
        SourceVersion version,
        HistoryAnnotationKind kind,
        string value)
        => new(
            LegacyHistoryMigrationIdentityV1.Annotation(state.OriginKey, kind),
            new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Version, version.VersionId.Value),
            kind,
            [],
            value,
            ToUtc(state.Entry.Timestamp));

    private static DateTimeOffset RecordTime(IEnumerable<object> facts)
        => facts.OfType<LegacyMigrationRecord>().Select(item => item.LegacyTimestampUtc).DefaultIfEmpty(DateTimeOffset.UnixEpoch).Max();

    private static string OriginKey(HistoryConfigId configId, LegacyHistoryEntrySnapshot entry)
        => LegacySourceIdentityV1.CreateHistoryOriginKey(
            configId.Value, entry.OriginalFolderPath, entry.FileName, entry.Timestamp);

    private static DateTimeOffset ToUtc(DateTime value)
        => value.Kind == DateTimeKind.Unspecified
            ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))
            : new DateTimeOffset(value.ToUniversalTime());

    private static bool SafeFileName(string value)
        => !string.IsNullOrWhiteSpace(value)
            && StringComparer.Ordinal.Equals(Path.GetFileName(value), value);

    private static string NormalizeFile(string value) => (value ?? string.Empty).Trim().ToUpperInvariant();
    private static bool IsSmart(string value) => StringComparer.OrdinalIgnoreCase.Equals(value?.Trim(), "Smart");
    private static string Format(string fileName)
        => Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant() is { Length: > 0 } format ? format : "7z";
    private static RepresentationKind Kind(string value)
        => value?.Trim().ToUpperInvariant() switch
        {
            "FULL" => RepresentationKind.CoreFull,
            "SMART" => RepresentationKind.CoreSmartDelta,
            "ROLLING" => RepresentationKind.CoreRolling,
            _ => RepresentationKind.LegacyArchive
        };
    private static HistoryDiagnostic Warning(string code, string message)
        => new(code, HistoryDiagnosticSeverity.Warning, message);

    private sealed record EntryState(LegacyHistoryEntrySnapshot Entry, string OriginKey);
}

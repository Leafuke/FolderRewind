using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using FolderRewind.Plugin.Runtime.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FolderRewind.History.Migration;

public sealed class LegacyHistoryMigrationBuilder
{
    private readonly HistoryPackCodec _codec;
    private readonly Func<string, bool> _fileExists;
    public LegacyHistoryMigrationBuilder(HistoryPackCodec? codec = null, Func<string, bool>? fileExists = null)
    { _codec = codec ?? new(); _fileExists = fileExists ?? File.Exists; }

    public LegacyHistoryMigrationBuild Build(LegacyHistoryMigrationInput input)
    {
        var sources = input.Sources.ToDictionary(s => s.SourceId);
        var entries = input.Entries.GroupBy(e => OriginKey(input.ConfigId, e), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal).ToArray();
        var facts = new Dictionary<HistoryObjectKey, HistoryPackObject>();
        var catalog = new Dictionary<LocalReplicaId, LocalReplicaCatalogEntry>();
        void Add(object value)
        {
            var obj = _codec.CreateObject(value);
            if (facts.TryGetValue(obj.Key, out var previous) && previous.PayloadHash != obj.PayloadHash)
                throw new InvalidDataException("Conflicting legacy immutable identity.");
            facts[obj.Key] = obj;
        }
        SourceVersion Version(string origin, SourceId source, string path, string name, DateTimeOffset timestamp,
            bool partial, bool support = false) => new(
                LegacyHistoryMigrationIdentityV1.Version(origin), input.ConfigId, source, [], timestamp, null,
                partial ? CaptureScope.PartialSource : CaptureScope.FullSource, CaptureOutcome.Recovered, [],
                new SourceDescriptorSnapshot(name, path), null,
                new HistoryProvenance(support ? HistoryOrigin.LegacyMetadataRecovery : HistoryOrigin.LegacyMigration, "", origin),
                creationKind: SourceVersionCreationKind.Import);

        foreach (var group in entries)
        {
            if (group.Distinct().Count() != 1) continue;
            var entry = group.First();
            if (!sources.TryGetValue(entry.SourceId, out var source)) continue;
            var origin = group.Key;
            var timestamp = ToUtc(entry.Timestamp);
            var version = Version(origin, entry.SourceId, entry.OriginalFolderPath, entry.FolderName, timestamp, entry.IsPartialBackup);
            Add(version);
            Add(new SourceCheckpoint(LegacyHistoryMigrationIdentityV1.Checkpoint(origin), input.ConfigId, timestamp,
                null, version.Provenance,
                [new(version.SourceId, version.SourceDescriptorSnapshot, version.VersionId, CheckpointSourceDisposition.Captured, version.EffectiveSourceBoundary)],
                [], CheckpointCreationKind.Import));
            var diagnostics = new List<HistoryDiagnostic>
            { new("legacy.boundary.unknown", HistoryDiagnosticSeverity.Warning, LegacyRecoveryPolicy.BoundaryDiagnostic) };
            if (entry.IsCloudArchived)
                diagnostics.Add(new("legacy.cloud.not-migrated", HistoryDiagnosticSeverity.Warning,
                    "Old cloud archives and settings are not migrated. Create a new cloud configuration; retrieve old archives and Smart metadata yourself."));
            Add(new LegacyMigrationRecord(LegacyHistoryMigrationIdentityV1.Record(origin), origin, version.VersionId,
                LegacyMigrationVisibility.Timeline, timestamp, diagnostics));
            if (!string.IsNullOrWhiteSpace(entry.Comment)) Annotate(HistoryAnnotationKind.Comment, entry.Comment);
            if (entry.IsImportant) Annotate(HistoryAnnotationKind.Pin, "true");
            void Annotate(HistoryAnnotationKind kind, string text) => Add(new HistoryAnnotationUpdate(
                LegacyHistoryMigrationIdentityV1.Annotation(origin, kind),
                new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Version, version.VersionId.Value), kind, [], text, timestamp));

            if (!LegacySmartPlan.SafeArchive(entry.FileName)) continue;
            if (input.Entries.Count(e => e.SourceId == entry.SourceId
                && e.FileName.Equals(entry.FileName, StringComparison.OrdinalIgnoreCase)) > 1)
                continue; // A mutable legacy archive cannot prove which historical generation it contains.
            try
            {
                var records = LegacySmartPlan.RecordsForTarget(entry.SourceId, entry.ResolvedArchivePath, input.SmartRecords);
                var rootRecords = records.Where(r => r.ArchiveFileName.Equals(entry.FileName, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (rootRecords.Length > 1 || rootRecords.Any(r => r.Diagnostic.Length != 0)) continue;
                var isSmart = entry.BackupType.Equals("Smart", StringComparison.OrdinalIgnoreCase);
                LegacySmartPlan? plan = isSmart ? LegacySmartPlan.Build(entry.SourceId, entry.FileName, records) : null;
                var manifest = rootRecords.SingleOrDefault()?.FullFileList;
                if (manifest is not null) _ = LegacySmartPlan.Paths(manifest);
                if (!isSmart && !entry.BackupType.Equals("Full", StringComparison.OrdinalIgnoreCase)
                    && manifest is null) continue;
                var metadata = new Dictionary<string, string>
                { [LegacySmartPlan.ContractKey] = "1", ["legacyFileName"] = entry.FileName, ["legacyBackupType"] = entry.BackupType };
                if (manifest is not null) metadata[LegacySmartPlan.FilesKey] = JsonSerializer.Serialize(
                    LegacySmartPlan.Paths(manifest).OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
                var dependencies = new List<RepresentationId>();
                if (plan is not null)
                {
                    metadata[LegacySmartPlan.OwnersKey] = plan.EncodeOwners();
                    foreach (var file in plan.Chain.Where(f => !f.Equals(entry.FileName, StringComparison.OrdinalIgnoreCase)))
                    {
                        var supportOrigin = $"legacy182-support|{origin}|{file.ToUpperInvariant()}";
                        var supportVersion = Version(supportOrigin, entry.SourceId, entry.OriginalFolderPath, entry.FolderName,
                            DateTimeOffset.UnixEpoch, true, true);
                        var supportMetadata = new Dictionary<string, string>
                        { [LegacySmartPlan.ContractKey] = "1", ["legacyFileName"] = file, ["legacy182Support"] = "true" };
                        var support = new VersionRepresentation(LegacyHistoryMigrationIdentityV1.Representation(supportOrigin),
                            supportVersion.VersionId, RepresentationKind.LegacyArchive, Format(file), [], MaterializationFidelity.Partial,
                            null, null, supportMetadata);
                        Add(supportVersion); Add(support);
                        Add(new LegacyMigrationRecord(LegacyHistoryMigrationIdentityV1.Record(supportOrigin), supportOrigin,
                            supportVersion.VersionId, LegacyMigrationVisibility.SupportOnly, DateTimeOffset.UnixEpoch, []));
                        dependencies.Add(support.RepresentationId);
                        var recordPath = records.Single(r => r.ArchiveFileName.Equals(file, StringComparison.OrdinalIgnoreCase)).ArchivePath;
                        var known = input.Entries.Where(e => e.SourceId == entry.SourceId
                            && e.FileName.Equals(file, StringComparison.OrdinalIgnoreCase)).ToArray();
                        var supportPath = known.Length == 1 && !string.IsNullOrEmpty(known[0].ResolvedArchivePath)
                            ? known[0].ResolvedArchivePath : recordPath;
                        Register(support, supportOrigin, supportPath ?? Path.Combine(source.ArchiveDirectory, file), DateTimeOffset.UnixEpoch);
                    }
                }
                var root = new VersionRepresentation(LegacyHistoryMigrationIdentityV1.Representation(origin), version.VersionId,
                    RepresentationKind.LegacyArchive, Format(entry.FileName), dependencies, MaterializationFidelity.Partial,
                    null, null, metadata);
                Add(root);
                Register(root, origin, entry.ResolvedArchivePath ?? Path.Combine(source.ArchiveDirectory, entry.FileName), timestamp);
            }
            catch (InvalidDataException) { /* Keep logical history; the takeover report explains blocked metadata. */ }
        }
        return new(facts.Count == 0 ? [] : [new HistoryCommitPack(PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UnixEpoch,
            facts.Values.OrderBy(f => f.Kind, StringComparer.Ordinal).ThenBy(f => f.Id, StringComparer.Ordinal))], new LocalReplicaCatalog(input.ConfigId, 0, catalog.Values),
            new HistoryWorkspace(input.ConfigId, 0, input.Sources.Select(s => new WorkspaceSourceBaseline(s.SourceId, null, WorkspaceBaselineRelation.Unknown))));

        void Register(VersionRepresentation representation, string key, string path, DateTimeOffset timestamp)
        {
            if (path.Length == 0 || !_fileExists(path)) return;
            var id = LegacyHistoryMigrationIdentityV1.LocalReplica(key);
            catalog[id] = new(representation.RepresentationId, id, LocalReplicaLocator.ControlledAbsolute(path), timestamp);
        }
    }
    public static string OriginKey(HistoryConfigId configId, LegacyHistoryEntrySnapshot entry)
        => LegacySourceIdentityV1.CreateHistoryOriginKey(configId.Value, entry.OriginalFolderPath, entry.FileName, entry.Timestamp);
    private static DateTimeOffset ToUtc(DateTime value) => value.Kind == DateTimeKind.Unspecified
        ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)) : new DateTimeOffset(value.ToUniversalTime());
    private static string Format(string name) => Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
}

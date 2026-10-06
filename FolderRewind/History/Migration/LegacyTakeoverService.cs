using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Legacy;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using FolderRewind.History.Representation;
using FolderRewind.Plugin.Runtime.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Migration;

public sealed record LegacyTakeoverItem(string OriginKey, LegacyHistoryRecord Original, string Status,
    string Diagnostic, string[] Candidates, SourceId? SourceId, string? VerificationFingerprint = null);

public sealed record LegacyTakeoverReport
{
    public string OperationStatus { get; init; } = "IndexPending";
    public string InputStatus { get; init; } = "Missing";
    public string InputSha256 { get; init; } = "";
    public string Diagnostic { get; init; } = "";
    public string CloudNotice { get; init; } = "1.9 不迁移旧云存档及云配置。原远端数据保持不变，请自行创建新的云配置。旧 Smart 备份如需取回，请同时保留所需依赖归档和元数据。";
    public LegacyTakeoverItem[] Items { get; init; } = [];
    public Dictionary<string, Guid> SourceSelections { get; init; } = new();
    public Dictionary<string, string> ArchiveSelections { get; init; } = new();
    public bool SummaryAcknowledged { get; init; }
}

/// <summary>Device-local evidence and choices. Never authoritative cloud facts or deletion permission.</summary>
public sealed class LegacyTakeoverService(string configDirectory, HistoryConfigId configId)
{
    internal Action<string>? StageObserver { get; init; }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public string Root => Path.Combine(configDirectory, "legacy-takeover", HistoryRepositoryPaths.EncodeConfigPathSegment(configId));
    public string ReportPath => Path.Combine(Root, "report.json");
    public LegacyTakeoverReport ReadReport()
    {
        if (!File.Exists(ReportPath)) return new();
        try
        {
            var report = JsonSerializer.Deserialize<LegacyTakeoverReport>(File.ReadAllBytes(ReportPath), Json);
            if (report is null || report.Items is null || report.SourceSelections is null || report.ArchiveSelections is null)
                throw new JsonException("Incomplete takeover report.");
            return report;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { return new() { Diagnostic = "Takeover report requires recovery: " + ex.Message }; }
    }

    public void SaveReport(LegacyTakeoverReport report) => WriteAtomic(ReportPath, JsonSerializer.SerializeToUtf8Bytes(report, Json));

    public async Task<LegacyTakeoverItem> VerifyAsync(HistoryRuntime runtime, string originKey,
        IArchiveRepresentationBackend backend, CancellationToken token = default)
    {
        await using var operation = await Services.NativeHistoryConfigurationOperationGate.EnterHistoryAsync(configId, token).ConfigureAwait(false);
        await using var gate = await runtime.MutationGate.EnterAsync(token).ConfigureAwait(false);
        var report = ReadReport();
        var item = report.Items.Single(i => i.OriginKey == originKey);
        var staging = Path.Combine(runtime.Repository.Paths.LocalStateRoot, "legacy-validation", Guid.NewGuid().ToString("N"));
        try
        {
            var fingerprint = VerificationFingerprint(item);
            var graph = await runtime.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false);
            var catalog = (await runtime.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false)).Value
                ?? throw new InvalidDataException("Replica catalog requires recovery.");
            var environment = new RepresentationEnvironment(catalog.Entries, [], []);
            var engine = new RepresentationRuntime([new CoreArchiveRepresentationHandler(backend)]);
            using var lease = await engine.LockVersionAsync(LegacyHistoryMigrationIdentityV1.Version(originKey), graph,
                environment, MaterializationFidelity.Partial, token).ConfigureAwait(false);
            await engine.MaterializeAsync(lease.RepresentationId, graph, environment, MaterializationFidelity.Partial, staging, token).ConfigureAwait(false);
            if (fingerprint is null || fingerprint != VerificationFingerprint(item))
                throw new IOException("Legacy archives or metadata changed during verification; recheck before restoring.");
            item = item with { Status = "RestrictedReady", Diagnostic = LegacyRecoveryPolicy.BoundaryDiagnostic,
                VerificationFingerprint = fingerprint };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var diagnostic = ex is Merge.HistoryMergeBlockedException blocked
                && !string.IsNullOrWhiteSpace(blocked.Diagnostic.Detail) ? blocked.Diagnostic.Detail : ex.Message;
            var archive = item.Candidates.FirstOrDefault(File.Exists) ?? item.Candidates.FirstOrDefault();
            if (archive is not null)
                diagnostic += " Metadata directory: " + LegacySmartMetadataReader.MetadataDirectory(Path.GetDirectoryName(archive)!);
            item = item with { Status = "Blocked", Diagnostic = diagnostic, VerificationFingerprint = null };
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        SaveReport(report with { Items = report.Items.Select(i => i.OriginKey == originKey ? item : i).ToArray() });
        return item;
    }

    public (LegacyHistoryMigrationInput Input, LegacyTakeoverReport Report) Prepare(
        IReadOnlyList<LegacyMigrationSourceSnapshot> sources, string destination)
    {
        var previous = ReadReport();
        var raw = new List<LegacyHistoryRecord>();
        string status = "Missing", fingerprint = previous.InputSha256, diagnostic = string.Empty;
        var history = Path.Combine(configDirectory, "history.json");
        try
        {
            if (File.Exists(history))
            {
                var bytes = File.ReadAllBytes(history);
                fingerprint = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                var records = JsonSerializer.Deserialize<LegacyHistoryRecord[]>(bytes, Json)
                    ?? throw new JsonException("Legacy history is null.");
                raw.AddRange(records.Where(r => r is not null && !string.IsNullOrWhiteSpace(r.ConfigId) && configId.Matches(r.ConfigId)));
                status = records.Length == 0 ? "Empty" : "Read";
                var snapshot = Path.Combine(Root, "inputs", fingerprint + ".json");
                if (!File.Exists(snapshot)) WriteAtomic(snapshot, bytes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { status = "Unreadable"; diagnostic = ex.Message; }
        // Saved input survives a disconnected disk, a missing history file, or a corrupt report.
        if (Directory.Exists(Path.Combine(Root, "inputs")))
            foreach (var path in Directory.EnumerateFiles(Path.Combine(Root, "inputs"), "*.json"))
                try
                {
                    raw.AddRange((JsonSerializer.Deserialize<LegacyHistoryRecord[]>(File.ReadAllBytes(path), Json) ?? [])
                        .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.ConfigId) && configId.Matches(r.ConfigId)));
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                { diagnostic = "Saved legacy input requires recovery: " + ex.Message; }
        var items = new List<LegacyTakeoverItem>();
        var entries = new List<LegacyHistoryEntrySnapshot>();
        var locations = new HashSet<(SourceId, string)>();
        foreach (var group in raw.Distinct().GroupBy(r => LegacySourceIdentityV1.CreateHistoryOriginKey(
                     configId.Value, r.FolderPath, r.FileName, r.Timestamp)).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var record = group.First();
            var source = previous.SourceSelections.TryGetValue(group.Key, out var selected)
                ? sources.SingleOrDefault(s => s.SourceId.Value == selected) : LegacyArchiveLocator.ResolveSource(record, sources);
            previous.ArchiveSelections.TryGetValue(group.Key, out var selectedPath);
            var location = LegacyArchiveLocator.Locate(destination, record, source, selectedPath);
            if (group.Count() > 1 || source is null)
            {
                items.Add(new(group.Key, record, "Unassigned", group.Count() > 1 ? "Conflicting legacy identity." : "Source association requires confirmation.", location.Candidates, null));
                continue;
            }
            foreach (var candidate in location.Selected is { } chosen ? new[] { chosen } : location.Candidates)
                locations.Add((source.SourceId, Path.GetDirectoryName(candidate)!));
            entries.Add(new(source.SourceId, record.FolderPath ?? "", record.FolderName ?? "", record.FileName ?? "", record.Timestamp,
                record.BackupType ?? "", record.Comment ?? "", record.IsImportant, record.IsPartialBackup, record.IsCloudArchived,
                "", location.Selected ?? ""));
            items.Add(new(group.Key, record, location.Selected is null ? "Locate" : "Verify",
                location.Diagnostic + (record.IsCloudArchived && location.Selected is null
                    ? " Old cloud data was not migrated; no local archive is available. Create a new cloud configuration yourself." : ""), location.Candidates, source.SourceId));
        }
        var smart = new List<LegacySmartRecordSnapshot>();
        foreach (var location in locations.OrderBy(l => l.Item2, StringComparer.OrdinalIgnoreCase))
            try { smart.AddRange(LegacySmartMetadataReader.Read([location])); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostic = ex.Message;
                foreach (var entry in entries.Where(e => e.SourceId == location.Item1 && e.BackupType.Equals("Smart", StringComparison.OrdinalIgnoreCase)))
                    smart.Add(new(entry.SourceId, entry.FileName, "", "", Diagnostic: ex.Message));
            }
        foreach (var entry in entries.Where(e => e.BackupType.Equals("Smart", StringComparison.OrdinalIgnoreCase)))
        {
            var key = LegacyHistoryMigrationBuilder.OriginKey(configId, entry);
            var index = items.FindIndex(i => i.OriginKey == key);
            var archiveDirectory = Path.GetDirectoryName(entry.ResolvedArchivePath ?? "")
                ?? items[index].Candidates.Select(Path.GetDirectoryName).FirstOrDefault(p => p is not null);
            var metadataDiagnostic = archiveDirectory is null ? ""
                : " Metadata directory: " + LegacySmartMetadataReader.MetadataDirectory(archiveDirectory);
            try
            {
                var records = LegacySmartPlan.RecordsForTarget(entry.SourceId, entry.ResolvedArchivePath, smart);
                var plan = LegacySmartPlan.Build(entry.SourceId, entry.FileName, records);
                var missing = plan.Chain.Where(name => !File.Exists(records.Single(r => r.ArchiveFileName.Equals(name,
                    StringComparison.OrdinalIgnoreCase)).ArchivePath)).ToArray();
                if (missing.Length > 0)
                    items[index] = items[index] with { Status = "Locate", Diagnostic = "Legacy dependency archives missing: "
                        + string.Join(", ", missing) + metadataDiagnostic };
                else
                    items[index] = items[index] with { Diagnostic = items[index].Diagnostic + metadataDiagnostic };
            }
            catch (InvalidDataException ex)
            {
                items[index] = items[index] with { Status = "Blocked", Diagnostic = ex.Message + metadataDiagnostic };
            }
        }
        foreach (var group in entries.GroupBy(e => (e.SourceId, File: e.FileName.ToUpperInvariant())).Where(g => g.Count() > 1))
            foreach (var entry in group)
            {
                var key = LegacyHistoryMigrationBuilder.OriginKey(configId, entry);
                var index = items.FindIndex(i => i.OriginKey == key);
                items[index] = items[index] with { Status = "Blocked", Diagnostic = "Multiple historical generations refer to the same archive filename; content identity cannot be proven." };
            }
        var input = new LegacyHistoryMigrationInput(configDirectory, configId, sources, entries, smart);
        var definitions = new LegacyHistoryMigrationBuilder().Build(input).Packs.SelectMany(p => p.Objects)
            .Where(o => o.Kind == HistoryObjectKinds.VersionRepresentation).Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < items.Count; index++)
            if (items[index].Status == "Verify" && !definitions.Contains(LegacyHistoryMigrationIdentityV1.Representation(items[index].OriginKey).ToString()))
                items[index] = items[index] with { Status = "Blocked", Diagnostic = "Legacy representation metadata is incomplete or ambiguous; no independent archive was fabricated." };
        var previousItems = previous.Items.ToDictionary(i => i.OriginKey);
        for (var index = 0; index < items.Count; index++)
            if (items[index].Status == "Verify" && previousItems.TryGetValue(items[index].OriginKey, out var verified)
                && verified.Status == "RestrictedReady" && verified.SourceId == items[index].SourceId
                && verified.VerificationFingerprint is { } verificationFingerprint && verificationFingerprint == VerificationFingerprint(items[index]))
                items[index] = items[index] with { Status = "RestrictedReady", VerificationFingerprint = verificationFingerprint,
                    Diagnostic = LegacyRecoveryPolicy.BoundaryDiagnostic + " " + items[index].Diagnostic };
        var report = previous with { InputStatus = status, InputSha256 = fingerprint, Diagnostic = diagnostic, Items = items.ToArray() };
        SaveReport(report);
        return (input, report);
    }

    public async Task<LegacyTakeoverReport> ResumeAsync(HistoryRuntime runtime,
        IReadOnlyList<LegacyMigrationSourceSnapshot> sources, string destination, CancellationToken token = default)
    {
        await using var operation = await Services.NativeHistoryConfigurationOperationGate.EnterHistoryAsync(configId, token).ConfigureAwait(false);
        await using (var recoveryGate = await runtime.MutationGate.EnterForRecoveryAsync(token).ConfigureAwait(false))
            await runtime.Repository.Journals.RecoverAsync(
                new HistoryLocalStateJournalRecovery(runtime.WorkspaceStore, runtime.LocalReplicaCatalogStore).ApplyCommittedStateAsync,
                (_, _) => Task.CompletedTask, token).ConfigureAwait(false);
        await using var gate = await runtime.MutationGate.EnterAsync(token).ConfigureAwait(false);
        var (input, report) = Prepare(sources, destination);
        var build = new LegacyHistoryMigrationBuilder().Build(input);
        StageObserver?.Invoke("Prepared");
        var existing = (await runtime.Repository.ReadAllPacksAsync(token).ConfigureAwait(false))
            .SelectMany(p => p.Pack.Objects).GroupBy(o => o.Key).ToDictionary(g => g.Key, g => g.First());
        var additions = build.Packs.SelectMany(p => p.Objects).Where(o => !existing.ContainsKey(o.Key)).ToArray();
        var conflicts = build.Packs.SelectMany(p => p.Objects).Where(o => existing.TryGetValue(o.Key, out var old)
            && old.PayloadHash != o.PayloadHash && !CanReuseFullWithoutManifest(old, o)).ToArray();
        if (conflicts.Length > 0)
        {
            report = report with { OperationStatus = "NeedsAttention", Diagnostic = "Saved legacy facts differ from the current input. Existing history was preserved; automatic completion is blocked. Conflicting objects: "
                + string.Join(", ", conflicts.Select(o => o.Kind + "/" + o.Id)) };
            SaveReport(report); return report;
        }
        var loaded = await runtime.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false);
        if (loaded.Status is DeviceLocalStateStatus.Corrupt or DeviceLocalStateStatus.Inaccessible)
            throw new InvalidDataException("Local catalog requires recovery before legacy completion.");
        var catalogRevision = loaded.Value?.CatalogRevision ?? LocalReplicaCatalogStore.MissingRevision;
        var merged = (loaded.Value?.Entries ?? []).ToDictionary(e => e.LocalReplicaId);
        foreach (var entry in build.LocalReplicaCatalog.Entries) merged[entry.LocalReplicaId] = entry;
        var changed = loaded.Value is null || !merged.Values.OrderBy(e => e.LocalReplicaId.ToString())
            .SequenceEqual(loaded.Value.Entries.OrderBy(e => e.LocalReplicaId.ToString()));
        var workspace = await runtime.WorkspaceStore.LoadAsync(token).ConfigureAwait(false);
        if (workspace.Status is DeviceLocalStateStatus.Corrupt or DeviceLocalStateStatus.Inaccessible)
            throw new InvalidDataException("Workspace requires recovery before legacy completion.");
        if (additions.Length > 0)
        {
            report = report with { OperationStatus = "PublishingHistory" };
            SaveReport(report);
            var pack = new HistoryCommitPack(PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow, additions);
            var catalog = new LocalReplicaCatalog(configId, catalogRevision + 1, merged.Values);
            var intents = new List<HistoryLocalStateIntent>();
            if (changed) intents.Add(HistoryLocalStateJournalRecovery.CreateCatalogIntent(catalog, catalogRevision));
            if (workspace.Value is null)
                intents.Add(HistoryLocalStateJournalRecovery.CreateWorkspaceIntent(build.Workspace, HistoryWorkspaceStore.MissingRevision));
            var journal = HistoryTransactionJournal.Prepared(pack.TransactionId, pack.PackId, intents);
            await runtime.Repository.CommitAsync(pack, journal, token).ConfigureAwait(false);
            report = report with { OperationStatus = "CompletingLocalState" };
            SaveReport(report);
            StageObserver?.Invoke("PackCommitted");
            await new HistoryLocalStateJournalRecovery(runtime.WorkspaceStore, runtime.LocalReplicaCatalogStore)
                .ApplyCommittedStateAsync(journal, token).ConfigureAwait(false);
            StageObserver?.Invoke("CatalogApplied");
            runtime.Repository.Journals.Save(journal with { Phase = HistoryTransactionPhase.Complete });
        }
        else
        {
            if (changed)
                await runtime.LocalReplicaCatalogStore.SaveAsync(new(configId, catalogRevision + 1, merged.Values), catalogRevision, token).ConfigureAwait(false);
            if (workspace.Value is null)
                await runtime.WorkspaceStore.SaveAsync(build.Workspace, HistoryWorkspaceStore.MissingRevision, token).ConfigureAwait(false);
        }
        await runtime.EnsureIndexCurrentAsync(token).ConfigureAwait(false);
        await runtime.RefreshLocalStateHealthAsync(token).ConfigureAwait(false);
        runtime.ChangeFeed.Publish(configId, HistoryChangeKind.LocalStateChanged);
        report = report with { OperationStatus = "IndexImported" };
        SaveReport(report);
        StageObserver?.Invoke("ReportSaved");
        return report;
    }

    private static bool CanReuseFullWithoutManifest(HistoryPackObject saved, HistoryPackObject incoming)
    {
        if (saved.Kind != HistoryObjectKinds.VersionRepresentation || incoming.Kind != saved.Kind
            || saved.SchemaVersion != incoming.SchemaVersion) return false;
        var codec = new HistoryPackCodec();
        if (codec.DeserializeKnown(saved) is not VersionRepresentation old
            || codec.DeserializeKnown(incoming) is not VersionRepresentation current
            || old.Kind != RepresentationKind.LegacyArchive || current.Kind != RepresentationKind.LegacyArchive
            || old.RepresentationSpecificMetadata.GetValueOrDefault("legacyBackupType") != "Full"
            || old.RepresentationSpecificMetadata.GetValueOrDefault(LegacySmartPlan.ContractKey) != "1"
            || old.RepresentationSpecificMetadata.ContainsKey(LegacySmartPlan.FilesKey)
            || !current.RepresentationSpecificMetadata.ContainsKey(LegacySmartPlan.FilesKey)) return false;
        var withoutManifest = new VersionRepresentation(current.RepresentationId, current.VersionId, current.Kind,
            current.Format, current.DependencyRepresentationIds, current.Fidelity, current.LogicalSha256,
            current.StateFingerprint, current.RepresentationSpecificMetadata.Remove(LegacySmartPlan.FilesKey));
        return codec.CreateObject(withoutManifest, incoming.SchemaVersion).PayloadHash == saved.PayloadHash;
    }

    // A report's verification is reusable only while archive locations/stamps and metadata bytes stay unchanged.
    private static string? VerificationFingerprint(LegacyTakeoverItem item)
    {
        var archive = item.Candidates.FirstOrDefault(File.Exists);
        if (archive is null) return null;
        try
        {
            var directory = Path.GetDirectoryName(archive)!;
            var metadata = LegacySmartMetadataReader.MetadataDirectory(directory);
            var inputs = Directory.EnumerateFiles(directory).Where(path => Path.GetExtension(path).ToLowerInvariant() is ".7z" or ".zip")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Select(path =>
                {
                    var info = new FileInfo(path);
                    return path + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
                }).ToList();
            if (Directory.Exists(metadata))
                inputs.AddRange(Directory.EnumerateFiles(metadata, "*.json", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .Select(path => path + "|" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
            inputs.Add(archive);
            return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", inputs))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

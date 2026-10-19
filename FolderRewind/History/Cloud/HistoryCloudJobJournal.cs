using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Cloud;

public enum CloudJobPhase { Prepared, Pending, Resolving, Uploading, Verifying, SyncingMetadata, Succeeded, RetryableFailure, Paused, NeedsAttention, Canceled, Aborted }
public enum CloudProtectionDisposition { Held, Released, Abandoned }
public sealed record CloudJobTarget(string ProfileId, string ProfileRevision, string BindingRevision, string RemoteRoot, string ConfigId,
    string CredentialTargetRevision = "", string DescriptorSha256 = "", string CredentialRevision = "", string ExecutionWorkingDirectory = "");
public sealed record CloudAdmissionInterval(string Id, CloudJobTarget Target, ImmutableArray<string> BaselineTransactions,
    ImmutableArray<string>? EndTransactions);
public sealed record CloudUploadIntent(RepresentationId RepresentationId, ReplicaId ReplicaId, HistoryReplicaManifest? Manifest);
public sealed record HistoryCloudJob(string JobId, string ProjectId, string AdmissionId, PackId PackId, HistoryTransactionId TransactionId,
    CloudJobTarget Target, ImmutableArray<RepresentationId> Representations, ImmutableArray<CloudUploadIntent> UploadIntents,
    CloudJobPhase Phase, CloudProtectionDisposition Protection, int Attempt, DateTimeOffset NextEligibleUtc, string Diagnostic,
    DateTimeOffset CreatedUtc, bool Automatic);
public sealed record HistoryCloudJournalDocument(int Version, long Revision, ImmutableArray<CloudAdmissionInterval> Admissions,
    ImmutableArray<HistoryCloudJob> Jobs, bool UnknownProtectionAbandoned = false);

// 本机辅助证据，不进入 Commit Pack/导出。所有保护增减与本地删除共用 History MutationGate。
public sealed class HistoryCloudJobJournal(HistoryRuntime history)
{
    public string JournalPath => Path.Combine(history.Repository.Paths.LocalStateRoot, "cloud-jobs.json");
    private bool _uncertain;
    private readonly object _fileGate = new();
    public string? Diagnostic { get; private set; }
    public Action<string>? Checkpoint { get; set; }
    public HistoryCloudJournalDocument Read()
    {
        lock (_fileGate) return ReadSnapshot();
    }
    private HistoryCloudJournalDocument ReadSnapshot()
    {
        if (_uncertain) throw new InvalidDataException("[cloud.intent-reconciliation-required] " + Diagnostic);
        try
        {
            if (!File.Exists(JournalPath))
            {
                if (history.Repository.CloudAdmissionExpected || File.Exists(JournalPath + ".required")) throw new InvalidDataException("[cloud.intent-reconciliation-required] Expected cloud admission evidence is missing.");
                return new(1, 0, [], []);
            }
            byte[] bytes;
            // Readers observe one immutable file generation while the worker atomically replaces the path.
            // UI polling must not deny that replacement or permanently poison protection on a sharing race.
            using (var snapshot = new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (snapshot.Length > 16 * 1024 * 1024) throw new InvalidDataException("Cloud journal exceeds its bound.");
                bytes = new byte[checked((int)snapshot.Length)];
                snapshot.ReadExactly(bytes);
                if (snapshot.ReadByte() != -1) throw new InvalidDataException("Cloud journal changed during inspection.");
            }
            var document = JsonSerializer.Deserialize(bytes, CloudJobJsonContext.Default.HistoryCloudJournalDocument)
                ?? throw new InvalidDataException("Cloud journal is empty.");
            if (document.Jobs.IsDefault || document.Admissions.IsDefault || document.Jobs.Any(j => j is null || j.Target is null)
                || document.Admissions.Any(a => a is null || a.Target is null)) throw new InvalidDataException("Cloud journal contains null evidence.");
            if (!document.Jobs.IsDefault && !document.Admissions.IsDefault)
                document = document with
                {
                    Jobs = [.. document.Jobs.Select(j => !string.IsNullOrWhiteSpace(j.ProjectId) && !string.IsNullOrWhiteSpace(j.Target.ConfigId)
                        ? j with { ProjectId = new HistoryConfigId(j.ProjectId).Value, Target = j.Target with { ConfigId = new HistoryConfigId(j.Target.ConfigId).Value } } : j)],
                    Admissions = [.. document.Admissions.Select(a => !string.IsNullOrWhiteSpace(a.Target.ConfigId)
                        ? a with { Target = a.Target with { ConfigId = new HistoryConfigId(a.Target.ConfigId).Value } } : a)]
                };
            if (document.Version != 1 || document.Revision < 0 || document.Jobs.IsDefault || document.Admissions.IsDefault
                || document.Jobs.Length > 10000 || document.Admissions.Length > 1000
                || document.Jobs.Any(j => j is null || j.Target is null || !Guid.TryParseExact(j.JobId, "N", out _)
                    || j.ProjectId != history.ConfigId.Value || j.Target.ConfigId != history.ConfigId.Value
                    || !Enum.IsDefined(j.Phase) || !Enum.IsDefined(j.Protection) || j.Representations.IsDefault || j.UploadIntents.IsDefault
                    || j.Representations.Length > 1000 || j.UploadIntents.Length > 1000
                    || j.Representations.Any(r => r.Value == Guid.Empty) || j.Representations.Distinct().Count() != j.Representations.Length
                    || j.UploadIntents.Any(i => i is null || i.RepresentationId.Value == Guid.Empty || i.ReplicaId.Value == Guid.Empty
                        || i.Manifest is { } m && (m.RepresentationId != i.RepresentationId || m.ReplicaId != i.ReplicaId
                            || m.ObjectKey != HistoryRepositoryPaths.CreateReplicaObjectKey(i.ReplicaId) || m.Size < 0
                            || m.StorageSha256 is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit)))
                    || j.UploadIntents.Select(i => i.RepresentationId).Distinct().Count() != j.UploadIntents.Length
                    || j.UploadIntents.Select(i => i.ReplicaId).Distinct().Count() != j.UploadIntents.Length || j.Attempt < 0)
                || document.Jobs.Select(j => j.JobId).Distinct().Count() != document.Jobs.Length
                || document.Admissions.Any(a => a is null || a.Target is null || a.BaselineTransactions.IsDefault || a.Target.ConfigId != history.ConfigId.Value))
                throw new InvalidDataException("Cloud journal evidence is invalid.");
            return document;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { _uncertain = true; Diagnostic = ex.Message; throw; }
    }
    private void Save(HistoryCloudJournalDocument document)
    {
        lock (_fileGate) SaveSnapshot(document);
    }
    private void SaveSnapshot(HistoryCloudJournalDocument document)
    {
        try
        {
            if (document.Jobs.Length > 10000 || document.Admissions.Length > 1000) throw new InvalidDataException("Cloud journal capacity requires attention.");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document with { Revision = checked(document.Revision + 1) }, CloudJobJsonContext.Default.HistoryCloudJournalDocument);
            if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("Cloud journal exceeds its metadata budget.");
            // 独立存在标记先于首次登记；Journal 丢失绝不能被误读成空队列，包括手动任务。
            if (!File.Exists(JournalPath + ".required")) AtomicFileService.Write(JournalPath + ".required", s => s.WriteByte(1));
            Checkpoint?.Invoke("before-journal-write");
            AtomicFileService.Write(JournalPath, s => s.Write(bytes));
            history.ChangeFeed.Publish(history.ConfigId, HistoryChangeKind.CloudJobChanged);
        }
        catch (Exception ex)
        {
            _uncertain = true; Diagnostic = ex.Message;
            // 原接纳区间和基线从不前移；即使此暂停标记也写不出，启动仍按本机事务补账。
            try { Checkpoint?.Invoke("before-attention-write"); AtomicFileService.Write(JournalPath + ".attention", s => s.Write(System.Text.Encoding.UTF8.GetBytes("cloud.intent-reconciliation-required"))); } catch (IOException) { }
            throw;
        }
    }
    private ImmutableArray<string> LocalTransactions() => [.. history.Repository.Journals.EnumerateJournalPaths()
        .Select(p => history.Repository.Journals.Load(p).TransactionId.ToString()).Order(StringComparer.Ordinal)];

    public void ConfigureAdmission(CloudJobTarget? target)
    {
        var document = Read(); var open = document.Admissions.LastOrDefault(a => a.EndTransactions is null);
        if (open?.Target == target || open is not null && target is not null
            && open.Target with { CredentialRevision = target.CredentialRevision } == target) return;
        var transactions = LocalTransactions();
        if (open is not null)
            document = document with { Admissions = document.Admissions.Replace(open, open with { EndTransactions = transactions }) };
        if (target is not null)
            document = document with { Admissions = document.Admissions.Add(new(Guid.NewGuid().ToString("N"), target, transactions, null)) };
        Save(document);
    }
    public void InitializeMissingAdmissionFromNow(CloudJobTarget target)
    {
        if (File.Exists(JournalPath)) throw new InvalidOperationException("Existing cloud evidence must be recovered, not replaced.");
        // 用户明确放弃未知旧保护之后，新的基线与处置记录必须在同一原子文件中落盘。
        var document = new HistoryCloudJournalDocument(1, 0,
            [new(Guid.NewGuid().ToString("N"), target, LocalTransactions(), null)], [], UnknownProtectionAbandoned: true);
        Save(document); _uncertain = false; Diagnostic = null;
        File.Delete(JournalPath + ".attention");
    }

    public void Prepare(HistoryCommitBatch batch, IReadOnlyList<VersionRepresentation> graph)
    {
        var document = Read(); var admission = document.Admissions.LastOrDefault(a => a.EndTransactions is null);
        if (admission is null || batch.Run.Invocation is BackupInvocationKind.Migration or BackupInvocationKind.Recovery or BackupInvocationKind.Internal) return;
        var selected = Select(batch.Run, graph, batch.UpdatedLocalReplicaCatalog?.Entries.Select(e => e.RepresentationId).ToHashSet());
        if (selected.Length == 0 && !batch.Run.SourceResults.Any(r => r.Outcome is BackupRunSourceOutcome.Captured or BackupRunSourceOutcome.Reused)) return;
        Checkpoint?.Invoke("before-prepared");
        Add(document, admission.Id, batch.Pack, admission.Target, selected, CloudJobPhase.Prepared, automatic: true);
        Checkpoint?.Invoke("prepared");
    }
    private static ImmutableArray<RepresentationId> Select(BackupRun run, IReadOnlyList<VersionRepresentation> graph, HashSet<RepresentationId>? local)
    {
        var result = new List<RepresentationId>();
        foreach (var source in run.SourceResults.Where(r => r.VersionId is not null && r.Outcome is BackupRunSourceOutcome.Captured or BackupRunSourceOutcome.Reused))
        {
            var candidate = graph.Where(r => r.VersionId == source.VersionId).OrderByDescending(r => local?.Contains(r.RepresentationId) == true)
                .ThenBy(r => r.Fidelity).ThenBy(r => r.RepresentationId.ToString(), StringComparer.Ordinal).FirstOrDefault();
            if (candidate is not null) result.Add(candidate.RepresentationId);
        }
        return [.. result.Distinct()];
    }
    private void Add(HistoryCloudJournalDocument document, string admission, HistoryCommitPack pack, CloudJobTarget target,
        ImmutableArray<RepresentationId> representations, CloudJobPhase phase, bool automatic)
    {
        if (document.Jobs.Any(j => j.PackId == pack.PackId && j.Target == target && j.Automatic == automatic
            && (automatic || j.Protection == CloudProtectionDisposition.Held && j.Representations.SequenceEqual(representations)))) return;
        var job = new HistoryCloudJob(Guid.NewGuid().ToString("N"), history.ConfigId.Value, admission, pack.PackId,
            pack.TransactionId, target, representations, [], representations.IsEmpty ? CloudJobPhase.NeedsAttention : phase,
            CloudProtectionDisposition.Held, 0, DateTimeOffset.UtcNow, representations.IsEmpty ? "cloud.payload-missing" : "", DateTimeOffset.UtcNow, automatic);
        Save(document with { Jobs = document.Jobs.Add(job) });
    }
    public HistoryCloudJob RegisterManual(HistoryCommitPack pack, CloudJobTarget target, ImmutableArray<RepresentationId> selected)
    {
        Add(Read(), "manual", pack, target, selected, CloudJobPhase.Pending, false);
        return Read().Jobs.Last(j => j.PackId == pack.PackId && j.Target == target && !j.Automatic && j.Representations.SequenceEqual(selected));
    }
    public void Activate(PackId pack)
    {
        var document = Read();
        if (!document.Jobs.Any(j => j.PackId == pack && j.Phase == CloudJobPhase.Prepared)) return;
        Checkpoint?.Invoke("before-activate");
        Save(document with { Jobs = [.. document.Jobs.Select(j => j.PackId == pack && j.Phase == CloudJobPhase.Prepared ? j with { Phase = CloudJobPhase.Pending } : j)] });
    }
    internal async Task ReplayAsync(Func<CancellationToken, Task<IReadOnlyList<HistoryPackReadResult>>> loadPacks,
        CancellationToken token)
    {
        // Preserve the same missing/uncertain/attention checks before the no-journal return.
        // Packs are unnecessary when this device has no cloud journal to reconcile.
        token.ThrowIfCancellationRequested();
        _uncertain = false;
        var document = Read();
        if (File.Exists(JournalPath + ".attention") && document.Admissions.IsEmpty)
            throw new InvalidDataException("[cloud.intent-reconciliation-required] Admission evidence is missing.");
        if (!File.Exists(JournalPath)) return;
        Replay(await loadPacks(token).ConfigureAwait(false));
    }

    public void Replay(IReadOnlyList<HistoryPackReadResult> packs)
    {
        // 本地 History 与 Local State 已恢复之后，补账必须先于链重写收尾/GC。
        _uncertain = false;
        var document = Read(); var map = packs.ToDictionary(p => p.Pack.PackId);
        if (File.Exists(JournalPath + ".attention") && document.Admissions.IsEmpty)
            throw new InvalidDataException("[cloud.intent-reconciliation-required] Admission evidence is missing.");
        if (!File.Exists(JournalPath)) return;
        var journals = history.Repository.Journals.EnumerateJournalPaths().Select(history.Repository.Journals.Load).ToArray();
        var codec = new HistoryPackCodec();
        var graph = packs.SelectMany(p => p.Pack.Objects).Where(o => o.Kind == HistoryObjectKinds.VersionRepresentation)
            .Select(o => (VersionRepresentation)codec.DeserializeKnown(o)).GroupBy(r => r.RepresentationId).Select(g => g.First()).ToArray();
        foreach (var admission in document.Admissions)
        {
            var baseline = admission.BaselineTransactions.ToHashSet(); var end = admission.EndTransactions?.ToHashSet();
            foreach (var local in journals.Where(j => !baseline.Contains(j.TransactionId.ToString()) && (end is null || end.Contains(j.TransactionId.ToString()))))
            {
                if (!map.TryGetValue(local.IntendedPackId, out var persisted)) continue;
                var runs = persisted.Pack.Objects.Where(o => o.Kind == HistoryObjectKinds.BackupRun).Select(o => (BackupRun)codec.DeserializeKnown(o));
                foreach (var run in runs.Where(r => r.Invocation is not (BackupInvocationKind.Migration or BackupInvocationKind.Recovery or BackupInvocationKind.Internal)
                    && r.SourceResults.Any(s => s.Outcome is BackupRunSourceOutcome.Captured or BackupRunSourceOutcome.Reused)))
                {
                    Add(Read(), admission.Id, persisted.Pack, admission.Target, Select(run, graph, null), CloudJobPhase.Pending, true);
                }
            }
        }
        document = Read();
        var jobs = document.Jobs.Select(j =>
        {
            if (j.Phase == CloudJobPhase.Prepared)
            {
                if (map.ContainsKey(j.PackId)) return j with { Phase = CloudJobPhase.Pending };
                if (journals.Any(t => t.TransactionId == j.TransactionId && t.Phase != HistoryTransactionPhase.Complete)) return j with { Phase = CloudJobPhase.NeedsAttention, Diagnostic = "cloud.intent-reconciliation-required" };
                return j with { Phase = CloudJobPhase.Aborted, Protection = CloudProtectionDisposition.Released };
            }
            return j.Phase is CloudJobPhase.Resolving or CloudJobPhase.Uploading or CloudJobPhase.Verifying or CloudJobPhase.SyncingMetadata
                ? j with { Phase = CloudJobPhase.Pending } : j;
        }).ToImmutableArray();
        Save(document with { Jobs = jobs });
        Checkpoint?.Invoke("reconciled");
        File.Delete(JournalPath + ".attention"); Diagnostic = null;
    }
    public void Update(HistoryCloudJob job)
    {
        var document = Read(); var previous = document.Jobs.Single(j => j.JobId == job.JobId);
        if (previous.Target != job.Target || previous.PackId != job.PackId || previous.TransactionId != job.TransactionId
            || previous.ProjectId != job.ProjectId || previous.AdmissionId != job.AdmissionId || previous.Automatic != job.Automatic
            || previous.CreatedUtc != job.CreatedUtc || !previous.Representations.SequenceEqual(job.Representations))
            throw new InvalidOperationException("Cloud intent target cannot be changed.");
        if (job.Attempt < previous.Attempt || previous.Protection != CloudProtectionDisposition.Held
            && (job.Protection != previous.Protection || job.Phase != previous.Phase))
            throw new InvalidOperationException("Cloud intent cannot restore released protection or move attempts backwards.");
        if (job.UploadIntents.IsDefault || job.UploadIntents.Length > 1000
            || job.UploadIntents.Any(i => i is null || i.RepresentationId.Value == Guid.Empty || i.ReplicaId.Value == Guid.Empty
                || i.Manifest is { } m && (m.RepresentationId != i.RepresentationId || m.ReplicaId != i.ReplicaId
                    || m.ObjectKey != HistoryRepositoryPaths.CreateReplicaObjectKey(i.ReplicaId) || m.Size < 0
                    || m.StorageSha256 is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit)))
            || job.UploadIntents.Select(i => i.RepresentationId).Distinct().Count() != job.UploadIntents.Length
            || job.UploadIntents.Select(i => i.ReplicaId).Distinct().Count() != job.UploadIntents.Length
            || previous.UploadIntents.Any(old => !job.UploadIntents.Any(i => i.RepresentationId == old.RepresentationId && i.ReplicaId == old.ReplicaId
                && (old.Manifest is null || i.Manifest is not null && HistoryCloudReplicaValidity.Matches(i.Manifest, old.Manifest)))))
            throw new InvalidOperationException("Cloud upload identity and published payload evidence cannot be replaced.");
        Save(document with { Jobs = document.Jobs.Replace(previous, job) });
    }
    public ImmutableArray<RepresentationId> ProtectedRoots()
        => [.. Read().Jobs.Where(j => j.Protection == CloudProtectionDisposition.Held).SelectMany(j => j.Representations).Distinct()];
    public string ProtectionFingerprint() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(Read(), CloudJobJsonContext.Default.HistoryCloudJournalDocument)));
    public void RequireCleanupSafe()
    {
        var document = Read();
        if (File.Exists(JournalPath + ".attention") || document.Jobs.Any(j => j.Protection == CloudProtectionDisposition.Held))
            throw new InvalidOperationException("[cloud.intent-reconciliation-required] Pending cloud backups retain local payload protection.");
    }
    public void RequireKnownProtection()
    {
        _ = Read();
        if (File.Exists(JournalPath + ".attention"))
            throw new InvalidOperationException("[cloud.intent-reconciliation-required] Cloud protection evidence needs reconciliation.");
    }
    public void ObserveFailure(Exception error) { _uncertain = true; Diagnostic = error.Message; }
}

[JsonSerializable(typeof(HistoryCloudJournalDocument))]
internal partial class CloudJobJsonContext : JsonSerializerContext;

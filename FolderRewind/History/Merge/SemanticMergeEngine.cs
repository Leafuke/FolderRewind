using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.Plugin.Abstractions;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Merge;

public sealed class SemanticMergeEngine(MergeSessionStore store, SemanticMergeBinding binding, ISemanticMergeResolver resolver)
{
    public SemanticMergeBinding Binding { get; } = binding;
    public MergeProviderDescriptor Descriptor => Binding.Descriptor;
    internal ValueTask<SemanticMergeInvocation> AcquireAsync(CancellationToken token) => resolver.AcquireAsync(Binding, token);

    internal async Task<MergeFileProposal> AnalyzeAsync(MergeSession session, HistoryMergeSourcePlan source,
        MergeTreeManifest b, MergeTreeManifest o, MergeTreeManifest t, IHistoryMergeProvider generic, CancellationToken token)
    {
        RequireBinding(session);
        using var invocation = await resolver.AcquireAsync(Binding, token).ConfigureAwait(false);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, invocation.Context.PluginLifetime);
        token = lifetime.Token;
        using var views = new InputViews(store.SessionDirectory(session.Id), source, b, o, t, token);
        var request = Request(session, source, b, o, t, views);
        var include = FileSystemHistoryRestoreMutationBackend.CreateBoundaryMatcher(new(source.SourceId,
            store.SessionDirectory(session.Id), source.Ours!.EffectiveSourceBoundary) { LogicalDirectory = source.Ours.SourceDescriptorSnapshot.PathHint });
        using var writer = new SemanticMergePlanWriter(store, session, source.SourceId, b, o, t, include, token);
        var diagnostics = RequireSuccess(await Task.Run(async () => await invocation.Provider.AnalyzeAsync(request, writer,
            invocation.Context with { OperationCancellation = token }).ConfigureAwait(false), token).ConfigureAwait(false));
        var owned = await writer.CompleteAsync().ConfigureAwait(false);
        MergeTreeManifest Unclaimed(MergeTreeManifest tree) => MergeTreeManifest.Create(tree.Files.Where(p => !owned.Contains(p.Key)), tree.Directories);
        var remainingBase = Unclaimed(b); var remainingOurs = Unclaimed(o); var remainingTheirs = Unclaimed(t);
        var proposal = generic.Analyze(source.SourceId, remainingBase, remainingOurs, remainingTheirs);
        HistoryMergeService.ValidateProposal(source.SourceId, remainingBase, remainingOurs, remainingTheirs, proposal);
        var conflicts = proposal.Conflicts.ToBuilder();
        var conflictedPaths = proposal.Conflicts.SelectMany(c => c.Subject.Paths).ToHashSet(StringComparer.Ordinal);
        foreach (var path in remainingOurs.Paths.Concat(proposal.Automatic.Paths).Distinct(StringComparer.Ordinal))
        {
            if (conflictedPaths.Contains(path) || remainingOurs.Files.GetValueOrDefault(path)?.Digest == proposal.Automatic.Files.GetValueOrDefault(path)?.Digest
                && remainingOurs.Directories.Contains(path) == proposal.Automatic.Directories.Contains(path)) continue;
            ImmutableSortedDictionary<string, MergeFileValue> Side(MergeTreeManifest tree)
                => tree.Files.TryGetValue(path, out var file) ? ImmutableSortedDictionary<string, MergeFileValue>.Empty.Add(path, file) : ImmutableSortedDictionary<string, MergeFileValue>.Empty;
            var id = "host-automatic:" + source.SourceId + ":" + path;
            conflicts.Add(new(id, new(source.SourceId, [path]), MergeConflictKind.ModifyModify, id, Side(remainingBase), Side(remainingOurs), Side(proposal.Automatic))
            {
                AutomaticDisplayOnly = true,
                BaseDirectories = remainingBase.Directories.Contains(path) ? [path] : [],
                OursDirectories = remainingOurs.Directories.Contains(path) ? [path] : [],
                TheirsDirectories = proposal.Automatic.Directories.Contains(path) ? [path] : []
            });
        }
        return proposal with { Conflicts = conflicts.ToImmutable(), Automatic = proposal.Automatic with
        { SemanticDiagnostics = diagnostics, RequiredAbsentPaths = views.RequiredAbsentPaths } };
    }

    internal async Task<MergeTreeManifest> BuildAsync(MergeSession session, MergeSessionSource source, MergeTreeManifest automatic,
        string resultRoot, CancellationToken token)
    {
        RequireBinding(session);
        using var invocation = await resolver.AcquireAsync(Binding, token).ConfigureAwait(false);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, invocation.Context.PluginLifetime);
        token = lifetime.Token;
        using var views = new InputViews(store.SessionDirectory(session.Id), source.Plan, source.Base, source.Ours, source.Theirs, token);
        var request = Request(session, source.Plan, source.Base, source.Ours, source.Theirs, views);
        var files = automatic.Files.ToBuilder(); var directories = automatic.Directories.ToHashSet(StringComparer.Ordinal);
        var budget = new SemanticMergeOutputBudget(automatic.Files.Values.Sum(f => f.Length), automatic.Files.Count);
        var diagnostics = source.Automatic.SemanticDiagnostics.ToBuilder();
        var inputTrees = new Dictionary<MergeInputSide, MergeTreeManifest>
        { [MergeInputSide.Base] = source.Base, [MergeInputSide.Ours] = source.Ours, [MergeInputSide.Theirs] = source.Theirs };
        foreach (var group in store.SemanticGroups(session, source.Plan.SourceId))
        {
            token.ThrowIfCancellationRequested();
            var paths = store.SemanticPaths(session, source.Plan.SourceId, group.GroupId);
            using var decisions = new DecisionReader(store, session, source.Plan.SourceId, group.GroupId, token);
            using var output = new SemanticMergeOutputWorkspace(Path.Combine(resultRoot, "semantic-" + Guid.NewGuid().ToString("N")), inputTrees,
                paths.Where(p => p.Input).Select(p => p.Path), paths.Where(p => p.Output).Select(p => p.Path), token, budget);
            diagnostics.AddRange(RequireSuccess(await Task.Run(async () => await invocation.Provider.BuildAsync(new(request, group, decisions), output,
                invocation.Context with { OperationCancellation = token }).ConfigureAwait(false), token).ConfigureAwait(false)));
            if (diagnostics.Count > 64) throw new InvalidDataException("Merge diagnostics exceed the Source budget.");
            var composed = await output.CompleteAsync().ConfigureAwait(false);
            foreach (var file in composed.Files)
                if (!files.TryAdd(file.Key, file.Value)) throw new InvalidDataException("Semantic Merge output overlaps another result.");
            directories.UnionWith(composed.Directories);
        }
        var merged = MergeTreeManifest.Create(files, directories);
        if (merged.Files.Count > Plugin.Runtime.Artifacts.ArtifactTransformLimits.Default.MaximumFiles
            || merged.Files.Values.Sum(f => f.Length) > Plugin.Runtime.Artifacts.ArtifactTransformLimits.Default.MaximumBytes)
            throw new IOException("Semantic Merge result exceeds the Source budget.");
        return merged with { SemanticDiagnostics = diagnostics.ToImmutable(),
            HasCompleteDirectoryInventory = source.Base.HasCompleteDirectoryInventory && source.Ours.HasCompleteDirectoryInventory && source.Theirs.HasCompleteDirectoryInventory,
            RequiredAbsentPaths = source.Automatic.RequiredAbsentPaths
            .Concat(views.RequiredAbsentPaths).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToImmutableArray() };
    }

    private void RequireBinding(MergeSession session)
    {
        if (session.Plan.SemanticProvider != Binding) throw new InvalidOperationException("Semantic Merge provider binding changed; recompute the plan.");
        store.RequireCurrentContract(session, Descriptor);
    }
    private static ImmutableArray<PluginDiagnostic> RequireSuccess(MergeProviderResult result)
    {
        if (result is null || result.Outcome is not (OperationOutcome.Success or OperationOutcome.SuccessWithWarnings))
            throw new HistoryMergeBlockedException(new(MergeDiagnosticCode.PreparationFailed,
                Detail: result is null ? "Merge provider returned no result." : string.Join("; ", result.Diagnostics.Select(d => d.Code))));
        if (result.Diagnostics.Count > 64) throw new InvalidDataException("Too many Merge diagnostics.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result.Diagnostics);
        if (bytes.Length > 65536) throw new InvalidDataException("Merge diagnostics exceed the record budget.");
        return JsonSerializer.Deserialize<PluginDiagnostic[]>(bytes)!.ToImmutableArray();
    }
    private MergeAnalysisRequest Request(MergeSession session, HistoryMergeSourcePlan source,
        MergeTreeManifest b, MergeTreeManifest o, MergeTreeManifest t, InputViews views)
    {
        MergeVersionInput Input(CheckpointSource? identity, MergeTreeManifest tree, IMergeSourceView view)
        {
            if (identity?.VersionId is not { } version || tree.InputRepresentationId is not { } representation)
                throw new InvalidDataException("Semantic Merge requires fixed Version and Representation identities.");
            var boundary = identity.EffectiveSourceBoundary;
            return new(version.ToString(), representation.ToString(), tree.Digest,
                new(boundary.SchemaVersion, (MergeBoundaryScope)boundary.ScopeMode, boundary.ScopeRules,
                    (MergeBoundaryFilter)boundary.FilterMode, boundary.FilterRules, boundary.UseRegex, boundary.Fingerprint)
                    { TypedFilterRules = boundary.Rules }, view);
        }
        return new(session.Id, session.Plan.Revision, source.SourceId.Value, Binding.Kind, Binding.PolicyId, Binding.PolicyVersion,
            Input(source.Base, b, views.Base), Input(source.Ours, o, views.Ours), Input(source.Theirs, t, views.Theirs), MergeLimits.Default);
    }

    private sealed class InputViews : IDisposable
    {
        internal SemanticMergeSourceView Base { get; }
        internal SemanticMergeSourceView Ours { get; }
        internal SemanticMergeSourceView Theirs { get; }
        private readonly Dictionary<string, SemanticMergeCoverage> _proofs = new(StringComparer.Ordinal);
        internal ImmutableArray<string> RequiredAbsentPaths => _proofs.Values.SelectMany(p => p.RequiredAbsentPaths)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToImmutableArray();
        internal InputViews(string root, HistoryMergeSourcePlan plan, MergeTreeManifest b, MergeTreeManifest o, MergeTreeManifest t, CancellationToken token)
        {
            // Absence shortcuts must hold in every input, avoiding a live-target absence
            // precondition for a directory already present on another selected side.
            // Core archive inventories can omit empty or newly created directories; only
            // a verified capture witness may prove their historical absence.
            var inventory = b.HasCompleteDirectoryInventory && o.HasCompleteDirectoryInventory && t.HasCompleteDirectoryInventory
                ? b.Paths.Concat(o.Paths).Concat(t.Paths).ToArray() : null;
            SemanticMergeCoverage? Coverage(CheckpointSource? identity)
            {
                if (identity is null) return null;
                var key = identity.EffectiveSourceBoundaryFingerprint + "\0" + identity.SourceDescriptorSnapshot.PathHint;
                if (!_proofs.TryGetValue(key, out var proof)) _proofs.Add(key, proof = new(identity.EffectiveSourceBoundary, identity.SourceDescriptorSnapshot.PathHint, inventory));
                return proof;
            }
            Base = new(b, root, token, Coverage(plan.Base)); Ours = new(o, root, token, Coverage(plan.Ours)); Theirs = new(t, root, token, Coverage(plan.Theirs));
        }
        public void Dispose() { Base.Dispose(); Ours.Dispose(); Theirs.Dispose(); }
    }

    private sealed class DecisionReader(MergeSessionStore store, MergeSession session, SourceId source, string group, CancellationToken token)
        : IMergeDecisionReader, IDisposable
    {
        private readonly SemanticMergeSourceView _view = new(MergeTreeManifest.Empty, store.SessionDirectory(session.Id), token);
        private bool _disposed;
        private void Check(CancellationToken callToken)
        { ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested(); callToken.ThrowIfCancellationRequested(); }
        public ValueTask<MergeDecisionPage> ReadPageAsync(string? cursor, int count, CancellationToken cancellationToken)
        {
            Check(cancellationToken);
            var rows = store.SemanticDecisionPage(session, source, group, cursor, count);
            var decisions = rows.Select(row =>
            {
                var resolution = row.Resolution ?? throw new InvalidDataException("Merge subject is unresolved.");
                if (resolution.PlanRevision != session.Plan.Revision || resolution.InputSignature != row.Conflict.InputSignature
                    || row.Conflict.SemanticSubject is not { } subject || !MergeSessionStore.SemanticChoiceAllowed(subject, resolution.Choice))
                    throw new InvalidDataException("Merge subject decision is stale or invalid.");
                return new MergeSubjectDecision(subject, MergeSessionStore.SemanticChoice(resolution.Choice),
                    resolution.Choice == MergeResolutionChoice.Manual ? row.Conflict.Id : null);
            }).ToArray();
            return ValueTask.FromResult(new MergeDecisionPage(Array.AsReadOnly(decisions), rows.Count == count ? rows[^1].Conflict.Id : null));
        }
        public ValueTask<Stream> OpenDataReadAsync(string key, CancellationToken cancellationToken)
        { Check(cancellationToken); return OpenAsync(store.ReadSemanticData(session, source, key), cancellationToken); }
        public ValueTask<Stream> OpenManualReadAsync(string contentId, CancellationToken cancellationToken)
        {
            Check(cancellationToken);
            var row = store.GetConflict(session, contentId);
            if (row.Conflict.Subject.SourceId != source || row.Conflict.SemanticSubject?.GroupId != group
                || row.Resolution is not { Choice: MergeResolutionChoice.Manual, Manual: { } file } resolution
                || resolution.PlanRevision != session.Plan.Revision || resolution.InputSignature != row.Conflict.InputSignature)
                throw new UnauthorizedAccessException("Manual Merge content is outside the fixed group.");
            return OpenAsync(file, cancellationToken);
        }
        private ValueTask<Stream> OpenAsync(MergeFileValue value, CancellationToken cancellationToken)
        {
            return _view.OpenVerifiedAsync(value, cancellationToken);
        }
        public void Dispose() { if (_disposed) return; _disposed = true; _view.Dispose(); }
    }
}

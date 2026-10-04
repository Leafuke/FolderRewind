using FolderRewind.History.Application;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal sealed partial class MergeOperationService
{
    private (Guid Session, Guid Plan, long Revision, Dictionary<string, MergeResolution?> Values)? _undo;
    public bool CanUndo => _undo is { } undo && Snapshot.Session is { } s && s.Id == undo.Session
        && s.Plan.Revision == undo.Plan && s.Revision == undo.Revision;
    private Guid? _automaticPlan;
    private IReadOnlyList<MergeConflict> _automatic = [];

    private Dictionary<string, MergeResolution?> ReadPrevious(MergeSession session, IEnumerable<string> ids)
        => ids.Distinct().ToDictionary(id => id, id => Runtime!.MergeSessions.GetConflict(session, id).Resolution);
    private void Remember(MergeSession session, Dictionary<string, MergeResolution?> previous)
    {
        if (previous.Count > 0) _undo = (session.Id, session.Plan.Revision, session.Revision, previous);
    }
    public Task ClearAsync(IReadOnlyList<string> ids) => Run(MergeOperationStage.Saving, _ =>
    {
        var session = RequireSession(); var before = ReadPrevious(session, ids);
        session = Runtime!.MergeSessions.SetResolutions(session, ids.Distinct().ToDictionary(id => id, _ => (MergeResolution?)null));
        Remember(session, before); SetSession(session); Refresh(); return Task.CompletedTask;
    });
    public Task UndoAsync() => Run(MergeOperationStage.Saving, _ =>
    {
        if (!CanUndo || _undo is not { } undo) throw new InvalidOperationException(I18n.GetString("MergeWorkspace_UndoExpired"));
        SetSession(Runtime!.MergeSessions.SetResolutions(RequireSession(), undo.Values)); _undo = null;
        Refresh(); return Task.CompletedTask;
    });

    public IReadOnlyList<MergeConflict> AutomaticChanges(MergeSession session)
    {
        if (_automaticPlan == session.Plan.Revision) return _automatic;
        var rows = new List<MergeConflict>();
        foreach (var stored in Runtime!.MergeSessions.Sources(session))
        {
            var source = stored;
            // Older reuse sessions did not persist their target comparison tree.
            if (source.Plan.Action == HistoryMergeSourceAction.Reuse && source.Ours.Files.IsEmpty && source.Plan.Ours is not null)
                source = source with { Ours = NativeHistoryApplicationService.MaterializeMergeComparisonAsync(Runtime, _restore!, session,
                    source.Plan.Ours, System.Threading.CancellationToken.None).GetAwaiter().GetResult() };
            var conflicts = Core.AllConflicts(session, source.Plan.SourceId).Select(c => c.Conflict).ToArray();
            if (conflicts.Any(c => c.Subject.Paths.IsEmpty)) continue;
            var claimed = conflicts.SelectMany(c => c.Subject.Paths).ToHashSet(StringComparer.Ordinal);
            foreach (var path in source.Automatic.Files.Keys.Concat(source.Ours.Files.Keys).Distinct(StringComparer.Ordinal))
            {
                if (claimed.Contains(path)) continue;
                var before = source.Ours.Files.GetValueOrDefault(path); var after = source.Automatic.Files.GetValueOrDefault(path);
                if (before?.Digest == after?.Digest && before?.Length == after?.Length) continue;
                ImmutableSortedDictionary<string, MergeFileValue> Side(MergeTreeManifest tree)
                    => tree.Files.TryGetValue(path, out var file) ? ImmutableSortedDictionary<string, MergeFileValue>.Empty.Add(path, file)
                        : ImmutableSortedDictionary<string, MergeFileValue>.Empty;
                var id = "auto:" + source.Plan.SourceId + ":" + path;
                rows.Add(new(id, new(source.Plan.SourceId, [path]), MergeConflictKind.ModifyModify, id,
                    Side(source.Base), Side(source.Ours), Side(source.Automatic)));
            }
        }
        _automatic = rows.OrderBy(c => c.Subject.Paths[0], StringComparer.Ordinal).ToArray(); _automaticPlan = session.Plan.Revision;
        return _automatic;
    }

    internal sealed record ChangePage(IReadOnlyList<(MergeConflict Conflict, MergeResolution? Resolution, bool Automatic)> Rows,
        int Matching, int Total, int Unresolved);
    public ChangePage QueryChanges(MergeSession session, string search, string filter, int offset, int count)
    {
        var conflicts = Runtime!.MergeSessions.ConflictIndex(session);
        var automatic = AutomaticChanges(session).ToDictionary(c => c.Id);
        var index = conflicts.Select(c => (c.Id, c.Path, c.Resolved, Automatic: false))
            .Concat(automatic.Values.Select(c => (c.Id, Path: c.Subject.Paths[0], Resolved: true, Automatic: true))).ToArray();
        var filtered = index.Where(c => c.Path.Contains(search, StringComparison.OrdinalIgnoreCase)
            && (filter == "All" || filter == "Unresolved" && !c.Resolved || filter == "Resolved" && c.Resolved && !c.Automatic
                || filter == "Automatic" && c.Automatic)).OrderBy(c => c.Path, StringComparer.Ordinal).ThenBy(c => c.Id, StringComparer.Ordinal).ToArray();
        var page = filtered.Skip(offset).Take(count).ToArray();
        var details = Runtime.MergeSessions.GetConflicts(session, page.Where(c => !c.Automatic).Select(c => c.Id));
        return new(page.Select(c => c.Automatic ? (automatic[c.Id], (MergeResolution?)null, true)
            : (details[c.Id].Conflict, details[c.Id].Resolution, false)).ToArray(), filtered.Length, index.Length, conflicts.Count(c => !c.Resolved));
    }
}

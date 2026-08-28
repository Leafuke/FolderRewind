using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public sealed record HistoryAnnotationProjectionResult(
    HistoryAnnotationTarget Target,
    bool IsPinned,
    bool IsSuppressed,
    bool IsRunImportant,
    string? EffectiveComment,
    ImmutableArray<HistoryAnnotationUpdate> Comments,
    ImmutableDictionary<HistoryAnnotationKind, ImmutableArray<HistoryAnnotationUpdate>> Tips);

public static class HistoryAnnotationProjection
{
    public static HistoryAnnotationProjectionResult Project(
        HistoryAnnotationTarget target,
        IEnumerable<HistoryAnnotationUpdate> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        var all = updates.Where(update => update.Target == target).ToImmutableArray();
        var tips = Enum.GetValues<HistoryAnnotationKind>()
            .ToImmutableDictionary(
                kind => kind,
                kind => FindTips(all.Where(update => update.AnnotationKind == kind)));
        var comments = all
            .Where(update => update.AnnotationKind == HistoryAnnotationKind.Comment)
            .OrderBy(update => update.CreatedAtUtc)
            .ThenBy(update => update.UpdateId.ToString(), StringComparer.Ordinal)
            .ToImmutableArray();
        string? effectiveComment = comments.LastOrDefault()?.Value;
        if (string.IsNullOrEmpty(effectiveComment)) effectiveComment = null;
        return new HistoryAnnotationProjectionResult(
            target,
            AnyTrue(tips[HistoryAnnotationKind.Pin]),
            AllTrue(tips[HistoryAnnotationKind.Suppression]),
            AnyTrue(tips[HistoryAnnotationKind.RunImportant]),
            effectiveComment,
            comments,
            tips);
    }

    public static ImmutableArray<HistoryAnnotationUpdate> FindTips(
        IEnumerable<HistoryAnnotationUpdate> updates)
    {
        var all = updates.ToImmutableArray();
        var parentIds = all.SelectMany(update => update.ParentUpdateIds).ToHashSet();
        return all.Where(update => !parentIds.Contains(update.UpdateId))
            .OrderBy(update => update.CreatedAtUtc)
            .ThenBy(update => update.UpdateId.ToString(), StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static bool AnyTrue(IEnumerable<HistoryAnnotationUpdate> tips)
        => tips.Any(update => string.Equals(update.Value, "true", StringComparison.OrdinalIgnoreCase));

    private static bool AllTrue(IEnumerable<HistoryAnnotationUpdate> tips)
    {
        var values = tips.ToArray();
        return values.Length > 0
            && values.All(update => string.Equals(update.Value, "true", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class HistoryAnnotationCommandException(string message) : Exception(message);

public sealed class HistoryAnnotationService
{
    private readonly HistoryRuntime _runtime;
    private readonly HistoryPackCodec _codec;

    public HistoryAnnotationService(HistoryRuntime runtime, HistoryPackCodec? codec = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _codec = codec ?? new HistoryPackCodec();
    }

    public Task<HistoryAnnotationUpdate> SetCommentAsync(
        HistoryAnnotationTarget target,
        string? comment,
        CancellationToken cancellationToken = default)
        => AppendAsync(target, HistoryAnnotationKind.Comment, comment ?? string.Empty, cancellationToken);

    public Task<HistoryAnnotationUpdate> SetPinAsync(
        HistoryAnnotationTarget target,
        bool pinned,
        CancellationToken cancellationToken = default)
    {
        if (target.Kind == HistoryAnnotationTargetKind.Run)
        {
            throw new HistoryAnnotationCommandException(
                "BackupRun uses RunImportant; retention Pin can only target a Version or Checkpoint.");
        }
        return AppendAsync(target, HistoryAnnotationKind.Pin, BooleanValue(pinned), cancellationToken);
    }

    public Task<HistoryAnnotationUpdate> SetSuppressionAsync(
        HistoryAnnotationTarget target,
        bool suppressed,
        CancellationToken cancellationToken = default)
        => AppendAsync(target, HistoryAnnotationKind.Suppression, BooleanValue(suppressed), cancellationToken);

    public Task<HistoryAnnotationUpdate> SetRunImportantAsync(
        HistoryAnnotationTarget target,
        bool important,
        CancellationToken cancellationToken = default)
    {
        if (target.Kind != HistoryAnnotationTargetKind.Run)
        {
            throw new HistoryAnnotationCommandException("RunImportant annotations can only target BackupRun facts.");
        }
        return AppendAsync(target, HistoryAnnotationKind.RunImportant, BooleanValue(important), cancellationToken);
    }

    private async Task<HistoryAnnotationUpdate> AppendAsync(
        HistoryAnnotationTarget target,
        HistoryAnnotationKind kind,
        string value,
        CancellationToken cancellationToken)
    {
        await using var lease = await _runtime.MutationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await _runtime.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
        await RequireTargetAsync(target, cancellationToken).ConfigureAwait(false);
        var current = await _runtime.Query.GetAnnotationUpdatesAsync(target, kind, cancellationToken).ConfigureAwait(false);
        var parents = HistoryAnnotationProjection.FindTips(current).Select(update => update.UpdateId);
        var update = new HistoryAnnotationUpdate(
            AnnotationUpdateId.New(),
            target,
            kind,
            parents,
            value,
            DateTimeOffset.UtcNow);
        await HistoryCommandCommitter.CommitInsideGateAsync(
            _runtime, _codec, [update], null, null, cancellationToken).ConfigureAwait(false);
        return update;
    }

    private async Task RequireTargetAsync(
        HistoryAnnotationTarget target,
        CancellationToken cancellationToken)
    {
        bool exists = target.Kind switch
        {
            HistoryAnnotationTargetKind.Version =>
                await _runtime.Query.GetVersionAsync(new VersionId(target.TargetId), cancellationToken).ConfigureAwait(false) is not null,
            HistoryAnnotationTargetKind.Checkpoint =>
                await _runtime.Query.GetCheckpointAsync(new CheckpointId(target.TargetId), cancellationToken).ConfigureAwait(false) is not null,
            HistoryAnnotationTargetKind.Run =>
                await _runtime.Query.GetRunAsync(new RunId(target.TargetId), cancellationToken).ConfigureAwait(false) is not null,
            _ => false
        };
        if (!exists) throw new HistoryAnnotationCommandException("Annotation target does not exist.");
    }

    private static string BooleanValue(bool value) => value ? "true" : "false";
}

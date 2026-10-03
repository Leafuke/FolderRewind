using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Models;

public enum OnboardingCheckState { NotApplicable, Unknown, Checking, NeedsInput, Ready, Blocked, Failed }
public sealed record OnboardingCheck(string Code, OnboardingCheckState State, string Message,
    DateTimeOffset CheckedAtUtc, IReadOnlyList<string> AllowedActions);

// 安装和连接观察不是持久历史事实；不得从动作成功推断 Ready。
public sealed record OnboardingPreparation(IReadOnlyList<string> CompletedActions,
    IReadOnlyList<string> PendingActions, IReadOnlyList<OnboardingCheck> Checks);

public enum SetupSourceOutcome { Captured, NoChange, Failed, Canceled, NotExecuted, NeedsRecovery }
public sealed record SetupSourceResult(string SourceId, string Name, SetupSourceOutcome Outcome,
    string? VersionId, bool IsPartial, IReadOnlyList<string> Diagnostics);
public sealed record SetupBackupResult(IReadOnlyList<SetupSourceResult> Sources, string? CheckpointId,
    string? RunId, bool RecoveryRequired, DateTimeOffset ObservedAtUtc)
{
    public int CreatedVersionCount => Sources.Count(s => s.Outcome == SetupSourceOutcome.Captured && s.VersionId is not null);
    public int UnchangedCount => Sources.Count(s => s.Outcome == SetupSourceOutcome.NoChange);
    public int FailedCount => Sources.Count(s => s.Outcome == SetupSourceOutcome.Failed);
    public int CanceledCount => Sources.Count(s => s.Outcome is SetupSourceOutcome.Canceled or SetupSourceOutcome.NotExecuted);
    public bool HasWarnings => RecoveryRequired || Sources.Any(s => s.IsPartial || s.Diagnostics.Count != 0);
    public SetupBackupResult MergeRetry(SetupBackupResult retry)
        => retry with
        {
            Sources = Sources.Where(s => retry.Sources.All(r => r.SourceId != s.SourceId)).Concat(retry.Sources).ToArray(),
            RecoveryRequired = RecoveryRequired || retry.RecoveryRequired
        };
}

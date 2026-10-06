using FolderRewind.History.LocalState;
using FolderRewind.Services;

namespace FolderRewind.ViewModels;

public sealed partial class MergePageViewModel
{
    private bool _choosingSource;
    public bool ShowSetup => !HasSession || _choosingSource;
    public bool ShowResult => !ShowSetup && ShowCompletion;
    public bool ShowFooter => !ShowSetup;
    public bool ShowUndo => ShowWorkspace && CanUndo;
    public bool ShowSetupStop => ShowSetup && CanStop;
    public bool ShowOperationNotice => !IsBusy && (State.Notice is not null
        || State.Result is not null && !ShowCompletion
        || State.Session?.State is MergeSessionState.Preparing or MergeSessionState.Applying or MergeSessionState.Stale or MergeSessionState.Abandoned);
    public bool ShowDetails => SelectedChange is not null;
    public bool ShowEmptyDetails => !ShowDetails;
    public bool ShowEmptyList => Changes.Count == 0;
    public bool ShowBatchActions => CheckedCount > 0;
    public bool ShowFileActions => CheckedCount == 0 && SelectedChange is { IsAutomatic: false };
    public bool ShowSaveStatus => HasSession && !ShowSetup && !ShowCompletion;
    public bool HasSourceWarning => !string.IsNullOrEmpty(SourceAvailability);
    public bool HasPreviewNotice => !string.IsNullOrEmpty(PreviewNotice);
    public bool HasRecomputeSummary => !string.IsNullOrEmpty(RecomputeSummary);
    public bool HasResultDiagnostic => !string.IsNullOrEmpty(ResultDiagnostic);
    public bool CanStartNew => CanChooseSession && !CanRecover;
    public string TargetBranchName => _targetName;
    public string StageLabel => I18n.GetString(ShowSetup ? "MergeWorkspace_StepSetup"
        : ShowCompletion ? "MergeWorkspace_StepDone" : ShowReview ? "MergeWorkspace_StepReview" : "MergeWorkspace_StepResolve");
    public string FooterHint => CanRecover ? Status : ShowReview ? I18n.GetString("MergeWorkspace_ReviewHint")
        : ShowCompletion ? I18n.GetString("MergeWorkspace_CompleteHint")
        : CanGenerate ? I18n.GetString("MergeWorkspace_ReadyHint") : CountLabel;
    public string EmptyListTitle => I18n.GetString(_total == 0 ? "MergeWorkspace_NoChanges" : "MergeWorkspace_NoMatches");
    public string EmptyDetailTitle => I18n.GetString(_total == 0 ? "MergeWorkspace_NoChanges" : "MergeWorkspace_SelectFile");
    public string EmptyDetailHint => State.Session?.State == MergeSessionState.Ready
        ? I18n.GetString("MergeWorkspace_ReadyHint") : I18n.GetString("MergeWorkspace_SelectFileHint");
    public void BeginNewMerge() { if (CanStartNew) { _choosingSource = true; Notify(); } }
    public void CancelNewMerge() { _choosingSource = false; Notify(); }
}

using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.History.Storage;
using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed record MergeReviewLine(string Path, string Kind);
public sealed partial class MergePageViewModel
{
    public bool ShowCompletion => State.Result?.TargetCommitted == true || State.Session?.State == MergeSessionState.Committed;
    public bool ShowReview => !ShowSetup && !ShowCompletion && Operations?.Review is not null;
    public bool ShowWorkspace => !ShowSetup && !ShowCompletion && !ShowReview && HasSession;
    public bool CanApplyReview => IsIdle && ShowReview && CanGenerate;
    public bool CanShowResult => IsIdle && ShowCompletion;
    public bool HasBranchChanges => Operations?.Review?.BranchChanges.Length > 0;
    public bool HasWorkingChanges => Operations?.Review?.WorkingChanges.Length > 0;
    public string ResultDiagnostic => State.Result?.Diagnostic ?? "";
    public string ProtectionText => I18n.GetString(Operations?.Review?.NeedsProtection == true ? "MergeWorkspace_ProtectionRequired" : "MergeWorkspace_ProtectionNotRequired");
    public string ReviewSummary => Operations?.Review is { } r
        ? string.Format(I18n.GetString("MergeWorkspace_ReviewSummary"), r.BranchChanges.Length, r.WorkingChanges.Length) : "";
    public IEnumerable<MergeReviewLine> BranchChanges => Operations?.Review?.BranchChanges.Select(c => new MergeReviewLine(c.Path, I18n.GetString("MergeWorkspace_Change_" + c.Kind))) ?? [];
    public IEnumerable<MergeReviewLine> WorkingChanges => Operations?.Review?.WorkingChanges.Select(c => new MergeReviewLine(c.Path, I18n.GetString("MergeWorkspace_Change_" + c.Kind))) ?? [];
    public Task GenerateAsync() => Operations is null ? Task.CompletedTask : ExecuteAsync(Operations.GenerateReviewAsync);
    public Task ApplyReviewAsync() => Operations is null ? Task.CompletedTask : ExecuteAsync(Operations.ApplyReviewedAsync);
    public void EditDecisions() { Operations?.DismissReview(); Notify(); }
    public async Task ShowResultAsync(bool safety)
    {
        if (Navigation is not { } navigation || Operations?.Runtime is not { } runtime || State.Session is not { } session) return;
        CheckpointId? checkpoint = null;
        if (!safety && session.IntendedPackId is { } packId)
        {
            checkpoint = await Task.Run(() =>
            {
                var codec = new HistoryPackCodec();
                var pack = codec.Decode(File.ReadAllBytes(runtime.Repository.Paths.GetPackPath(packId))).Pack;
                return pack.Objects.Select(codec.DeserializeKnown).OfType<BranchUpdate>().Single(u => u.Reason == BranchUpdateReason.Merged).TargetCheckpointId;
            });
        }
        NavigationService.NavigateTo("History", new HistoryReturnContext(navigation.ConfigId, navigation.FolderId,
            session.Plan.Ours.BranchId, "", HistoryPresentationMode.Advanced, FocusCheckpoint: checkpoint, OpenSafetySnapshots: safety));
    }
}

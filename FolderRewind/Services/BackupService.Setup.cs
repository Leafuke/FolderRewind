using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.Models;
using FolderRewind.History.Domain;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Services;

public static partial class BackupService
{
    // 与既有入口共用事务和全局任务。页面销毁不会取消已开始的备份。
    public static async Task<SetupBackupResult> BackupForSetupAsync(BackupConfig config, System.Collections.Generic.IReadOnlyList<string>? sourceIds = null)
    {
        var requested = config.SourceFolders.Where(s => sourceIds is null || sourceIds.Contains(s.Id)).ToArray();
        var result = await ExecuteBackupTransactionAsync(config, requested,
            BackupInvocationOptions.ForManual(""), cancellationToken: CancellationToken.None).ConfigureAwait(false);
        var run = result.CommittedBatch?.Run;
        var versions = result.CommittedBatch?.NewVersions;
        var sources = requested.Select(folder =>
        {
            var sourceId = new SourceId(Guid.Parse(folder.Id));
            var executed = result.SourceOutcomes.FirstOrDefault(s => s.FolderId == sourceId.Value);
            var fact = run?.SourceResults.FirstOrDefault(s => s.SourceId == sourceId);
            var version = versions?.FirstOrDefault(v => v.SourceId == sourceId);
            // 未提交的归档不能算新版本，沿用旧版本不能冒充本次捕获。
            var outcome = fact?.Outcome switch
            {
                BackupRunSourceOutcome.Captured when version is not null => SetupSourceOutcome.Captured,
                BackupRunSourceOutcome.Reused => SetupSourceOutcome.NoChange,
                BackupRunSourceOutcome.Failed or BackupRunSourceOutcome.Unavailable => SetupSourceOutcome.Failed,
                _ when result.HistoryRecoveryRequired && executed?.CreatedNewArchive == true => SetupSourceOutcome.NeedsRecovery,
                _ when executed?.OperationOutcome == OperationOutcome.Canceled => SetupSourceOutcome.Canceled,
                _ when executed is not null => SetupSourceOutcome.Failed,
                _ => SetupSourceOutcome.NotExecuted
            };
            var diagnostics = (fact?.Diagnostics.Select(d => d.Code) ?? [])
                .Concat(string.IsNullOrWhiteSpace(executed?.ErrorMessage) ? [] : new[] { executed.ErrorMessage })
                .Concat(executed?.OperationOutcome == OperationOutcome.SuccessWithWarnings ? new[] { I18n.GetString("Onboarding_ConsistencyWarning") } : [])
                .ToArray();
            return new SetupSourceResult(folder.Id, folder.DisplayName, outcome, fact?.VersionId?.ToString(),
                version?.CaptureScope == CaptureScope.PartialSource, diagnostics);
        }).ToArray();
        return new(sources, sources.Length == 1 ? run?.SourceResults.SingleOrDefault()?.CheckpointId?.ToString() : null, run?.RunId.ToString(),
            result.HistoryRecoveryRequired, DateTimeOffset.UtcNow);
    }
}

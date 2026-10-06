using FolderRewind.History.Application;
using FolderRewind.History.Retention;
using FolderRewind.History.Representation;
using FolderRewind.History.Storage;
using FolderRewind.Models;
using System;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal static partial class NativeHistoryApplicationService
{
    public static HistoryCleanupReport? ReadCleanupReport(string configId)
        => new HistoryCleanupReportStore(HistoryRepositoryPaths.ForConfigDirectory(ConfigService.ConfigDirectory, new(configId)).LocalStateRoot).Read();

    public static async Task<HistoryCleanupReport?> RunManualCleanupAsync(string configId, string expectedRevision,
        HistoryRetentionBenefitPolicy policy, IProgress<HistoryChainRewriteProgress>? progress, CancellationToken token)
    {
        progress?.Report(new("waiting", 0, 0));
        await using var operation = await NativeHistoryConfigurationOperationGate.EnterAsync(configId, token).ConfigureAwait(false);
        var config = await UiDispatcherService.RunOnUiAsync(() => Task.FromResult(
            ConfigService.CurrentConfig.BackupConfigs.FirstOrDefault(c => c.Id == configId))).ConfigureAwait(false);
        if (config is null || config.ConfigRevision != expectedRevision)
            throw new InvalidOperationException(I18n.GetString("Retention_ConfigChanged"));
        ConfigMutationProtection.RequireWritable(config);
        progress?.Report(new("planning", 0, 0));
        return await RunCleanupInsideOperationAsync(config, policy, false, progress, token, operation).ConfigureAwait(false);
    }

    private static async Task<HistoryCleanupReport?> RunCleanupInsideOperationAsync(BackupConfig config,
        HistoryRetentionBenefitPolicy policy, bool automatic, IProgress<HistoryChainRewriteProgress>? progress, CancellationToken token,
        NativeHistoryConfigurationOperationGate.Lease? recoveryOperation = null)
    {
        if (config.Archive.KeepCount <= 0) return null;
        try
        {
            if (recoveryOperation is not null)
            {
                var recovery = await CreateRestoreServiceAsync(config, token).ConfigureAwait(false);
                await recovery.RecoverInsideConfigurationAsync(recoveryOperation, token).ConfigureAwait(false);
            }
            var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token).ConfigureAwait(false);
            var archive = new SevenZipHistoryArchiveBackend(config);
            var engine = RewriteEngine(archive);
            async Task<IRepresentationEnvironment> Environment(CancellationToken ct) => await BuildEnvironmentAsync(runtime, ct).ConfigureAwait(false);
            var planner = new HistoryRetentionPlanner(runtime, engine, Environment, new FileSystemHistoryLocalPayloadStore());
            var signature = JsonSerializer.Serialize(config.Archive, AppJsonContext.Default.ArchiveSettings) + "|" + config.DestinationPath + "|" + config.IsEncrypted;
            // Cache identity includes the backend binary and encrypted credential store, never plaintext credentials.
            try
            {
                var executable = SevenZipExecutableLocator.Resolve(ConfigService.CurrentConfig.GlobalSettings.SevenZipPath);
                signature += executable is null ? Guid.NewGuid().ToString() : Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(executable, token).ConfigureAwait(false)));
                if (config.IsEncrypted)
                    signature += string.IsNullOrEmpty(EncryptionService.RetrievePassword(config.Id)) ? Guid.NewGuid().ToString()
                        : Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(ConfigService.ConfigDirectory, "encrypted_passwords.dat"), token).ConfigureAwait(false)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { signature += Guid.NewGuid().ToString(); }
            var result = await new HistoryCleanupCoordinator(runtime, engine, archive, planner, GetRewriteBackupRoot(config))
                .ExecuteAsync(config.Archive.KeepCount, config.Archive.MaxSmartBackupsPerFull, policy, automatic, signature, progress, token).ConfigureAwait(false);
            if (result is not null)
            {
                LogService.LogInfo($"[Retention] {result.Status}; deleted {result.Sources.Sum(s => s.DeletedArchives)} archives; "
                    + $"net released {result.Sources.Sum(s => s.ReclaimedBytes - s.CreatedBytes)} bytes.", nameof(NativeHistoryApplicationService));
                foreach (var issue in result.Issues) LogService.LogWarning($"[Retention] {issue.Code}: {issue.Detail}", nameof(NativeHistoryApplicationService));
            }
            return result;
        }
        catch (Exception ex)
        {
            var report = new HistoryCleanupReport
            {
                KeepCount = config.Archive.KeepCount, Policy = policy, Trigger = automatic ? "Automatic" : "Manual",
                Status = ex is OperationCanceledException && token.IsCancellationRequested ? "Canceled" : "Incomplete",
                FinishedAtUtc = DateTimeOffset.UtcNow
            };
            report.Issues.Add(new("GlobalStateUnavailable", EncryptionService.SanitizeForLog(ex.Message, config.Id)));
            try { new HistoryCleanupReportStore(HistoryRepositoryPaths.ForConfigDirectory(ConfigService.ConfigDirectory, new(config.Id)).LocalStateRoot).Save(report); }
            catch (Exception write) when (write is IOException or UnauthorizedAccessException)
            { report.Issues.Add(new("ReportWriteFailed", write.Message)); }
            return report;
        }
    }
}

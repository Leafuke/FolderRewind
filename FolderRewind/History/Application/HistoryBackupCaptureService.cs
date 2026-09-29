using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

/// <summary>
/// 一次图谱提交通路的结果。主备份已经落盘，本结果只用于日志与诊断 ——
/// 调用方不得因为它未提交而回滚已经完成的主备份。
/// </summary>
public sealed record HistoryBackupCaptureOutcome(
    bool Committed,
    bool Blocked,
    string? Diagnostic,
    bool RecoveryRequired,
    IReadOnlyList<SourceCaptureResult> Captures)
{
    public HistoryCommitBatch? CommittedBatch { get; init; }

    public static HistoryBackupCaptureOutcome BlockedWith(string diagnostic) =>
        new(Committed: false, Blocked: true, Diagnostic: diagnostic, RecoveryRequired: false, Captures: []);
}

/// <summary>
/// 图谱自己的备份提交通路，与 1.8 既有的 <c>HistoryService</c> / <c>HistoryItem</c> 写入并行、互不知情。
/// <para>
/// 顺序：配置操作门 → 历史就绪 → 为全部来源解析并冻结权威边界 → 边界漂移预检 → 逐个来源捕获
/// → 原子提交为一条不可变 Commit Pack。Commit Pack 落盘即持久事实，
/// 其后的失败只影响本机派生态，不会回退历史。
/// </para>
/// </summary>
public static class HistoryBackupCaptureService
{
    /// <summary>
    /// 为本次备份涉及的来源捕获并提交图谱事实。
    /// </summary>
    /// <remarks>
    /// 预检与捕获阶段抛出的异常一律向上传播：调用方是主备份成功后的旁路，
    /// 由它决定只记日志还是提示用户 —— 这里吞掉异常会让「图谱没写进去」变得不可见。
    /// 唯一内部消化的是「已经形成持久 Pack、但本机派生态需要恢复」这一种情况。
    /// </remarks>
    public static async Task<HistoryBackupCaptureOutcome> CaptureAndCommitAsync(
        BackupConfig config,
        IReadOnlyList<ManagedFolder> requestedFolders,
        BackupInvocationOptions? invocationOptions = null,
        string? comment = null,
        HistoryCommitIntent intent = HistoryCommitIntent.AdvanceBranch,
        HistorySafetySnapshotIntent? safetySnapshotIntent = null,
        CancellationToken cancellationToken = default)
    {
        if (config is null || requestedFolders is null || requestedFolders.Count == 0)
        {
            return HistoryBackupCaptureOutcome.BlockedWith(
                "A backup configuration and at least one requested source are required.");
        }

        invocationOptions ??= BackupInvocationOptions.Default;
        await using var operationLease = await NativeHistoryConfigurationOperationGate
            .EnterAsync(config.Id, cancellationToken).ConfigureAwait(false);
        _ = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);

        // 1. 为全部配置来源解析并冻结权威边界。
        // 即使本次只备份部分来源，也必须掌握全部来源在本次事务中的权威边界，
        // 否则配置级快照无法建立，提交后其它来源的边界就会以旧值留在历史里。
        var resolutions = new Dictionary<string, HistorySourceBoundaryResolution>(StringComparer.Ordinal);
        foreach (var folder in config.SourceFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (folder is null) continue;

            var resolution = HistorySourceBoundaryResolver.Resolve(config, folder);
            resolutions[folder.Id] = resolution;
            if (resolution.IsBlocked)
            {
                var message =
                    $"来源「{folder.DisplayName}」的有效备份边界无法解析，配置级历史快照无法建立，本次图谱提交中止。" +
                    $"({resolution.BlockedErrorCode} {resolution.BlockedErrorMessage})";
                LogService.LogWarning(message, nameof(HistoryBackupCaptureService));
                return HistoryBackupCaptureOutcome.BlockedWith(message);
            }
        }

        var requested = new List<(ManagedFolder Folder, HistorySourceBoundaryResolution Resolution)>();
        foreach (var folder in requestedFolders)
        {
            if (folder is null) continue;
            if (!resolutions.TryGetValue(folder.Id, out var resolution))
            {
                // 请求的来源不属于本配置是调用方的编程错误：静默跳过会让这次备份在图谱里凭空少一条线。
                throw new InvalidOperationException(
                    $"Managed folder '{folder.DisplayName}' does not belong to configuration '{config.Name}'.");
            }
            if (!Guid.TryParse(resolution.EffectiveFolder.Id, out var parsed) || parsed == Guid.Empty)
            {
                return HistoryBackupCaptureOutcome.BlockedWith(
                    $"来源「{folder.DisplayName}」缺少稳定的来源标识，无法写入图谱历史。");
            }

            requested.Add((folder, resolution));
        }

        if (requested.Count == 0)
        {
            return HistoryBackupCaptureOutcome.BlockedWith("No usable source was requested for capture.");
        }

        // 2. 冻结配置级权威快照
        var historySnapshot = new HistoryConfigSnapshot(
            new HistoryConfigId(config.Id),
            config.SourceFolders
                .Where(folder => folder is not null && resolutions.ContainsKey(folder.Id))
                .Select(folder =>
                {
                    var resolution = resolutions[folder.Id];
                    return new HistoryConfigSourceSnapshot(
                        new SourceId(Guid.Parse(resolution.EffectiveFolder.Id)),
                        new SourceDescriptorSnapshot(
                            resolution.EffectiveFolder.DisplayName,
                            resolution.EffectiveFolder.Path),
                        resolution.Boundary);
                }));

        // 3. 边界漂移预检：未参与本次捕获、但边界已经变化的来源必须先做一次完整配置备份重建基线，
        //    否则它的历史会带着过期边界继续往后走。
        var plannedSourceIds = requested
            .Select(item => new SourceId(Guid.Parse(item.Resolution.EffectiveFolder.Id)))
            .ToArray();
        var requiredRecaptures = await NativeHistoryCoreGateway.FindRequiredBoundaryRecapturesAsync(
            historySnapshot,
            plannedSourceIds,
            cancellationToken).ConfigureAwait(false);
        if (requiredRecaptures.Count > 0)
        {
            var drift = requiredRecaptures[0];
            var driftFolder = config.SourceFolders.FirstOrDefault(
                folder => folder is not null && Guid.TryParse(folder.Id, out var id) && new SourceId(id) == drift.SourceId);
            var driftName = driftFolder?.DisplayName ?? drift.SourceId.ToString();
            var message =
                $"配置中的「{driftName}」有效备份边界已变化，需要先执行一次完整配置备份重建可靠基线。" +
                $"(SourceId={drift.SourceId}, previous={Shorten(drift.PreviousBoundaryFingerprint)}, " +
                $"current={Shorten(drift.CurrentBoundaryFingerprint)})";
            LogService.LogWarning(message, nameof(HistoryBackupCaptureService));
            return HistoryBackupCaptureOutcome.BlockedWith(message);
        }

        var startedAtUtc = DateTimeOffset.UtcNow;
        var captures = new List<SourceCaptureResult>(requested.Count);
        HistoryCommitBatch committedBatch;
        try
        {
            // 4. 逐个来源捕获。每个来源自己扫盘、自己建归档、自己校验。
            foreach (var (folder, resolution) in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var payloadDirectory = ResolvePayloadDirectory(resolution.EffectiveConfig, resolution.EffectiveFolder);
                var sourceId = new SourceId(Guid.Parse(resolution.EffectiveFolder.Id));
                var baseline = await NativeHistoryCoreGateway.LoadCaptureBaselineAsync(
                    resolution.EffectiveConfig.Id,
                    sourceId,
                    cancellationToken).ConfigureAwait(false);
                var capture = await BackupService.CaptureHistorySourceAsync(
                    resolution.EffectiveConfig,
                    resolution.EffectiveFolder,
                    payloadDirectory,
                    baseline,
                    cancellationToken).ConfigureAwait(false);
                captures.Add(capture.WithEffectiveSourceBoundary(resolution.Boundary));
            }

            // 5. 原子提交
            committedBatch = await NativeHistoryCoreGateway.CommitBackupAsync(
                historySnapshot,
                captures,
                MapInvocationKind(invocationOptions.Source),
                startedAtUtc,
                comment,
                cancellationToken,
                intent,
                safetySnapshotIntent).ConfigureAwait(false);
        }
        catch (HistoryCommitRecoveryRequiredException ex)
        {
            // Commit Pack 已经是持久事实，不能当作失败去清理归档；只报告本机派生态需要恢复。
            LogService.LogWarning(
                $"图谱 Commit Pack '{ex.CommittedPackId}' 已落盘，但本机派生状态需要恢复：{ex.Message}",
                nameof(HistoryBackupCaptureService));
            return new HistoryBackupCaptureOutcome(
                Committed: true,
                Blocked: false,
                Diagnostic: ex.Message,
                RecoveryRequired: true,
                Captures: captures);
        }
        catch
        {
            // 提交前的失败：本次捕获产生的归档还没进入历史，删掉它们，避免留下无引用的大文件。
            await CleanupUncommittedCapturesAsync(captures).ConfigureAwait(false);
            throw;
        }

        return new HistoryBackupCaptureOutcome(
            Committed: true,
            Blocked: false,
            Diagnostic: null,
            RecoveryRequired: false,
            Captures: captures)
        {
            CommittedBatch = committedBatch
        };
    }

    /// <summary>
    /// 图谱载荷放在主备份归档所在的同一个来源目录下的 <c>history/</c> 子目录里。
    /// 目录不存在时先建出来，7z 不会替调用方建。
    /// </summary>
    private static string ResolvePayloadDirectory(BackupConfig config, ManagedFolder folder)
    {
        if (!BackupStoragePathService.TryResolveBackupStoragePaths(
                config.DestinationPath,
                folder.DisplayName,
                folder.Path,
                out _,
                out var backupSubDir,
                out _))
        {
            throw new InvalidOperationException(
                $"Backup destination for '{folder.DisplayName}' could not be resolved.");
        }

        var payloadDirectory = BackupService.ResolveHistoryPayloadDirectory(backupSubDir);
        Directory.CreateDirectory(payloadDirectory);
        return payloadDirectory;
    }

    private static async Task CleanupUncommittedCapturesAsync(
        IEnumerable<SourceCaptureResult> captures)
    {
        foreach (var cleanup in captures
                     .Select(item => item.CleanupHandle)
                     .Where(item => item is not null))
        {
            try
            {
                await cleanup!.CleanupAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogService.LogWarning(
                    $"清理未提交的图谱归档失败：{ex.Message}",
                    nameof(HistoryBackupCaptureService));
            }
        }
    }

    private static BackupInvocationKind MapInvocationKind(BackupInvocationSource source) => source switch
    {
        BackupInvocationSource.Automatic => BackupInvocationKind.Automatic,
        BackupInvocationSource.Remote => BackupInvocationKind.Remote,
        BackupInvocationSource.PluginHotkey => BackupInvocationKind.PluginHotkey,
        BackupInvocationSource.Internal => BackupInvocationKind.Internal,
        _ => BackupInvocationKind.Manual
    };

    private static string Shorten(string fingerprint)
        => fingerprint.Length <= 10 ? fingerprint : fingerprint[..10];
}

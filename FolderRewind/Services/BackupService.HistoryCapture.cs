using FolderRewind.History.Application;
using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public static partial class BackupService
    {
        /// <summary>
        /// 图谱载荷归档在备份子目录下的位置。主流程会把备份子目录里最新的 <c>*.7z</c> 当作原地覆写
        /// 的目标、并按 <c>*.7z</c> 剪枝，图谱归档混在同一层会被覆写或删除；放进子目录即可隔离
        /// —— 主流程的扫描不递归，看不到这里。
        /// </summary>
        private const string HistoryPayloadDirectoryName = "history";

        internal static string ResolveHistoryPayloadDirectory(string backupSubDir)
            => Path.Combine(backupSubDir, HistoryPayloadDirectoryName);

        /// <summary>
        /// 把当前磁盘状态整体收成一次不进任何分支的独立提交（保护点）。
        /// 切换分支、还原这类会覆盖工作区的动作，在此之前必须先有它，否则用户的未收改动没有任何退路。
        /// </summary>
        /// <remarks>
        /// 这里刻意不吞异常：捕获被挡住时保护点并不存在，调用方必须按「没做成」处理，
        /// 而不是拿一个空的 <see cref="SafetySnapshot"/> 继续往下覆盖磁盘。
        /// </remarks>
        internal static async Task<SafetySnapshot> CreateSafetySnapshotAsync(
            BackupConfig config,
            SafetySnapshotReason reason,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(config);
            var outcome = await HistoryBackupCaptureService.CaptureAndCommitAsync(
                config,
                config.SourceFolders,
                BackupInvocationOptions.ForInternal(),
                comment: null,
                intent: HistoryCommitIntent.IndependentRecoveryPoint,
                safetySnapshotIntent: new HistorySafetySnapshotIntent(reason),
                cancellationToken).ConfigureAwait(false);

            if (outcome.Blocked)
            {
                throw new InvalidOperationException(
                    $"The independent recovery point was blocked, so the working files were not protected: {outcome.Diagnostic}");
            }

            return outcome.CommittedBatch?.NewSafetySnapshot
                ?? throw new InvalidOperationException("Independent recovery commit did not create a SafetySnapshot.");
        }

        /// <summary>
        /// 判断某个来源目录的内容此刻是否精确等于它工作区基线指向的那个版本。
        /// 探测要把该版本重新落一份出来再逐个文件比对，因此只用于切换分支这种一次性动作，不进备份热路径。
        /// </summary>
        internal static async Task<bool> DeepProbeWorkspaceVersionAsync(
            BackupConfig config,
            ManagedFolder folder,
            VersionId candidateVersionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(folder);
            if (!Guid.TryParse(folder.Id, out var sourceGuid) || sourceGuid == Guid.Empty)
            {
                return false;
            }

            var sourceId = new SourceId(sourceGuid);
            var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
            var workspace = (await runtime.WorkspaceStore.LoadAsync(cancellationToken).ConfigureAwait(false)).Value;
            var workspaceBaseline = workspace?.SourceBaselines
                .FirstOrDefault(item => item.SourceId == sourceId);
            if (workspaceBaseline?.BaseVersionId != candidateVersionId || !Directory.Exists(folder.Path))
            {
                return false;
            }

            var resolution = HistorySourceBoundaryResolver.Resolve(config, folder);
            if (resolution.IsBlocked)
            {
                return false;
            }

            var restore = await NativeHistoryApplicationService
                .CreateRestoreServiceAsync(config, cancellationToken).ConfigureAwait(false);
            return await new HistoryExactWorkingStateProbe(runtime, restore).IsExactAsync(
                new HistoryRestoreSourceBinding(
                    sourceId,
                    resolution.EffectiveFolder.Path,
                    resolution.Boundary),
                workspaceBaseline,
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 图谱提交通路接在主备份成功路径之后，是与旧记录写入并行的旁路。
        /// 这里必须吞掉一切异常：主备份已经落盘，图谱写不进去只应被记录，
        /// 不能让一次镜像失败把用户眼中的成功备份变成失败。
        /// </summary>
        private static async Task TryCaptureHistoryAsync(
            BackupConfig config,
            IReadOnlyList<ManagedFolder> folders,
            BackupInvocationOptions invocationOptions,
            string? comment)
        {
            try
            {
                var outcome = await HistoryBackupCaptureService.CaptureAndCommitAsync(
                    config,
                    folders,
                    invocationOptions,
                    comment,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
                if (outcome.Blocked)
                {
                    Log($"[History] 图谱未提交：{outcome.Diagnostic}", LogLevel.Warning);
                }
                else if (outcome.RecoveryRequired)
                {
                    Log($"[History] 图谱已提交但本机派生状态需要恢复：{outcome.Diagnostic}", LogLevel.Warning);
                }
                else
                {
                    Log($"[History] 图谱已提交 {outcome.Captures.Count} 个来源。", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                Log($"[History] 图谱提交失败：{ex.Message}", LogLevel.Error);
            }
        }

        /// <summary>
        /// 图谱自己的一套捕获：独立扫盘、独立建归档、独立校验，与主流程的归档互不共享，
        /// 因此这里任何失败都不会影响已经写入的主备份。
        /// </summary>
        /// <remarks>
        /// 捕获范围恒为 FullSource。1.9.x 的 PartialSource 来自插件按次给出的操作选择，
        /// 而本分支的插件 BackupScope 只收紧过滤器、不产生 include 文件夹范围，没有 PartialSource 的来源。
        /// </remarks>
        internal static Task<SourceCaptureResult> CaptureHistorySourceAsync(
            BackupConfig config,
            ManagedFolder effectiveFolder,
            string payloadDirectory,
            SourceCaptureBaseline? baseline,
            CancellationToken cancellationToken = default)
        {
            var sourceId = new SourceId(Guid.Parse(effectiveFolder.Id));
            var source = effectiveFolder.Path;
            var selection = effectiveFolder.SourceScope ?? new BackupSourceScope();
            return config.Archive.Mode switch
            {
                BackupMode.Incremental => DoHistorySmartBackupAsync(
                    sourceId, source, payloadDirectory, baseline, effectiveFolder, config, selection, cancellationToken),
                BackupMode.Overwrite => DoHistoryRollingBackupAsync(
                    sourceId, source, payloadDirectory, baseline, effectiveFolder, config, selection, cancellationToken),
                _ => DoHistoryFullBackupAsync(
                    sourceId, source, payloadDirectory, baseline, effectiveFolder, config, selection, cancellationToken)
            };
        }

        /// <summary>
        /// 模式 1：全量捕获。压缩边界内全部匹配文件，并返回可重建的 capture baseline candidate，
        /// 仅在 Native History 提交成功后由 runtime 更新本机缓存。
        /// </summary>
        /// <remarks>
        /// SkipIfUnchanged 短路需同时满足两个前提：确无变更，且缓存引用的上个归档文件仍然存在
        /// （防止基于已被手动删除的 payload 判定「无变化」）。
        /// <para>
        /// 与 1.9.x 的差异：这里始终把扫描得到的文件清单作为 7z 的 @listfile 传入，而 1.9.x 传 null、
        /// 依赖 7z 自己的 -xr! 排除规则复现扫描结果。两者并不等价 —— Run7zCommandAsync 明确跳过
        /// <c>regex:</c> 黑名单规则（7z 不支持），于是归档会多出被正则排除的文件，
        /// 而提交前的逻辑校验按同一份扫描结果比对条目数，直接判失败。
        /// 传清单让归档内容与冻结的边界由同一份扫描结果决定。
        /// </para>
        /// </remarks>
        private static async Task<SourceCaptureResult> DoHistoryFullBackupAsync(
            SourceId sourceId,
            string source,
            string payloadDirectory,
            SourceCaptureBaseline? baseline,
            ManagedFolder effectiveFolder,
            BackupConfig config,
            BackupSourceScope selection,
            CancellationToken cancellationToken)
        {
            var captureScope = CaptureScope.FullSource;
            var currentStates = HistorySourceFileEnumerator.Scan(source, config.Filters, selection, cancellationToken);
            if (BackupSourceAvailabilityPolicy.IsUnavailable(selection, currentStates.Count))
            {
                return SourceCaptureResult.Unavailable(sourceId, captureScope);
            }

            if (config.Archive.SkipIfUnchanged
                && baseline is not null
                && File.Exists(baseline.PayloadPath)
                && !ComputeHistoryChangeSet(currentStates, baseline).HasChanges)
            {
                Log(I18n.Format("BackupService_Log_NoChangesDetected"), LogLevel.Info);
                return SourceCaptureResult.NoChanges(sourceId, captureScope);
            }

            string fileName = GenerateFileName(effectiveFolder.DisplayName, config.Archive.Format, "Full", string.Empty);
            string destFile = Path.Combine(payloadDirectory, fileName);
            if (!TryResolveRequiredPassword(config, out var password, null))
            {
                return SourceCaptureResult.Failed(sourceId, captureScope);
            }

            var fileTypeExclusions = config.Archive.FileTypeHandlingEnabled
                ? (IReadOnlyList<FileTypeRule>)config.Archive.FileTypeRules
                : null;
            bool result;
            string? listFile = null;
            try
            {
                listFile = WriteHistoryCaptureListFile(currentStates.Keys);
                result = await Run7zCommandAsync(
                    "a",
                    source,
                    destFile,
                    config.Archive,
                    password,
                    listFile,
                    config.Filters,
                    fileTypeExclusions,
                    null,
                    applyAdditionalArguments: true).ConfigureAwait(false);

                if (result && config.Archive.FileTypeHandlingEnabled)
                {
                    bool ruleResult = await RunFileTypeRulePassesAsync(
                        source,
                        destFile,
                        config.Archive,
                        null,
                        config.Filters,
                        password,
                        null).ConfigureAwait(false);
                    if (!ruleResult)
                    {
                        // 规则追加失败不影响主备份结果，仅记录警告。
                        Log(I18n.Format("BackupService_Log_FileTypeRulePassFailed"), LogLevel.Warning);
                    }
                }
            }
            finally
            {
                TryDeleteHistoryListFile(listFile);
            }

            if (!result)
            {
                TryDeleteUncommittedHistoryArchive(destFile);
                return SourceCaptureResult.Failed(sourceId, captureScope);
            }

            return await VerifyAndCreateHistoryArchiveCaptureAsync(
                sourceId,
                captureScope,
                payloadDirectory,
                fileName,
                RepresentationKind.CoreFull,
                config.Archive.Format,
                currentStates,
                currentStates,
                allowDeletionMarker: false,
                baseline: baseline,
                dependencies: [],
                consecutiveSmartCaptures: 0,
                password: password,
                sevenZipExe: ResolveSevenZipExecutable(),
                expectedBaseVersionId: null,
                deletedFiles: null).ConfigureAwait(false);
        }

        /// <summary>
        /// 模式 2：智能增量捕获。与本机 capture baseline 比对后只压缩有变更的文件；
        /// 删除类变更用只含内部标记文件的「仅删除」归档表达。
        /// </summary>
        /// <remarks>
        /// 三种情况强制回退全量：baseline 缓存缺失或损坏、缓存引用的归档已被删除（增量链断裂）、
        /// 最近一次 Full 之后的 Smart 数量达到 MaxSmartBackupsPerFull 上限（截断链条）。
        /// 变更文件先按 FileTypeRules 拆分：不匹配规则的进主列表文件，匹配的留给追加压缩阶段处理；
        /// 若变更全部由删除构成，则跳过主压缩直接生成仅删除归档。
        /// </remarks>
        private static async Task<SourceCaptureResult> DoHistorySmartBackupAsync(
            SourceId sourceId,
            string source,
            string payloadDirectory,
            SourceCaptureBaseline? baseline,
            ManagedFolder effectiveFolder,
            BackupConfig config,
            BackupSourceScope selection,
            CancellationToken cancellationToken)
        {
            var captureScope = CaptureScope.FullSource;
            if (baseline is null || !File.Exists(baseline.PayloadPath))
            {
                Log(I18n.Format("BackupService_Log_NoBaselineMetadataFallbackFull"), LogLevel.Info);
                return await DoHistoryFullBackupAsync(
                    sourceId, source, payloadDirectory, baseline, effectiveFolder, config, selection,
                    cancellationToken).ConfigureAwait(false);
            }

            int maxChain = config.Archive.MaxSmartBackupsPerFull;
            if (maxChain > 0 && baseline.ConsecutiveSmartCaptures >= maxChain)
            {
                Log(I18n.Format("BackupService_Log_SmartChainLimitReached", maxChain), LogLevel.Info);
                return await DoHistoryFullBackupAsync(
                    sourceId, source, payloadDirectory, baseline, effectiveFolder, config, selection,
                    cancellationToken).ConfigureAwait(false);
            }

            Log(I18n.Format("BackupService_Log_AnalyzingDiff"), LogLevel.Info);
            var currentStates = HistorySourceFileEnumerator.Scan(source, config.Filters, selection, cancellationToken);
            if (BackupSourceAvailabilityPolicy.IsUnavailable(selection, currentStates.Count))
            {
                return SourceCaptureResult.Unavailable(sourceId, captureScope);
            }

            var changeSet = ComputeHistoryChangeSet(currentStates, baseline);
            if (!changeSet.HasChanges)
            {
                Log(I18n.Format("BackupService_Log_NoChangesDetected"), LogLevel.Info);
                return SourceCaptureResult.NoChanges(sourceId, captureScope);
            }

            var contentChangedFiles = changeSet.AddedFiles
                .Concat(changeSet.ModifiedFiles)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            Log(I18n.Format(
                "BackupService_Log_ChangesDetected",
                contentChangedFiles.Count + changeSet.DeletedFiles.Length), LogLevel.Info);

            var fileTypeRules = config.Archive.FileTypeRules;
            bool hasFileTypeRules = config.Archive.FileTypeHandlingEnabled
                && fileTypeRules != null
                && fileTypeRules.Count > 0;
            var mainFiles = contentChangedFiles;
            if (hasFileTypeRules && fileTypeRules != null)
            {
                var fileTypeMatchers = CompileFileTypeWildcardPatterns(fileTypeRules.Select(rule => rule.Pattern));
                mainFiles = contentChangedFiles.Where(f => !MatchesAnyFileTypePattern(f, fileTypeMatchers)).ToList();
            }

            string fileName = GenerateFileName(effectiveFolder.DisplayName, config.Archive.Format, "Smart", string.Empty);
            string destFile = Path.Combine(payloadDirectory, fileName);
            var fileTypeExclusions = hasFileTypeRules && fileTypeRules != null
                ? (IReadOnlyList<FileTypeRule>)fileTypeRules
                : null;
            if (!TryResolveRequiredPassword(config, out var password, null))
            {
                return SourceCaptureResult.Failed(sourceId, captureScope);
            }

            bool deletionOnlyChange = contentChangedFiles.Count == 0 && changeSet.DeletedFiles.Length > 0;
            bool result;
            string? listFile = null;
            try
            {
                if (!deletionOnlyChange)
                {
                    listFile = WriteHistoryCaptureListFile(mainFiles);
                }

                if (deletionOnlyChange)
                {
                    result = await CreateDeletionOnlyArchiveAsync(destFile, config.Archive, password, null)
                        .ConfigureAwait(false);
                }
                else if (listFile is not null)
                {
                    result = await Run7zCommandAsync(
                        "a",
                        source,
                        destFile,
                        config.Archive,
                        password,
                        listFile,
                        config.Filters,
                        fileTypeExclusions,
                        null,
                        applyAdditionalArguments: true).ConfigureAwait(false);
                }
                else
                {
                    // 全部变更文件都被自定义规则接管，主压缩阶段跳过，归档由追加阶段创建。
                    result = true;
                }

                if (result && hasFileTypeRules && contentChangedFiles.Count > 0)
                {
                    bool ruleResult = await RunFileTypeRulePassesAsync(
                        source,
                        destFile,
                        config.Archive,
                        contentChangedFiles,
                        config.Filters,
                        password,
                        null).ConfigureAwait(false);
                    if (!ruleResult)
                    {
                        if (listFile is null)
                        {
                            // 归档全靠追加阶段产出，追加失败即本次捕获失败。
                            result = false;
                        }
                        else
                        {
                            Log(I18n.Format("BackupService_Log_FileTypeRulePassFailed"), LogLevel.Warning);
                        }
                    }
                }
            }
            finally
            {
                TryDeleteHistoryListFile(listFile);
            }

            if (!result || !File.Exists(destFile))
            {
                TryDeleteUncommittedHistoryArchive(destFile);
                return SourceCaptureResult.Failed(sourceId, captureScope);
            }

            var expectedDeltaStates = contentChangedFiles.ToDictionary(
                path => path,
                path => currentStates[path],
                StringComparer.Ordinal);
            return await VerifyAndCreateHistoryArchiveCaptureAsync(
                sourceId,
                captureScope,
                payloadDirectory,
                fileName,
                RepresentationKind.CoreSmartDelta,
                config.Archive.Format,
                currentStates,
                expectedDeltaStates,
                allowDeletionMarker: deletionOnlyChange,
                baseline: baseline,
                dependencies: [baseline.BaseRepresentationId],
                consecutiveSmartCaptures: checked(baseline.ConsecutiveSmartCaptures + 1),
                password: password,
                sevenZipExe: ResolveSevenZipExecutable(),
                expectedBaseVersionId: baseline.BaseVersionId,
                deletedFiles: changeSet.DeletedFiles).ConfigureAwait(false);
        }

        /// <summary>
        /// 模式 3：Rolling copy-on-write 捕获。从已验证的自包含基线创建唯一 staging 副本，
        /// 在副本上应用增删改，校验通过后再以 create-once 语义安装为新归档；
        /// 全程不修改已提交的基线字节。基线缺失、类型不明、校验失败时保守回退全量。
        /// </summary>
        private static async Task<SourceCaptureResult> DoHistoryRollingBackupAsync(
            SourceId sourceId,
            string source,
            string payloadDirectory,
            SourceCaptureBaseline? baseline,
            ManagedFolder effectiveFolder,
            BackupConfig config,
            BackupSourceScope selection,
            CancellationToken cancellationToken)
        {
            var captureScope = CaptureScope.FullSource;
            async Task<SourceCaptureResult> FallbackToFullAsync()
                => await DoHistoryFullBackupAsync(
                    sourceId, source, payloadDirectory, baseline, effectiveFolder, config, selection,
                    cancellationToken).ConfigureAwait(false);

            if (baseline is null
                || selection.Mode == BackupSourceScopeMode.Include
                || baseline.BaseRepresentationKind is not (RepresentationKind.CoreFull or RepresentationKind.CoreRolling)
                || !File.Exists(baseline.PayloadPath))
            {
                Log(I18n.Format("BackupService_Log_NoBaselineMetadataFallbackFull"), LogLevel.Info);
                return await FallbackToFullAsync().ConfigureAwait(false);
            }

            string baselinePath = baseline.PayloadPath;
            string baselineFileName = Path.GetFileName(baselinePath);

            var currentStates = HistorySourceFileEnumerator.Scan(source, config.Filters, selection, cancellationToken);
            if (BackupSourceAvailabilityPolicy.IsUnavailable(selection, currentStates.Count))
            {
                return SourceCaptureResult.Unavailable(sourceId, captureScope);
            }

            var changeSet = ComputeHistoryChangeSet(currentStates, baseline);
            if (config.Archive.SkipIfUnchanged && !changeSet.HasChanges)
            {
                Log(I18n.Format("BackupService_Log_NoChangesDetected"), LogLevel.Info);
                return SourceCaptureResult.NoChanges(sourceId, captureScope);
            }

            if (!TryResolveRequiredPassword(config, out var password, null))
            {
                return SourceCaptureResult.Failed(sourceId, captureScope);
            }

            string? sevenZipExe = ResolveSevenZipExecutable();
            if (string.IsNullOrWhiteSpace(sevenZipExe)
                || !await ValidateRestoreChainAsync([new FileInfo(baselinePath)], sevenZipExe, password, null)
                    .ConfigureAwait(false))
            {
                Log(I18n.Format("BackupService_Log_RestoreIntegrityArchiveCheckFailed", baselineFileName), LogLevel.Warning);
                return await FallbackToFullAsync().ConfigureAwait(false);
            }

            string finalFileName = CreateUniqueRollingHistoryFileName(
                payloadDirectory, effectiveFolder.DisplayName, config.Archive.Format);
            string finalPath = Path.Combine(payloadDirectory, finalFileName);
            string? changedListFile = null;
            string? deletedListFile = null;
            bool committed = false;
            try
            {
                using var transaction = RollingArchiveTransaction.Create(baselinePath, payloadDirectory);

                var changedFiles = changeSet.AddedFiles
                    .Concat(changeSet.ModifiedFiles)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var fileTypeRules = config.Archive.FileTypeRules;
                bool hasFileTypeRules = config.Archive.FileTypeHandlingEnabled
                    && fileTypeRules != null
                    && fileTypeRules.Count > 0;
                var fileTypeMatchers = hasFileTypeRules
                    ? CompileFileTypeWildcardPatterns(fileTypeRules!.Select(rule => rule.Pattern))
                    : Array.Empty<Regex>();
                var mainChangedFiles = hasFileTypeRules
                    ? changedFiles.Where(path => !MatchesAnyFileTypePattern(path, fileTypeMatchers)).ToList()
                    : changedFiles;

                // staging 副本已是完整归档快照，增删都直接作用于它；过滤规则不重复施加 ——
                // 清单里的路径已经过了边界判定，再让 7z 按黑名单排除一遍只会让两边对不上。
                bool updated = true;
                if (mainChangedFiles.Count > 0)
                {
                    changedListFile = WriteHistoryCaptureListFile(mainChangedFiles);
                    updated = changedListFile is not null && await Run7zCommandAsync(
                        "u",
                        source,
                        transaction.StagingPath,
                        config.Archive,
                        password,
                        changedListFile,
                        filters: null,
                        fileTypeExclusions: null,
                        null,
                        applyAdditionalArguments: true).ConfigureAwait(false);
                }

                if (updated && hasFileTypeRules && changedFiles.Count > 0)
                {
                    updated = await RunFileTypeRulePassesAsync(
                        source,
                        transaction.StagingPath,
                        config.Archive,
                        changedFiles,
                        filters: null,
                        password,
                        null).ConfigureAwait(false);
                }

                if (updated && changeSet.DeletedFiles.Length > 0)
                {
                    deletedListFile = WriteHistoryCaptureListFile(changeSet.DeletedFiles);
                    updated = deletedListFile is not null && await Run7zCommandAsync(
                        "d",
                        source,
                        transaction.StagingPath,
                        config.Archive,
                        password,
                        deletedListFile,
                        filters: null,
                        fileTypeExclusions: null,
                        null).ConfigureAwait(false);
                }

                bool exactArchiveState = updated
                    && await ValidateRestoreChainAsync([new FileInfo(transaction.StagingPath)], sevenZipExe, password, null)
                        .ConfigureAwait(false)
                    && await ValidateArchiveLogicalStateAsync(
                        sevenZipExe, transaction.StagingPath, password, currentStates).ConfigureAwait(false);
                if (!exactArchiveState)
                {
                    transaction.Dispose();
                    return await FallbackToFullAsync().ConfigureAwait(false);
                }

                transaction.Commit(finalPath);
                committed = true;
                return CreateHistoryArchiveCapture(
                    sourceId,
                    captureScope,
                    payloadDirectory,
                    finalFileName,
                    RepresentationKind.CoreRolling,
                    config.Archive.Format,
                    currentStates,
                    baseline,
                    dependencies: [],
                    consecutiveSmartCaptures: 0);
            }
            catch (Exception ex)
            {
                Log($"[History][Rolling] {ex.Message}", LogLevel.Error);
                if (committed)
                {
                    TryDeleteUncommittedHistoryArchive(finalPath);
                }
                return SourceCaptureResult.Failed(sourceId, captureScope);
            }
            finally
            {
                TryDeleteHistoryListFile(changedListFile);
                TryDeleteHistoryListFile(deletedListFile);
            }
        }

        /// <summary>
        /// 本次扫描结果相对基线状态的增删改。捕获范围恒为 FullSource，
        /// 基线里出现过的每个路径都在本次范围内，故范围判定恒真 ——
        /// 走 ScopeAwareSourceCaptureDiff 统一算法，避免两处差异实现漂移。
        /// </summary>
        private static ScopeAwareSourceCaptureDiff ComputeHistoryChangeSet(
            IReadOnlyDictionary<string, SourceCaptureFileState> currentStates,
            SourceCaptureBaseline baseline)
            => ScopeAwareSourceCaptureDiff.Compute(baseline.FileStates, currentStates, static _ => true);

        /// <summary>
        /// 深度校验归档的逻辑清单与捕获时冻结的受管理状态完全一致：
        /// 条目与文件大小必须在两个方向上都吻合；不安全或重复的归档路径直接判失败。
        /// </summary>
        private static async Task<bool> ValidateArchiveLogicalStateAsync(
            string sevenZipExe,
            string archivePath,
            string? password,
            IReadOnlyDictionary<string, SourceCaptureFileState> expectedStates,
            bool allowDeletionMarker = false)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = sevenZipExe,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    CreateNoWindow = true
                };
                string? workingDirectory = Path.GetDirectoryName(archivePath);
                if (!string.IsNullOrWhiteSpace(workingDirectory)) startInfo.WorkingDirectory = workingDirectory;
                startInfo.ArgumentList.Add("l");
                startInfo.ArgumentList.Add("-slt");
                startInfo.ArgumentList.Add("-sccUTF-8");
                startInfo.ArgumentList.Add(archivePath);
                if (!string.IsNullOrWhiteSpace(password)) startInfo.ArgumentList.Add($"-p{password}");
                using var process = new Process { StartInfo = startInfo };
                if (!process.Start()) return false;
                var standardOutput = process.StandardOutput.ReadToEndAsync();
                var standardError = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().ConfigureAwait(false);
                string output = await standardOutput.ConfigureAwait(false);
                string error = await standardError.ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    Log($"[History] 7z listing failed: {error}", LogLevel.Warning);
                    return false;
                }

                if (!SevenZipArchiveListingParser.TryParse(output, out var entries))
                {
                    Log("[History] 7z listing output was ambiguous or malformed.", LogLevel.Warning);
                    return false;
                }

                var expectedSizes = expectedStates.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Size,
                    StringComparer.OrdinalIgnoreCase);
                if (!allowDeletionMarker)
                {
                    var matches = ArchiveLogicalStateVerifier.TryMatch(expectedSizes, entries, out var diagnostic);
                    if (!matches) Log($"[History] {diagnostic}", LogLevel.Warning);
                    return matches;
                }

                // 不用 Path.Combine 拼标记路径：校验用的是 OrdinalIgnoreCase 的字符串相等，
                // Path.Combine 会把分隔符换成当前系统的分隔符，反而比不中。
                var markerPath = $"{InternalRestoreMarkerDirectoryName}/{InternalRestoreMarkerFileName}";
                var userEntries = entries
                    .Where(pair => !string.Equals(pair.Key, markerPath, StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                bool markerSetIsValid = entries.ContainsKey(markerPath)
                    && entries.Keys.All(path => userEntries.ContainsKey(path)
                        || string.Equals(path, markerPath, StringComparison.OrdinalIgnoreCase));
                if (!markerSetIsValid)
                {
                    Log("[History] Smart deletion marker is missing or invalid.", LogLevel.Warning);
                    return false;
                }

                var deltaMatches = ArchiveLogicalStateVerifier.TryMatch(expectedSizes, userEntries, out var deltaDiagnostic);
                if (!deltaMatches) Log($"[History] {deltaDiagnostic}", LogLevel.Warning);
                return deltaMatches;
            }
            catch (Exception ex)
            {
                Log($"[History] Archive listing verification failed: {ex.Message}", LogLevel.Warning);
                return false;
            }
        }

        /// <summary>
        /// 归档先校验、后登记为一次捕获事实；校验不过的一律删除，绝不让未验证的字节进入历史。
        /// </summary>
        private static async Task<SourceCaptureResult> VerifyAndCreateHistoryArchiveCaptureAsync(
            SourceId sourceId,
            CaptureScope captureScope,
            string payloadDirectory,
            string fileName,
            RepresentationKind kind,
            string format,
            IReadOnlyDictionary<string, SourceCaptureFileState> currentStates,
            IReadOnlyDictionary<string, SourceCaptureFileState> expectedArchiveStates,
            bool allowDeletionMarker,
            SourceCaptureBaseline? baseline,
            IEnumerable<RepresentationId> dependencies,
            int consecutiveSmartCaptures,
            string? password,
            string? sevenZipExe,
            VersionId? expectedBaseVersionId,
            IEnumerable<string>? deletedFiles)
        {
            var archivePath = Path.GetFullPath(Path.Combine(payloadDirectory, fileName));
            bool verified = !string.IsNullOrWhiteSpace(sevenZipExe)
                && File.Exists(archivePath)
                && await ValidateRestoreChainAsync([new FileInfo(archivePath)], sevenZipExe, password, null)
                    .ConfigureAwait(false)
                && await ValidateArchiveLogicalStateAsync(
                    sevenZipExe,
                    archivePath,
                    password,
                    expectedArchiveStates,
                    allowDeletionMarker).ConfigureAwait(false);
            if (!verified)
            {
                TryDeleteUncommittedHistoryArchive(archivePath);
                return SourceCaptureResult.Failed(
                    sourceId,
                    captureScope,
                    I18n.GetString("BackupService_HistoryCaptureVerificationFailed"));
            }

            return CreateHistoryArchiveCapture(
                sourceId,
                captureScope,
                payloadDirectory,
                fileName,
                kind,
                format,
                currentStates,
                baseline,
                dependencies,
                consecutiveSmartCaptures,
                expectedBaseVersionId,
                deletedFiles);
        }

        private static SourceCaptureResult CreateHistoryArchiveCapture(
            SourceId sourceId,
            CaptureScope captureScope,
            string payloadDirectory,
            string fileName,
            RepresentationKind kind,
            string format,
            IReadOnlyDictionary<string, SourceCaptureFileState> currentStates,
            SourceCaptureBaseline? baseline,
            IEnumerable<RepresentationId> dependencies,
            int consecutiveSmartCaptures,
            VersionId? expectedBaseVersionId = null,
            IEnumerable<string>? deletedFiles = null)
            => VerifiedArchiveCaptureFactory.Create(
                sourceId,
                captureScope,
                Path.Combine(payloadDirectory, fileName),
                kind,
                format,
                currentStates,
                baseline,
                dependencies,
                consecutiveSmartCaptures,
                captureScope == CaptureScope.FullSource
                    || (kind == RepresentationKind.CoreSmartDelta && expectedBaseVersionId is not null)
                    ? MaterializationFidelity.Exact
                    : MaterializationFidelity.Partial,
                expectedBaseVersionId,
                deletedFiles);

        /// <summary>
        /// 把待归档的相对路径写成 7z 的 @listfile；没有条目时返回 null，由调用方沿用 7z 自身的枚举。
        /// 相对路径由 RunSevenZipProcessAsync 设定的工作目录（源目录）解析。
        /// </summary>
        private static string? WriteHistoryCaptureListFile(IEnumerable<string> relativePaths)
        {
            var paths = relativePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (paths.Count == 0) return null;

            var listFile = Path.GetTempFileName();
            // 不带 BOM：7z 会把 BOM 当成第一个路径的一部分。
            File.WriteAllLines(listFile, paths, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return listFile;
        }

        private static void TryDeleteHistoryListFile(string? listFile)
        {
            if (string.IsNullOrWhiteSpace(listFile)) return;
            try
            {
                File.Delete(listFile);
            }
            catch
            {
            }
        }

        private static void TryDeleteUncommittedHistoryArchive(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log($"[History] Failed to clean an uncommitted archive: {ex.Message}", LogLevel.Warning);
            }
        }

        /// <summary>
        /// Rolling 归档是 create-once 的，名字撞车时补一个随机后缀而不是覆盖已有归档。
        /// </summary>
        private static string CreateUniqueRollingHistoryFileName(
            string payloadDirectory,
            string baseName,
            string format)
        {
            string candidate = GenerateFileName(baseName, format, "Rolling", string.Empty);
            if (!File.Exists(Path.Combine(payloadDirectory, candidate)))
            {
                return candidate;
            }

            string extension = Path.GetExtension(candidate);
            string stem = Path.GetFileNameWithoutExtension(candidate);
            return $"{stem}-{Guid.NewGuid():N}{extension}";
        }
    }
}

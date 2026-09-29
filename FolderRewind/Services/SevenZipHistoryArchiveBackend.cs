using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.History.Storage;
using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    /// <summary>
    /// 归档类表示形态（CoreFull / CoreRolling / CoreSmartDelta）的校验与落地，底层是 7z。
    /// <para>
    /// 「落地」是按依赖顺序把闭包里的归档依次解到暂存目录，再按归档自带的删除清单删掉本轮不再存在的文件 ——
    /// 差量归档只带变化，只有照这个顺序叠上去才等于被捕获时的目录状态。
    /// </para>
    /// </summary>
    internal sealed class SevenZipHistoryArchiveBackend : IArchiveRepresentationBackend
    {
        private readonly BackupConfig _config;

        public SevenZipHistoryArchiveBackend(BackupConfig config)
            => _config = config ?? throw new ArgumentNullException(nameof(config));

        public async ValueTask<PayloadVerificationResult> VerifyAsync(
            VersionRepresentation representation,
            string localPath,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(localPath))
                return new(false, string.Empty, "Archive payload is missing.");
            var result = await RunAsync("t", localPath, outputDirectory: null, workingDirectory: null, cancellationToken)
                .ConfigureAwait(false);
            return result.Success
                ? new(true, $"7z-test:{new FileInfo(localPath).Length}", string.Empty)
                : new(false, string.Empty, result.Diagnostic);
        }

        public async ValueTask MaterializeAsync(
            IReadOnlyList<ArchiveMaterializationInput> dependencyFirstInputs,
            string stagingDirectory,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(stagingDirectory);
            foreach (var input in dependencyFirstInputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await RunAsync(
                    "x",
                    input.LocalPath,
                    stagingDirectory,
                    workingDirectory: null,
                    cancellationToken).ConfigureAwait(false);
                if (!result.Success)
                    throw new InvalidDataException(result.Diagnostic);
                ApplyDeletedFiles(input.Representation, stagingDirectory);
            }

            // 备份归档里带的内部标记目录只服务于备份流程本身，不能跟着落回用户目录。
            var marker = Path.Combine(stagingDirectory, BackupService.InternalRestoreMarkerDirectoryName);
            if (Directory.Exists(marker)) Directory.Delete(marker, recursive: true);
        }

        private async Task<(bool Success, string Diagnostic)> RunAsync(
            string operation,
            string archivePath,
            string? outputDirectory,
            string? workingDirectory,
            CancellationToken cancellationToken)
        {
            var executable = SevenZipExecutableLocator.Resolve(
                ConfigService.CurrentConfig?.GlobalSettings?.SevenZipPath);
            if (string.IsNullOrWhiteSpace(executable))
                return (false, I18n.GetString("BackupService_Log_SevenZipNotFound"));
            var password = _config.IsEncrypted ? EncryptionService.RetrievePassword(_config.Id) : null;
            if (_config.IsEncrypted && string.IsNullOrEmpty(password))
                return (false, "Encrypted archive credential is unavailable.");

            var start = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                    ? Path.GetDirectoryName(archivePath) ?? Environment.CurrentDirectory
                    : workingDirectory
            };
            start.ArgumentList.Add(operation);
            start.ArgumentList.Add(archivePath);
            if (operation == "x") start.ArgumentList.Add("-o" + outputDirectory);
            start.ArgumentList.Add("-y");
            if (!string.IsNullOrEmpty(password)) start.ArgumentList.Add("-p" + password);

            using var process = new Process { StartInfo = start };
            if (!process.Start()) return (false, "7z process did not start.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            return process.ExitCode == 0
                ? (true, string.Empty)
                : (false, string.IsNullOrWhiteSpace(error) ? output : error);
        }

        /// <summary>
        /// 差量归档自带的「本轮被删掉的文件」清单。路径来自归档元数据，属于不可信输入，
        /// 必须逐条确认它没有越出暂存根目录。
        /// </summary>
        private static void ApplyDeletedFiles(VersionRepresentation representation, string stagingDirectory)
        {
            if (!representation.RepresentationSpecificMetadata.TryGetValue("deletedFiles", out var encoded))
                return;
            var root = Path.GetFullPath(stagingDirectory);
            foreach (var relative in encoded.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var normalized = relative.Replace('\\', '/');
                if (!HistoryRepositoryPaths.IsSafeRepositoryRelativePath(normalized))
                    throw new InvalidDataException("Smart deletion path is unsafe.");
                var target = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
                var relation = Path.GetRelativePath(root, target);
                if (relation == ".." || relation.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new InvalidDataException("Smart deletion escapes the materialization root.");
                if (File.Exists(target)) File.Delete(target);
                else if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            }
        }
    }
}

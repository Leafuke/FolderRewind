using FolderRewind.History.Application;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Migration;

public sealed class LegacyHistoryMigrationService
{
    private readonly LegacyHistoryMigrationBuilder _builder;

    public LegacyHistoryMigrationService(LegacyHistoryMigrationBuilder? builder = null)
        => _builder = builder ?? new LegacyHistoryMigrationBuilder();

    public async Task<LegacyHistoryMigrationResult> MigrateAsync(
        LegacyHistoryMigrationInput input,
        Func<int, CancellationToken, Task> persistBindingAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(persistBindingAsync);
        var targetPaths = HistoryRepositoryPaths.ForConfigDirectory(input.ConfigDirectory, input.ConfigId);
        if (File.Exists(targetPaths.DescriptorPath))
        {
            var existing = await OpenExistingAsync(input, targetPaths, cancellationToken).ConfigureAwait(false);
            if (existing is null)
                return new(LegacyHistoryMigrationStatus.Failed, "Existing Native repository failed validation.", null);
            try
            {
                await persistBindingAsync(HistoryRepositoryDescriptor.CurrentFormatVersion, cancellationToken)
                    .ConfigureAwait(false);
                return new(LegacyHistoryMigrationStatus.ExistingRepositoryBound, string.Empty, existing);
            }
            catch (Exception ex)
            {
                existing.Dispose();
                return new(LegacyHistoryMigrationStatus.BindingPersistenceFailed, ex.Message, null);
            }
        }

        var historyRoot = Path.GetDirectoryName(targetPaths.RepositoryRoot)
            ?? throw new InvalidOperationException("Native History target has no parent directory.");
        var migrationParent = Path.Combine(historyRoot, ".migration");
        var stagingRoot = Path.Combine(
            migrationParent,
            HistoryRepositoryPaths.EncodeConfigPathSegment(input.ConfigId) + "-" + Guid.NewGuid().ToString("N"));
        FileHistoryRepository? stagedRepository = null;
        try
        {
            var build = _builder.Build(input);
            stagedRepository = new FileHistoryRepository(
                input.ConfigId,
                new HistoryRepositoryPaths(stagingRoot));
            await stagedRepository.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var codec = new HistoryPackCodec();
            await stagedRepository.ImportAsync(
                build.Packs.Select(pack => (ReadOnlyMemory<byte>)codec.Encode(pack)),
                cancellationToken).ConfigureAwait(false);
            await stagedRepository.LocalStateAsync(build, cancellationToken).ConfigureAwait(false);
            await using (var validationRuntime = new HistoryRuntime(stagedRepository))
            {
                stagedRepository = null;
                await validationRuntime.InitializeAsync(cancellationToken).ConfigureAwait(false);
            }

            Directory.CreateDirectory(historyRoot);
            if (Directory.Exists(targetPaths.RepositoryRoot))
                throw new IOException("Native History target appeared during Legacy migration.");
            Directory.Move(stagingRoot, targetPaths.RepositoryRoot);
            var opened = await OpenExistingAsync(input, targetPaths, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Migrated repository failed validation after atomic cutover.");
            try
            {
                await persistBindingAsync(HistoryRepositoryDescriptor.CurrentFormatVersion, cancellationToken)
                    .ConfigureAwait(false);
                return new(LegacyHistoryMigrationStatus.MigratedAndBound, string.Empty, opened);
            }
            catch (Exception ex)
            {
                opened.Dispose();
                return new(LegacyHistoryMigrationStatus.BindingPersistenceFailed, ex.Message, null);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stagedRepository?.Dispose();
            CleanupStaging(stagingRoot, migrationParent);
            throw;
        }
        catch (Exception ex)
        {
            stagedRepository?.Dispose();
            CleanupStaging(stagingRoot, migrationParent);
            return new(LegacyHistoryMigrationStatus.Failed, ex.Message, null);
        }
    }

    private static async Task<FileHistoryRepository?> OpenExistingAsync(
        LegacyHistoryMigrationInput input,
        HistoryRepositoryPaths paths,
        CancellationToken cancellationToken)
    {
        FileHistoryRepository? repository = null;
        try
        {
            repository = new FileHistoryRepository(input.ConfigId, paths);
            await using var runtime = new HistoryRuntime(repository);
            repository = null;
            await runtime.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var opened = new FileHistoryRepository(input.ConfigId, paths);
            await opened.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return opened;
        }
        catch
        {
            repository?.Dispose();
            return null;
        }
    }

    private static void CleanupStaging(string stagingRoot, string migrationParent)
    {
        try
        {
            var parent = Path.GetFullPath(migrationParent);
            var target = Path.GetFullPath(stagingRoot);
            var relative = Path.GetRelativePath(parent, target);
            if (relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
        catch { }
    }
}

internal static class LegacyHistoryMigrationLocalStateExtensions
{
    public static async Task LocalStateAsync(
        this FileHistoryRepository repository,
        LegacyHistoryMigrationBuild build,
        CancellationToken cancellationToken)
    {
        using var workspace = new HistoryWorkspaceStore(
            repository.ConfigId,
            Path.Combine(repository.Paths.LocalStateRoot, "workspace.json"));
        using var catalog = new LocalReplicaCatalogStore(
            repository.ConfigId,
            Path.Combine(repository.Paths.LocalStateRoot, "replicas.json"));
        await workspace.SaveAsync(build.Workspace, HistoryWorkspaceStore.MissingRevision, cancellationToken)
            .ConfigureAwait(false);
        await catalog.SaveAsync(
            build.LocalReplicaCatalog,
            LocalReplicaCatalogStore.MissingRevision,
            cancellationToken).ConfigureAwait(false);
    }
}

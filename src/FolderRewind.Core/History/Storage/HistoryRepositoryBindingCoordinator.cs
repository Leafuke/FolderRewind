using FolderRewind.History.Domain;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Storage;

public enum HistoryRepositoryBindingStatus
{
    Bound = 0,
    CreatedAndBound = 1,
    RecoveryRequired = 2,
    CompatibilityBlocked = 3,
    BindingPersistenceFailed = 4
}

public sealed record HistoryRepositoryBindingResult(
    HistoryRepositoryBindingStatus Status,
    FileHistoryRepository? Repository,
    string Diagnostic)
{
    public bool IsReady => Status is HistoryRepositoryBindingStatus.Bound
        or HistoryRepositoryBindingStatus.CreatedAndBound;
}

public sealed class HistoryRepositoryBindingCoordinator
{
    private readonly string _configDirectory;

    public HistoryRepositoryBindingCoordinator(string configDirectory)
        => _configDirectory = Path.GetFullPath(
            configDirectory ?? throw new ArgumentNullException(nameof(configDirectory)));

    public async Task<HistoryRepositoryBindingResult> EnsureAsync(
        HistoryConfigId configId,
        int? bindingFormatVersion,
        Func<int, CancellationToken, Task> persistBindingAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persistBindingAsync);
        var paths = HistoryRepositoryPaths.ForConfigDirectory(_configDirectory, configId);
        if (bindingFormatVersion is { } boundVersion)
        {
            if (boundVersion != HistoryRepositoryDescriptor.CurrentFormatVersion)
            {
                return new(
                    HistoryRepositoryBindingStatus.CompatibilityBlocked,
                    null,
                    $"History repository binding format {boundVersion} is unsupported.");
            }

            if (!File.Exists(paths.DescriptorPath))
            {
                return new(
                    HistoryRepositoryBindingStatus.RecoveryRequired,
                    null,
                    "Configuration is bound to Native History, but repository.json is missing.");
            }

            return await OpenExistingAsync(configId, paths, cancellationToken).ConfigureAwait(false);
        }

        var repositoryAlreadyExisted = File.Exists(paths.DescriptorPath);
        FileHistoryRepository? repository = null;
        try
        {
            if (repositoryAlreadyExisted)
            {
                var opened = await OpenExistingAsync(configId, paths, cancellationToken).ConfigureAwait(false);
                if (!opened.IsReady)
                {
                    return opened;
                }
                repository = opened.Repository;
            }
            else
            {
                repository = new FileHistoryRepository(configId, paths);
                await repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                await persistBindingAsync(
                    HistoryRepositoryDescriptor.CurrentFormatVersion,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // descriptor 已经 create-once 落盘；下次启动会验证同一 ConfigId 后补完 binding，
                // 绝不能因配置保存失败再创建第二个仓库。
                repository?.Dispose();
                return new(
                    HistoryRepositoryBindingStatus.BindingPersistenceFailed,
                    null,
                    ex.Message);
            }

            return new(
                repositoryAlreadyExisted
                    ? HistoryRepositoryBindingStatus.Bound
                    : HistoryRepositoryBindingStatus.CreatedAndBound,
                repository,
                string.Empty);
        }
        catch
        {
            repository?.Dispose();
            throw;
        }
    }

    private static async Task<HistoryRepositoryBindingResult> OpenExistingAsync(
        HistoryConfigId configId,
        HistoryRepositoryPaths paths,
        CancellationToken cancellationToken)
    {
        FileHistoryRepository? repository = null;
        try
        {
            var descriptorBytes = await File.ReadAllBytesAsync(paths.DescriptorPath, cancellationToken)
                .ConfigureAwait(false);
            var descriptor = HistoryRepositoryDescriptor.Parse(descriptorBytes);
            if (descriptor.ConfigId != configId)
            {
                return new(
                    HistoryRepositoryBindingStatus.RecoveryRequired,
                    null,
                    "History repository descriptor is bound to another ConfigId.");
            }

            repository = new FileHistoryRepository(configId, paths);
            await repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return new(HistoryRepositoryBindingStatus.Bound, repository, string.Empty);
        }
        catch (HistoryPackCompatibilityException ex)
        {
            repository?.Dispose();
            return new(HistoryRepositoryBindingStatus.CompatibilityBlocked, null, ex.Message);
        }
        catch (HistoryRepositoryException ex)
        {
            repository?.Dispose();
            return new(HistoryRepositoryBindingStatus.RecoveryRequired, null, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            repository?.Dispose();
            return new(HistoryRepositoryBindingStatus.RecoveryRequired, null, ex.Message);
        }
    }
}

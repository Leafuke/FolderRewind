using FolderRewind.Models;
using FolderRewind.Services.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Discovery;

public sealed class GameDiscoveryService
{
    private readonly IReadOnlyList<IFolderRewindDiscoveryProvider> _providers;

    public GameDiscoveryService(IEnumerable<IFolderRewindDiscoveryProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers
            .Where(provider => provider != null)
            .GroupBy(provider => provider.Descriptor.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(provider => provider.Descriptor.Priority).First())
            .OrderByDescending(provider => provider.Descriptor.Priority)
            .ToList();
    }

    public async Task<GameDiscoveryResult> DiscoverAsync(
        DiscoveryRequest request,
        IProgress<DiscoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var tasks = _providers.Select(provider =>
            RunProviderAsync(provider, request, progress, cancellationToken));
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return new GameDiscoveryResult
        {
            Candidates = DiscoveryCandidateMerger.Merge(results),
            Diagnostics = results.SelectMany(result => result.Diagnostics).ToList()
        };
    }

    private static async Task<DiscoveryProviderResult> RunProviderAsync(
        IFolderRewindDiscoveryProvider provider,
        DiscoveryRequest request,
        IProgress<DiscoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await provider.DiscoverAsync(request, progress, cancellationToken)
                .ConfigureAwait(false);
            return result ?? FailureResult(
                provider.Descriptor.Id,
                "provider-returned-null",
                "The discovery provider returned no result.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FailureResult(provider.Descriptor.Id, "provider-failed", ex.Message);
        }
    }

    private static DiscoveryProviderResult FailureResult(
        string providerId,
        string code,
        string message)
    {
        return new DiscoveryProviderResult
        {
            ProviderId = providerId,
            Diagnostics = new[]
            {
                new DiscoveryDiagnostic
                {
                    Severity = DiscoveryDiagnosticSeverity.Error,
                    Code = code,
                    Message = message,
                    ProviderId = providerId
                }
            }
        };
    }
}

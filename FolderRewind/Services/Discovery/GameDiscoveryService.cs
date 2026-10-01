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
    private readonly IReadOnlyList<IGameDiscoveryProvider> _providers;
    private readonly IReadOnlyList<DiscoveryDiagnostic> _compositionDiagnostics;

    public GameDiscoveryService(
        IEnumerable<IGameDiscoveryProvider> providers,
        IEnumerable<DiscoveryDiagnostic>? compositionDiagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers
            .Where(provider => provider != null)
            .GroupBy(provider => provider.Descriptor.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(provider => provider.Descriptor.Priority).First())
            .OrderByDescending(provider => provider.Descriptor.Priority)
            .ToList();
        _compositionDiagnostics = (compositionDiagnostics ?? Array.Empty<DiscoveryDiagnostic>()).ToList();
    }

    public async Task<GameDiscoveryResult> DiscoverAsync(
        DiscoveryRequest request,
        IProgress<DiscoveryProgress>? progress,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        int? maxCandidates = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (timeout is { } duration && duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (maxCandidates is <= 0) throw new ArgumentOutOfRangeException(nameof(maxCandidates));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } limit) budget.CancelAfter(limit);

        var providers = _providers.AsEnumerable();
        if (request.Mode == DiscoveryRequestMode.PresetTargeted)
        {
            var requestedProviderIds = request.Definitions.Select(value => value.ProviderId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            providers = providers.Where(provider => requestedProviderIds.Contains(provider.Descriptor.Id));
        }
        var tasks = providers.Select(provider =>
            RunProviderAsync(provider, request, progress, budget.Token)).ToArray();
        var all = Task.WhenAll(tasks);
        DiscoveryProviderResult[] results;
        var timedOut = false;
        try { results = await all.WaitAsync(budget.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && budget.IsCancellationRequested)
        {
            timedOut = true;
            results = tasks.Where(task => task.IsCompletedSuccessfully).Select(task => task.Result).ToArray();
        }
        finally
        {
            // Providers may ignore cancellation. Observe late failures without waiting for them.
            _ = all.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        cancellationToken.ThrowIfCancellationRequested();

        var diagnostics = RelevantCompositionDiagnostics(request).ToList();
        diagnostics.AddRange(results.SelectMany(result => result.Diagnostics));
        if (timedOut) diagnostics.Add(new DiscoveryDiagnostic
        {
            Severity = DiscoveryDiagnosticSeverity.Warning, Code = "discovery-time-budget",
            Message = "Discovery reached its time limit. Completed provider results are retained; choose a narrower root to continue."
        });
        if (request.Mode == DiscoveryRequestMode.PresetTargeted)
        {
            var availableProviders = _providers.Select(provider => provider.Descriptor.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var providerId in request.Definitions.Select(value => value.ProviderId)
                         .Where(value => !string.IsNullOrWhiteSpace(value))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!availableProviders.Contains(providerId)
                    && !diagnostics.Any(value => string.Equals(value.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)))
                {
                    diagnostics.Add(new DiscoveryDiagnostic
                    {
                        Severity = DiscoveryDiagnosticSeverity.Error,
                        Code = "provider-unavailable",
                        Message = $"Discovery provider '{providerId}' is unavailable.",
                        ProviderId = providerId,
                        Category = "provider-composition"
                    });
                }
            }
        }

        var candidates = DiscoveryCandidateMerger.Merge(results);
        if (maxCandidates is { } maximum && candidates.Count > maximum)
        {
            diagnostics.Add(new DiscoveryDiagnostic
            {
                Severity = DiscoveryDiagnosticSeverity.Warning, Code = "discovery-result-budget",
                Message = "Discovery reached its result limit. Choose a narrower root to find additional content."
            });
            candidates = candidates.Take(maximum).ToList();
        }
        return new GameDiscoveryResult
        {
            Candidates = candidates,
            Diagnostics = diagnostics
        };
    }

    private IEnumerable<DiscoveryDiagnostic> RelevantCompositionDiagnostics(DiscoveryRequest request)
    {
        if (request.Mode != DiscoveryRequestMode.PresetTargeted)
        {
            return _compositionDiagnostics;
        }
        var providerIds = request.Definitions.Select(value => value.ProviderId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _compositionDiagnostics.Where(value => providerIds.Contains(value.ProviderId));
    }

    private static async Task<DiscoveryProviderResult> RunProviderAsync(
        IGameDiscoveryProvider provider,
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

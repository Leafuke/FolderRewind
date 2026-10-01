using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed partial class HomePageViewModel
{
    private readonly Dictionary<string, IDisposable> _historySubscriptions = [];
    private long _protectionRequest;
    public ObservableCollection<ProjectSourceAttention> SourceIssues { get; } = [];
    public bool HasSourceIssues => SourceIssues.Count > 0;
    private void OnProtectionSaved() => EnqueueOnUiThread(() => { if (_isActive) TaskObserver.Observe(RefreshProtectionAsync(), nameof(HomePageViewModel)); });
    private async Task RefreshProtectionAsync()
    {
        var request = Interlocked.Increment(ref _protectionRequest);
        var token = _pageLifetime.Token;
        var issues = new List<ProjectSourceAttention>();
        foreach (var config in (Configs ?? []).ToArray())
        {
            try
            {
                if (!_historySubscriptions.ContainsKey(config.Id) && NativeHistoryCoreGateway.TryGetRuntime(new(config.Id), out var runtime) && runtime is not null)
                    _historySubscriptions[config.Id] = runtime.ChangeFeed.Subscribe(_ => OnProtectionSaved());
                var summary = await ProjectProtectionSummaryService.ReadAsync(config, false, token);
                if (!_isActive || token.IsCancellationRequested || request != Volatile.Read(ref _protectionRequest)) return;
                if (Configs?.Contains(config) == true) { config.SummaryText = summary.Text; issues.AddRange(summary.Issues); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch { if (_isActive && Configs?.Contains(config) == true) config.SummaryText = I18n.GetString("HomeProtection_Unconfirmed"); }
        }
        if (!_isActive || token.IsCancellationRequested || request != Volatile.Read(ref _protectionRequest)) return;
        SourceIssues.Clear(); foreach (var issue in issues) SourceIssues.Add(issue);
        OnPropertyChanged(nameof(HasSourceIssues));
        foreach (var id in _historySubscriptions.Keys.Where(id => Configs?.Any(c => c.Id == id) != true).ToArray())
        { _historySubscriptions[id].Dispose(); _historySubscriptions.Remove(id); }
    }
    private void DetachProtection()
    {
        ConfigService.Saved -= OnProtectionSaved;
        foreach (var subscription in _historySubscriptions.Values) subscription.Dispose();
        _historySubscriptions.Clear();
    }
}

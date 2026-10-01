using FolderRewind.History.Application;
using FolderRewind.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed partial class FolderManagerPageViewModel
{
    private IDisposable? _protectionSubscription;
    private CancellationTokenSource? _protectionLifetime;
    private long _protectionRequest;
    private string _protectionSummary = "";
    public string ProtectionSummary { get => _protectionSummary; private set => SetProperty(ref _protectionSummary, value); }
    private void OnProtectionSaved() => EnqueueOnUiThread(() => { if (_isActive) RefreshProtection(); });
    private void RefreshProtection()
    {
        _protectionSubscription?.Dispose(); _protectionSubscription = null;
        _protectionLifetime?.Cancel(); _protectionLifetime?.Dispose(); _protectionLifetime = null;
        var request = ++_protectionRequest;
        ProtectionSummary = "";
        if (!_isActive || CurrentConfig is not { } config) return;
        if (NativeHistoryCoreGateway.TryGetRuntime(new(config.Id), out var runtime) && runtime is not null)
            _protectionSubscription = runtime.ChangeFeed.Subscribe(_ => OnProtectionSaved());
        _protectionLifetime = new();
        TaskObserver.Observe(ReadAsync(_protectionLifetime.Token), nameof(FolderManagerPageViewModel));
        async Task ReadAsync(CancellationToken token)
        {
            try
            {
                var result = await ProjectProtectionSummaryService.ReadAsync(config, true, token);
                if (_isActive && request == _protectionRequest && !token.IsCancellationRequested && ReferenceEquals(config, CurrentConfig))
                    ProtectionSummary = result.Text;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch { if (_isActive && request == _protectionRequest) ProtectionSummary = I18n.GetString("HomeProtection_Unconfirmed"); }
        }
    }
    private void DetachProtection()
    {
        ConfigService.Saved -= OnProtectionSaved;
        _protectionSubscription?.Dispose(); _protectionSubscription = null;
        _protectionLifetime?.Cancel(); _protectionLifetime?.Dispose(); _protectionLifetime = null;
        _protectionRequest++;
    }
}

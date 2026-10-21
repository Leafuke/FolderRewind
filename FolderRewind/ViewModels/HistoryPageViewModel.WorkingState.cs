using CommunityToolkit.Mvvm.Input;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed partial class HistoryPageViewModel
{
    private readonly LatestRequestCoordinator _workingComparisons = new();
    private string _workingComparisonMessage = string.Empty;
    private string _workingComparisonDetails = string.Empty;
    public string WorkingComparisonMessage
    {
        get => _workingComparisonMessage;
        private set { if (SetProperty(ref _workingComparisonMessage, value)) OnPropertyChanged(nameof(HasWorkingComparisonMessage)); }
    }
    public string WorkingComparisonDetails { get => _workingComparisonDetails; private set => SetProperty(ref _workingComparisonDetails, value); }
    public bool HasWorkingComparisonMessage => WorkingComparisonMessage.Length > 0;
    public IRelayCommand CancelWorkingComparisonCommand => _cancelWorkingComparisonCommand ??= new RelayCommand(() => CompareWorkingStateCommand.Cancel());
    private IRelayCommand? _cancelWorkingComparisonCommand;

    private void CancelWorkingComparison()
    {
        _workingComparisons.CancelCurrent();
        WorkingComparisonMessage = string.Empty;
        WorkingComparisonDetails = string.Empty;
    }

    private async Task CompareWorkingStateAsync(CancellationToken token)
    {
        var config = _currentConfig;
        var folder = _currentFolder;
        if (config is null || folder is null || !Guid.TryParse(folder.Id, out var id)) return;
        using var request = _workingComparisons.Begin(token);
        WorkingComparisonMessage = I18n.GetString("History_Compare_Running");
        WorkingComparisonDetails = string.Empty;
        try
        {
            if (!await VerifyPasswordIfRequiredAsync(config, request.Token)) { if (request.IsCurrent) WorkingComparisonMessage = string.Empty; return; }
            var result = await Task.Run(() => NativeHistoryApplicationService.CompareWorkingStateAsync(config, new SourceId(id), request.Token), request.Token);
            if (!request.IsCurrent || !ReferenceEquals(config, _currentConfig) || !ReferenceEquals(folder, _currentFolder)) return;
            WorkingComparisonMessage = I18n.GetString("History_Compare_" + result.Status);
            WorkingComparisonDetails = CloudCommandSecurity.Redact(result.Reason);
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested)
        { if (request.IsCurrent) WorkingComparisonMessage = I18n.GetString("History_Compare_Cancelled"); }
        catch (Exception ex)
        {
            if (!request.IsCurrent) return;
            WorkingComparisonMessage = I18n.GetString("History_Compare_Unknown");
            WorkingComparisonDetails = CloudCommandSecurity.Redact(ex.Message);
        }
    }
}

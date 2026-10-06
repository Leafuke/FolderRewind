using FolderRewind.History.Retention;
using FolderRewind.Services;
using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed class HistoryCleanupDialogViewModel : ViewModelBase
{
    private readonly ConfigSettingsDialogViewModel _settings;
    private CancellationTokenSource? _cancellation;
    private bool _running;
    private string _text = "";
    private string _stage = "";
    public HistoryCleanupDialogViewModel(ConfigSettingsDialogViewModel settings, bool reportOnly)
    {
        _settings = settings;
        ReportOnly = reportOnly;
        Heading = reportOnly ? settings.Config.Name : I18n.Format("Retention_Heading", settings.Config.Name, settings.KeepCount);
        try { ReportText = FormatReport(NativeHistoryApplicationService.ReadCleanupReport(settings.Config.Id)); }
        catch (Exception ex) { ReportText = I18n.GetString("Retention_ReportReadFailed") + "\n" + ex.Message; }
    }
    public bool ReportOnly { get; }
    public string CloseLabel => I18n.GetString(IsRunning ? "Common_Cancel" : "Retention_Close");
    public string Heading { get; }
    public bool CountFirst { get; set; }
    public bool IsRunning { get => _running; private set { _running = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanStart)); OnPropertyChanged(nameof(CanChoosePolicy)); OnPropertyChanged(nameof(CloseLabel)); } }
    public bool CanStart => !ReportOnly && !IsRunning && _settings.KeepCount > 0;
    public bool CanChoosePolicy => !ReportOnly && !IsRunning;
    public string StartLabel => _settings.HasUnsavedChanges ? I18n.GetString("Retention_SaveClean") : I18n.GetString("Retention_Start");
    public string ReportText { get => _text; private set { _text = value; OnPropertyChanged(); } }
    public string Stage { get => _stage; private set { _stage = value; OnPropertyChanged(); } }
    public void Cancel() { _cancellation?.Cancel(); Stage = I18n.GetString("Retention_Canceling"); }
    public async Task RunAsync()
    {
        if (!CanStart) return;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IsRunning = true;
        try
        {
            if (_settings.HasUnsavedChanges)
            {
                Stage = I18n.GetString("Retention_Saving");
                await _settings.SaveForCleanupAsync(cancellation.Token);
                OnPropertyChanged(nameof(StartLabel));
            }
            cancellation.Token.ThrowIfCancellationRequested();
            var id = _settings.Config.Id;
            var revision = _settings.Config.ConfigRevision;
            var progress = new Progress<HistoryChainRewriteProgress>(p => { if (IsRunning) Stage = I18n.GetString("Retention_Stage_" + p.Stage); });
            Stage = I18n.GetString("Retention_Stage_waiting");
            var report = await Task.Run(() => NativeHistoryApplicationService.RunManualCleanupAsync(id, revision,
                CountFirst ? HistoryRetentionBenefitPolicy.CountFirst : HistoryRetentionBenefitPolicy.SpaceFirst, progress, cancellation.Token));
            ReportText = FormatReport(report);
            Stage = report is null ? "" : I18n.GetString("Retention_Status_" + report.Status);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { Stage = I18n.GetString("Retention_Status_Canceled"); }
        catch (Exception ex) { Stage = I18n.GetString("Retention_Status_Incomplete"); ReportText = ex.Message; }
        finally { _cancellation = null; IsRunning = false; }
    }

    private static string FormatReport(HistoryCleanupReport? report)
    {
        if (report is null) return I18n.GetString("Retention_NoReport");
        var text = new StringBuilder();
        text.AppendLine(I18n.GetString("Retention_Status_" + report.Status));
        text.AppendLine(I18n.Format("Retention_ReportHeader", report.StartedAtUtc.ToLocalTime().ToString("g"),
            I18n.GetString("Retention_Trigger_" + report.Trigger), report.KeepCount,
            I18n.GetString("Retention_Policy_" + report.Policy)));
        text.AppendLine(I18n.Format("Retention_Bytes", report.Sources.Sum(s => s.DeletedArchives),
            Bytes(report.Sources.Sum(s => s.CreatedBytes)), Bytes(report.Sources.Sum(s => s.ReclaimedBytes)),
            Bytes(report.Sources.Sum(s => s.ReclaimedBytes - s.CreatedBytes))));
        foreach (var source in report.Sources)
        {
            text.AppendLine().AppendLine(source.Name + " — " + I18n.GetString("Retention_Status_" + source.Status));
            text.AppendLine(I18n.Format("Retention_Counts", Count(source.Before), Count(source.After), Count(source.Recent), Count(source.ExtraProtected)));
            if (report.Sources.Count(s => s.GroupId == source.GroupId) > 1)
                text.AppendLine(I18n.Format("Retention_SharedGroup", string.Join(", ", report.Sources.Where(s => s.GroupId == source.GroupId).Select(s => s.Name))));
            text.AppendLine(I18n.Format("Retention_Bytes", source.DeletedArchives, Bytes(source.CreatedBytes), Bytes(source.ReclaimedBytes), Bytes(source.ReclaimedBytes - source.CreatedBytes)));
            foreach (var issue in source.Issues) AppendIssue(issue);
        }
        foreach (var issue in report.Issues) AppendIssue(issue);
        return text.ToString();
        void AppendIssue(HistoryCleanupIssue issue)
        {
            var key = "Retention_Reason_" + issue.Code;
            var message = I18n.GetString(key);
            text.AppendLine("• " + (string.IsNullOrWhiteSpace(message) || message == key ? I18n.GetString("Retention_Reason_Blocked") : message));
            if (issue.Version is not null) text.AppendLine(I18n.Format("Retention_Version", issue.Version));
            if (!string.IsNullOrEmpty(issue.Detail)) text.AppendLine(issue.Detail);
        }
        static string Count(int? count) => count?.ToString() ?? I18n.GetString("Retention_Unknown");
        static string Bytes(long bytes) => $"{bytes / 1048576d:N2} MB ({bytes:N0} B)";
    }
}

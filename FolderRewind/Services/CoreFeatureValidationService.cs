using FolderRewind.History.Application;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

public sealed class CoreFeatureValidationStepResult
{
    public string Name { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string Details { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }
}

public sealed class CoreFeatureValidationReport
{
    public bool Automatic { get; init; }
    public bool Success { get; set; }
    public string Summary { get; set; } = string.Empty;
    public DateTime StartedAtUtc { get; init; }
    public DateTime FinishedAtUtc { get; set; }
    public IReadOnlyList<CoreFeatureValidationStepResult> Steps { get; set; } = [];
    public TimeSpan Duration => FinishedAtUtc >= StartedAtUtc ? FinishedAtUtc - StartedAtUtc : TimeSpan.Zero;

    public string ToDisplayText()
    {
        var text = new StringBuilder();
        text.AppendLine($"{I18n.GetString("CoreValidation_Report_Result")}: {(Success ? I18n.GetString("CoreValidation_Report_Result_Passed") : I18n.GetString("CoreValidation_Report_Result_Failed"))}");
        text.AppendLine($"{I18n.GetString("CoreValidation_Report_Mode")}: {(Automatic ? I18n.GetString("CoreValidation_Report_Mode_Automatic") : I18n.GetString("CoreValidation_Report_Mode_Manual"))}");
        text.AppendLine($"{I18n.GetString("CoreValidation_Report_Started")}: {UserDisplayFormatter.LongDateTime(StartedAtUtc.ToLocalTime())}");
        text.AppendLine($"{I18n.GetString("CoreValidation_Report_Finished")}: {UserDisplayFormatter.LongDateTime(FinishedAtUtc.ToLocalTime())}");
        text.AppendLine($"{I18n.GetString("CoreValidation_Report_Duration")}: {I18n.Format("CoreValidation_Report_DurationValue", Duration.TotalSeconds)}");
        text.AppendLine($"{I18n.GetString("CoreValidation_Report_Summary")}: {Summary}");
        foreach (var step in Steps)
        {
            text.AppendLine();
            text.AppendLine(I18n.Format(
                "CoreValidation_Report_StepFormat",
                step.Success
                    ? I18n.GetString("CoreValidation_Report_Result_Passed")
                    : I18n.GetString("CoreValidation_Report_Result_Failed"),
                step.Name,
                I18n.Format("CoreValidation_Report_DurationValue", step.Duration.TotalSeconds)));
            if (!string.IsNullOrWhiteSpace(step.Details)) text.AppendLine(step.Details);
        }
        return text.ToString().TrimEnd();
    }
}

public static class CoreFeatureValidationService
{
    private static readonly SemaphoreSlim RunGate = new(1, 1);
    private static int _autoRunQueued;
    public static event Action? StateChanged;
    public static bool IsRunning { get; private set; }
    public static string StatusText { get; private set; } = I18n.GetString("CoreValidation_Status_Idle");
    public static CoreFeatureValidationReport? LastReport { get; private set; }

    public static bool ShouldRunInitialValidation()
    {
        var settings = ConfigService.CurrentConfig?.GlobalSettings;
        var configs = ConfigService.CurrentConfig?.BackupConfigs;
        return settings is not null
               && !settings.HasTriggeredInitialCoreValidation
               && configs is { Count: > 0 };
    }

    public static void TryScheduleInitialValidation()
    {
        if (!ShouldRunInitialValidation() || IsRunning || Interlocked.Exchange(ref _autoRunQueued, 1) != 0)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                if (ShouldRunInitialValidation() && !IsRunning)
                    await RunValidationAsync(automatic: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogService.LogError($"[CoreValidation] {ex.Message}", nameof(CoreFeatureValidationService), ex);
            }
            finally
            {
                Interlocked.Exchange(ref _autoRunQueued, 0);
            }
        });
    }

    public static async Task<CoreFeatureValidationReport> RunValidationAsync(bool automatic = false)
    {
        await RunGate.WaitAsync().ConfigureAwait(false);
        IsRunning = true;
        UpdateStatus(I18n.GetString("CoreValidation_Status_Preparing"));
        try
        {
            var report = await RunValidationInternalAsync(automatic).ConfigureAwait(false);
            LastReport = report;
            await PersistReportAsync(report, automatic);
            UpdateStatus(report.Success
                ? I18n.GetString("CoreValidation_Status_Succeeded")
                : I18n.GetString("CoreValidation_Status_Failed"));
            if (automatic) NotifyAutomaticResult(report);
            return report;
        }
        finally
        {
            IsRunning = false;
            RunGate.Release();
            RaiseStateChanged();
        }
    }

    private static async Task<CoreFeatureValidationReport> RunValidationInternalAsync(bool automatic)
    {
        var started = DateTime.UtcNow;
        var steps = new List<CoreFeatureValidationStepResult>();
        foreach (var config in ConfigService.CurrentConfig?.BackupConfigs ?? [])
        {
            var timer = Stopwatch.StartNew();
            try
            {
                UpdateStatus(I18n.Format("CoreValidation_Status_Step", config.Name));
                var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config).ConfigureAwait(false);
                var packs = await runtime.Repository.ReadAllPacksAsync().ConfigureAwait(false);
                new HistoryRepositoryValidator(new HistoryPackCodec()).Validate(runtime.ConfigId, packs);
                await runtime.EnsureIndexCurrentAsync().ConfigureAwait(false);
                await runtime.RefreshLocalStateHealthAsync().ConfigureAwait(false);
                if (runtime.Health != HistoryRuntimeHealth.Ready)
                    throw new InvalidOperationException(runtime.HealthDiagnostic ?? $"Config {config.Id}: {runtime.Health}");
                steps.Add(new()
                {
                    Name = config.Name,
                    Success = true,
                    Details = $"Validated {packs.Count} immutable Commit Packs and the derived index.",
                    Duration = timer.Elapsed
                });
            }
            catch (Exception ex)
            {
                steps.Add(new()
                {
                    Name = config.Name,
                    Success = false,
                    Details = $"{config.Name} ({config.Id}): {ex.Message}",
                    Duration = timer.Elapsed
                });
            }
        }
        var sevenZip = SevenZipExecutableLocator.Resolve(
            ConfigService.CurrentConfig?.GlobalSettings?.SevenZipPath);
        steps.Add(new()
        {
            Name = "7-Zip runtime",
            Success = !string.IsNullOrWhiteSpace(sevenZip),
            Details = sevenZip ?? I18n.GetString("BackupService_Log_SevenZipNotFound"),
            Duration = TimeSpan.Zero
        });
        var success = steps.All(item => item.Success);
        return new()
        {
            Automatic = automatic,
            StartedAtUtc = started,
            FinishedAtUtc = DateTime.UtcNow,
            Success = success,
            Summary = success
                ? I18n.GetString("CoreValidation_Summary_Passed")
                : steps.First(item => !item.Success).Details,
            Steps = steps
        };
    }

    private static async Task PersistReportAsync(CoreFeatureValidationReport report, bool automatic)
    {
        var saved = await ConfigService.UpdateAndSaveAsync(config =>
        {
            var settings = config.GlobalSettings;
            if (automatic) settings.HasTriggeredInitialCoreValidation = true;
            settings.LastCoreValidationPassed = report.Success;
            settings.LastCoreValidationUtc = report.FinishedAtUtc;
            settings.LastCoreValidationSummary = report.Summary;
        });
        if (!saved.Success) LogService.LogWarning(I18n.Format("Config_SaveFailed", saved.ErrorMessage), nameof(CoreFeatureValidationService));
    }

    private static void NotifyAutomaticResult(CoreFeatureValidationReport report)
    {
        if (report.Success)
            NotificationService.ShowSuccess(report.Summary);
        else
            NotificationService.ShowWarning(report.Summary);
    }

    private static void UpdateStatus(string status)
    {
        StatusText = status;
        RaiseStateChanged();
    }

    private static void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(); } catch { }
    }
}

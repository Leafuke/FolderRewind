using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace FolderRewind.Services;

public enum NotificationSeverity { Informational, Success, Warning, Error }
public enum ToastNotificationLevel { Off = 0, ErrorOnly = 1, ImportantAndAbove = 2, All = 3 }
public enum NotificationImportance { Info, Important, Error }
public enum NotificationCategory { Transient, BusinessResult, Attention, Progress, Announcement, Cancellation, AppUpdate }

/// <summary>Compatibility input only. Never retained by the store or presentation queue.</summary>
public sealed class InAppNotificationRequest
{
    public NotificationCorrelation? Correlation { get; set; }
    public string? RecordId { get; set; }
    public NotificationTarget? Target { get; set; }
    public NotificationCategory? Category { get; set; }
    public NotificationContent? Content { get; set; }
    public ProductMessageReference? Product { get; set; }
    public bool SuppressAlert { get; set; }
    public bool? RestoreReadState { get; set; }
    public string? TechnicalDetails { get; set; }
    public string? FailureCode { get; set; }
    public string? Source { get; set; }
    public bool KeepInCenter { get; set; }
    public string? Details { get; set; }
    public string? ResultKey { get; set; }
    public bool AlertOnce { get; set; }
    // Kept for source compatibility; channel choice now belongs to NotificationPolicy.
    public bool ToastOnlyInBackground { get; set; }
    public bool? CompletionSoundSuccess { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public NotificationSeverity Severity { get; set; }
    public int AutoCloseMs { get; set; } = -1;
    public string? ActionText { get; set; }
    public Action? Action { get; set; }
    public DateTime CreatedTime { get; } = DateTime.Now;
}

internal sealed record NotificationEvent(
    string Id, string Revision, string Title, string Message, string Details, string ResultKey,
    NotificationCategory Category, NotificationSeverity Severity, NotificationImportance Importance,
    NotificationTarget? Target, NotificationCorrelation? Correlation, DateTimeOffset OccurredUtc,
    bool KeepInCenter, bool AlertOnce, int AutoCloseMs, bool? CompletionSoundSuccess,
    string FailureKey, string? ActionText, Action? TransientAction,
    IReadOnlyDictionary<string, string> Arguments, Uri? Logo = null, string? Group = null, bool SystemOnly = false,
    NotificationContent? Content = null, ProductMessageReference? Product = null,
    bool SuppressAlert = false, bool? RestoreReadState = null, string TechnicalDetails = "")
{
    public string GroupKey => Correlation?.Group ?? Id;
    public bool IsPersistent => AutoCloseMs <= 0;
    public bool IsFailure => Importance >= NotificationImportance.Important;

    public static NotificationEvent Capture(InAppNotificationRequest request, NotificationImportance? importance,
        TimeProvider clock, Func<string, string> redact, NotificationTarget? context = null,
        IDictionary<string, string>? arguments = null, Uri? logo = null, string? group = null, bool systemOnly = false, string? recordIdOverride = null)
    {
        string Normalize(string? value, int max) { var text = redact(value ?? ""); return text.Length <= max ? text : text[..max] + "…"; }
        static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        var level = importance ?? (request.Severity == NotificationSeverity.Error ? NotificationImportance.Error
            : request.KeepInCenter && request.Severity == NotificationSeverity.Warning ? NotificationImportance.Important : NotificationImportance.Info);
        var category = request.Category ?? (request.Severity == NotificationSeverity.Error || level >= NotificationImportance.Important
            ? NotificationCategory.Attention : request.KeepInCenter ? NotificationCategory.BusinessResult
            : request.AutoCloseMs == 0 ? NotificationCategory.Announcement : NotificationCategory.Transient);
        var keep = request.KeepInCenter || category is NotificationCategory.BusinessResult or NotificationCategory.Attention or NotificationCategory.Cancellation;
        if (category == NotificationCategory.Progress) keep = false;
        var target = request.Product is null ? request.Target ?? context : request.Target;
        var id = request.RecordId ?? recordIdOverride ?? (target?.OperationId is { } operation ? "operation:" + operation : Guid.NewGuid().ToString("N"));
        if (keep && request.Product is null) target = (target ?? new(NotificationTargetKind.None)) with { OperationId = target?.OperationId ?? id };
        var title = Normalize(request.Title, 512);
        var message = Normalize(request.Message, 2048);
        var details = Normalize(string.IsNullOrWhiteSpace(request.Details) ? request.Message : request.Details, 8192);
        var technical = Normalize(request.TechnicalDetails, 16384);
        var content = request.Content is { } supplied ? supplied with
        { ProjectName = Normalize(supplied.ProjectName, 256), ObjectName = Normalize(supplied.ObjectName, 256), Summary = Normalize(supplied.Summary, 512) } : null;
        var key = Hash($"{category}|{request.Severity}|{level}|{content}|{technical}|" + (request.ResultKey ?? title + "\n" + message + "\n" + details));
        var failure = $"{request.Source ?? category.ToString()}|{target?.ConfigId ?? "global"}|"
            + (request.FailureCode ?? Hash(details));
        return new(id, Guid.NewGuid().ToString("N"), title, message, details, key, category, request.Severity, level,
            target, request.Correlation, request.Correlation?.EventUtc ?? clock.GetUtcNow(), keep, request.AlertOnce,
            request.AutoCloseMs < 0 ? DefaultDuration(request.Severity) : request.AutoCloseMs,
            request.CompletionSoundSuccess, failure, Normalize(request.ActionText, 128),
            request.Action, new ReadOnlyDictionary<string, string>(arguments?.ToDictionary(p => p.Key, p => p.Value)
                ?? new Dictionary<string, string>()), logo, group, systemOnly, content, request.Product,
            request.SuppressAlert, request.RestoreReadState, technical);
    }

    public static int DefaultDuration(NotificationSeverity severity) => severity switch
    { NotificationSeverity.Error => 12000, NotificationSeverity.Warning => 8000, _ => 5000 };
}

internal readonly record struct NotificationEnvironment(bool Enabled, bool Suppressed, bool Foreground,
    ToastNotificationLevel ToastLevel, bool CompletionSoundEnabled, int Generation, bool CenterOpen = false, int PresentationGeneration = 0);
internal readonly record struct NotificationDeliveryPlan(bool Record, bool Banner, bool System, bool Sound, bool MuteSystem);
internal enum NotificationChannel { Banner, System, Sound }
internal enum NotificationSendStatus { Submitted, Unavailable, Failed, Disabled, Canceled }
internal sealed record NotificationBannerRequest(NotificationEvent Event, int Generation, bool QuietUpdate, bool Resume = false, int PresentationGeneration = 0);
internal interface INotificationSystemSender
{
    Task<NotificationSendStatus> SendAsync(NotificationEvent notification, bool muteAudio, Func<bool> canSend);
}
internal interface INotificationSoundSender
{
    Task<NotificationSendStatus> SendAsync(bool success, Func<bool> canSend);
}

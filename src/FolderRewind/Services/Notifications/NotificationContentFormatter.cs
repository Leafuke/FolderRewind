using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace FolderRewind.Services;

internal static class NotificationContentFormatter
{
    public static string Title(NotificationContent? content, string fallback, Func<string, string> text)
    {
        if (content is null || content.Result == NotificationResultKind.Unknown) return fallback;
        var subject = string.IsNullOrWhiteSpace(content.ObjectName) ? content.ProjectName : content.ObjectName;
        var operation = text("NotificationUI_Operation_" + content.Operation);
        var result = text("NotificationUI_Result_" + content.Result);
        return string.Format(CultureInfo.CurrentCulture, text("NotificationUI_TitleFormat"), subject, operation, result).Trim();
    }
    public static string Summary(NotificationContent? content, string fallback, Func<string, string> text, bool includeZero = false)
    {
        if (content is null) return fallback;
        if (content.Result is NotificationResultKind.Canceled or NotificationResultKind.NoChanges)
            return text("NotificationUI_Result_" + content.Result);
        if (content.Statistics is not { } statistics) return string.IsNullOrEmpty(content.Summary) ? fallback : content.Summary;
        var parts = new List<string>();
        void Add(string key, int value)
        {
            if (value > 0 || includeZero) parts.Add(string.Format(CultureInfo.CurrentCulture, text("NotificationUI_Count_" + key), value));
        }
        Add("Created", statistics.Created); Add("Unchanged", statistics.Unchanged);
        Add("Warnings", statistics.Warnings); Add("Failed", statistics.Failed);
        return parts.Count == 0 ? text("NotificationUI_Result_" + content.Result) : string.Join(" · ", parts);
    }
    public static string ActionResource(NotificationTarget? target) => target?.Kind switch
    {
        NotificationTargetKind.AnnouncementDetails => "Product_ViewAnnouncement",
        NotificationTargetKind.ApplicationUpdate => "Product_ViewUpdate",
        NotificationTargetKind.Tasks => "NotificationUI_ViewTasks",
        NotificationTargetKind.History => "NotificationUI_ViewHistory",
        NotificationTargetKind.CloudProject => "NotificationUI_ViewCloud",
        NotificationTargetKind.Configuration => "Product_CheckConfiguration",
        NotificationTargetKind.Logs => "NotificationCenter_OpenLogs.Content",
        _ => "NotificationCenter_OpenTarget.Content"
    };
}

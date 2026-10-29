using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services;

internal static class NotificationNavigationService
{
    private static readonly NotificationActivationQueue Pending = new();
    private static Action? _restoreWindow;
    private static readonly object UnavailableLock = new();
    private static readonly HashSet<string> UnavailableTargets = new(StringComparer.Ordinal);
    private static readonly Queue<string> UnavailableOrder = new();
    private static string TargetKey(NotificationTarget target) => string.Join("|", target.OperationId, target.ConfigId,
        target.FolderId, target.RunId, target.VersionId, target.JobId);
    public static event Action<string>? DetailsRequested;

    public static void Initialize(Action restoreWindow) => _restoreWindow = restoreWindow;
    public static void SetReady(bool ready)
    {
        Pending.SetReady(ready);
        if (ready) Drain();
    }
    public static void RequestDetails(string id) => DetailsRequested?.Invoke(id);

    public static bool IsTargetAvailable(NotificationTarget? target)
    {
        if (target is null || target.Kind == NotificationTargetKind.None) return false;
        lock (UnavailableLock) if (UnavailableTargets.Contains(TargetKey(target))) return false;
        if (target.Kind == NotificationTargetKind.FolderManager) return true;
        if (target.Kind == NotificationTargetKind.Logs) return target.OperationId is not null;
        var config = ConfigService.CurrentConfig?.BackupConfigs.FirstOrDefault(c => c.Id == target.ConfigId);
        if (target.ConfigId is not null && config is null) return false;
        return target.Kind switch
        {
            NotificationTargetKind.Tasks => target.TaskId is null && target.OperationId is null
                || BackupService.ActiveTasks.Any(t => target.TaskId is { } id ? t.TaskId == id : t.OperationId == target.OperationId)
                || config is not null && target.RunId is not null,
            NotificationTargetKind.History => config is not null && (target.FolderId is null || config.SourceFolders.Any(f => f.Id == target.FolderId)),
            NotificationTargetKind.Configuration or NotificationTargetKind.CloudProject => config is not null,
            _ => false
        };
    }

    public static bool Open(NotificationTarget target)
    {
        lock (UnavailableLock) if (UnavailableTargets.Contains(TargetKey(target))) return false;
        if (target.Kind == NotificationTargetKind.FolderManager) return NavigationService.NavigateTo("Manager");
        if (target.Kind == NotificationTargetKind.Logs)
            return target.OperationId is { } operation && NavigationService.NavigateTo("Logs", new LogNavigationParameter(operation));

        var config = target.ConfigId is null ? null
            : ConfigService.CurrentConfig.BackupConfigs.FirstOrDefault(c => c.Id == target.ConfigId);
        if (target.ConfigId is not null && config is null) return Unavailable();
        if (target.Kind == NotificationTargetKind.Configuration)
            return config is null ? Unavailable() : NavigationService.NavigateTo("ConfigSettings", new ConfigSettingsNavigationParameter(config.Id));
        if (target.Kind == NotificationTargetKind.Tasks)
        {
            if (target.TaskId is null && target.OperationId is null) return NavigationService.NavigateTo("Tasks");
            var exists = BackupService.ActiveTasks.Any(t => target.TaskId is { } taskId ? t.TaskId == taskId : t.OperationId == target.OperationId);
            if (exists) return NavigationService.NavigateTo("Tasks", new NotificationNavigationParameter(target));
            if (config is not null && target.RunId is not null)
                return NavigationService.NavigateTo("History", new NotificationNavigationParameter(target with { Kind = NotificationTargetKind.History }));
            return Unavailable();
        }
        if (target.Kind == NotificationTargetKind.History)
        {
            if (config is null) return Unavailable();
            if (target.FolderId is not null && !config.SourceFolders.Any(f => f.Id == target.FolderId)) return Unavailable();
            return NavigationService.NavigateTo("History", new NotificationNavigationParameter(target));
        }
        if (target.Kind == NotificationTargetKind.CloudProject)
        {
            if (config is null) return Unavailable();
            return NavigationService.NavigateTo("CloudProject", new CloudSetupNavigationParameter(config.Id,
                Intent: CloudSetupIntent.ProjectStatus, JobId: target.JobId));
        }
        return Unavailable();
    }

    internal static bool Unavailable(NotificationTarget? target = null)
    {
        if (target is not null)
        {
            var key = TargetKey(target);
            lock (UnavailableLock)
            {
                if (UnavailableTargets.Add(key)) UnavailableOrder.Enqueue(key);
                while (UnavailableOrder.Count > 200) UnavailableTargets.Remove(UnavailableOrder.Dequeue());
            }
            var record = NotificationCenterService.Snapshot().FirstOrDefault(r => r.Target is { } candidate && TargetKey(candidate) == key);
            if (record is not null)
            {
                UiDispatcherService.Post(() => RequestDetails(record.Id));
                return false;
            }
        }
        NotificationService.ShowWarning(I18n.GetString("NotificationCenter_TargetUnavailable"));
        return false;
    }

    public static IDictionary<string, string> ToArguments(string id, NotificationTarget? target)
    {
        var args = new Dictionary<string, string> { ["notification"] = id };
        if (target is null) return args;
        args["kind"] = target.Kind.ToString();
        void Add(string key, string? value) { if (value is not null) args[key] = value; }
        Add("operation", target.OperationId); Add("config", target.ConfigId); Add("folder", target.FolderId);
        Add("task", target.TaskId); Add("run", target.RunId); Add("version", target.VersionId); Add("job", target.JobId);
        return args;
    }

    public static void QueueActivation(IEnumerable<KeyValuePair<string, string>> args)
    {
        Pending.Enqueue(args);
        if (Pending.Ready) UiDispatcherService.Enqueue(Drain);
    }

    private static void Drain()
    {
        while (Pending.TryTake(out var args))
        {
            try
            {
                _restoreWindow?.Invoke();
                NotificationActivationRouter.Route(args, NotificationCenterService.Find, NotificationCenterService.MarkRead,
                    Open, page => NavigationService.NavigateTo(page), RequestDetails, () => Unavailable(), NotificationCenterService.MarkRead);
            }
            catch (Exception error)
            {
                LogService.LogError("Could not open notification target.", nameof(NotificationNavigationService), error);
                Unavailable();
            }
        }
    }
}

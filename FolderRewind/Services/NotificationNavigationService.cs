using FolderRewind.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services;

internal static class NotificationNavigationService
{
    private static readonly ConcurrentQueue<Dictionary<string, string>> Pending = new();
    private static volatile bool _ready;
    private static Action? _restoreWindow;
    public static event Action<string>? DetailsRequested;

    public static void Initialize(Action restoreWindow) => _restoreWindow = restoreWindow;
    public static void SetReady(bool ready)
    {
        _ready = ready;
        if (ready) Drain();
    }
    public static void RequestDetails(string id) => DetailsRequested?.Invoke(id);

    public static bool Open(NotificationTarget target)
    {
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

    internal static bool Unavailable()
    {
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
        Pending.Enqueue(args.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
        if (_ready) UiDispatcherService.Enqueue(Drain);
    }

    private static void Drain()
    {
        if (!_ready) return;
        while (Pending.TryDequeue(out var args))
        {
            try
            {
                _restoreWindow?.Invoke();
                var id = args.GetValueOrDefault("notification");
                var record = id is null ? null : NotificationCenterService.Find(id);
                if (record is not null)
                {
                    NotificationCenterService.MarkRead(record.Id);
                    if (record.Target is null or { Kind: NotificationTargetKind.None } || !Open(record.Target)) RequestDetails(record.Id);
                    continue;
                }
                if (Enum.TryParse<NotificationTargetKind>(args.GetValueOrDefault("kind"), out var kind) && Enum.IsDefined(kind))
                {
                    string? Read(string key) => args.GetValueOrDefault(key) is { Length: > 0 and <= 128 } value ? value : null;
                    Open(new(kind, Read("operation"), Read("config"), Read("folder"), Read("task"), Read("run"), Read("version"), Read("job")));
                }
                else if (args.GetValueOrDefault("target") is { } legacy && legacy is "Home" or "Tasks" or "History" or "Logs")
                    NavigationService.NavigateTo(legacy);
                else Unavailable();
            }
            catch (Exception error)
            {
                LogService.LogError("Could not open notification target.", nameof(NotificationNavigationService), error);
                Unavailable();
            }
        }
    }
}

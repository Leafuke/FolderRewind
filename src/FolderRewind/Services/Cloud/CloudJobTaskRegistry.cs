using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services;

/// <summary>UI-owned lookup; evicting a visible task also ends its retention.</summary>
internal static class CloudJobTaskRegistry
{
    public static BackupTask BeginAttempt(ICollection<BackupTask> tasks, string configId, string jobId, string name)
    {
        var task = GetOrCreate(tasks, configId, jobId, name);
        task.Log = "JobId: " + jobId;
        task.ErrorMessage = string.Empty;
        task.ErrorDiagnostics = [];
        task.WarningDiagnostics = [];
        task.Progress = 0;
        task.IsCompleted = false; task.IsSuccess = false; task.IsIndeterminate = true;
        return task;
    }

    public static BackupTask GetOrCreate(ICollection<BackupTask> tasks, string configId, string jobId, string name)
    {
        var task = tasks.FirstOrDefault(item => string.Equals(item.ConfigId, configId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.JobId, jobId, StringComparison.Ordinal));
        if (task is not null) return task;
        task = new() { FolderName = name, OperationId = jobId, ConfigId = configId, JobId = jobId };
        tasks.Add(task);
        return task;
    }
}

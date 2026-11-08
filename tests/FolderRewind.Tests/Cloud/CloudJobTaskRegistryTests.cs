using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Plugin.Abstractions;
using System.Runtime.CompilerServices;

namespace FolderRewind.Tests;

[TestClass]
public sealed class CloudJobTaskRegistryTests
{
    [TestMethod]
    public void VisibleRetryKeepsTaskIdentityButDifferentProjectDoesNotReuseIt()
    {
        var tasks = new BackupTaskCollection();
        var first = CloudJobTaskRegistry.GetOrCreate(tasks, "project", "job", "first");
        first.IsCompleted = true;
        var retry = CloudJobTaskRegistry.GetOrCreate(tasks, "PROJECT", "job", "retry");
        Assert.AreSame(first, retry);
        Assert.AreEqual(first.TaskId, retry.TaskId);
        Assert.AreNotSame(first, CloudJobTaskRegistry.GetOrCreate(tasks, "other", "job", "other"));
    }

    [TestMethod]
    public void NewAttemptClearsPriorErrorsAndDiagnosticsWithoutChangingTaskIdentity()
    {
        var tasks = new BackupTaskCollection();
        var previous = CloudJobTaskRegistry.GetOrCreate(tasks, "project", "job", "first");
        var diagnostic = new PluginDiagnostic("old.failure", DiagnosticSeverity.Error, "cloud", "test", new Dictionary<string, string>());
        previous.ErrorMessage = "offline"; previous.ErrorDiagnostics = [diagnostic]; previous.WarningDiagnostics = [diagnostic];
        previous.Log = "old attempt"; previous.Progress = 88;
        previous.IsCompleted = true; previous.IsSuccess = true; previous.IsIndeterminate = false;
        Assert.IsFalse(previous.IsProgressIndeterminate);
        var progressNotifications = new List<bool>();
        previous.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(BackupTask.IsProgressIndeterminate)) progressNotifications.Add(previous.IsProgressIndeterminate);
        };
        var retry = CloudJobTaskRegistry.BeginAttempt(tasks, "project", "job", "retry");
        Assert.AreSame(previous, retry);
        Assert.AreEqual(previous.TaskId, retry.TaskId);
        Assert.AreEqual("JobId: job", retry.Log);
        Assert.AreEqual(string.Empty, retry.ErrorMessage);
        Assert.IsEmpty(retry.ErrorDiagnostics); Assert.IsEmpty(retry.WarningDiagnostics);
        Assert.IsFalse(retry.HasDetails); Assert.IsFalse(retry.IsCompleted); Assert.IsFalse(retry.IsSuccess);
        Assert.IsTrue(retry.IsIndeterminate); Assert.AreEqual(0, retry.Progress);
        Assert.IsTrue(retry.IsProgressIndeterminate);
        CollectionAssert.AreEqual(new[] { false, true }, progressNotifications);
        Assert.AreEqual(1, tasks.RunningCount);
    }

    [TestMethod]
    public void EvictedTasksCanBeCollectedAndRetryCreatesANewVisibleTask()
    {
        var tasks = new BackupTaskCollection();
        var (evicted, oldId) = CreateEvictedTasks(tasks);
        Assert.HasCount(200, tasks);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.HasCount(801, evicted);
        Assert.IsTrue(evicted.All(weak => !weak.TryGetTarget(out _)), "Evicted cloud tasks must not remain in a static registry.");
        var retry = CloudJobTaskRegistry.GetOrCreate(tasks, "project", "old-job", "retry");
        Assert.AreNotEqual(oldId, retry.TaskId);
        Assert.AreEqual("old-job", retry.OperationId);
        Assert.AreEqual("old-job", retry.JobId);
        Assert.AreEqual(1, tasks.RunningCount);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<BackupTask>[], string) CreateEvictedTasks(BackupTaskCollection tasks)
    {
        var first = CloudJobTaskRegistry.GetOrCreate(tasks, "project", "old-job", "old");
        first.IsCompleted = true;
        var oldId = first.TaskId;
        var tracked = new List<WeakReference<BackupTask>> { new(first) };
        for (var index = 0; index < 1000; index++)
        {
            var task = CloudJobTaskRegistry.GetOrCreate(tasks, "project", "job-" + index, "completed");
            task.IsCompleted = true;
            tracked.Add(new(task));
        }
        return (tracked.Take(tracked.Count - 200).ToArray(), oldId);
    }
}

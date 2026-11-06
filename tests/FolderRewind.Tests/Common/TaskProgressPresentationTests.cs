using FolderRewind.Models;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
public sealed class TaskProgressPresentationTests
{
    [TestMethod]
    public void CompletionStopsIndeterminatePresentationWithoutChangingTaskEvidenceAndRetryRestoresIt()
    {
        var task = new BackupTask { Progress = 37, ErrorMessage = "failure detail", Log = "retained log" };
        var states = new List<bool>();
        task.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(BackupTask.IsProgressIndeterminate)) states.Add(task.IsProgressIndeterminate);
        };

        Assert.IsTrue(task.IsProgressIndeterminate);
        task.IsCompleted = true;
        Assert.IsFalse(task.IsProgressIndeterminate);
        Assert.IsTrue(task.IsIndeterminate, "Presentation must not rewrite the producer's progress mode.");
        Assert.AreEqual(37d, task.Progress);
        Assert.AreEqual("failure detail", task.ErrorMessage);
        Assert.AreEqual("retained log", task.Log);
        Assert.IsTrue(task.HasDetails);

        task.IsCompleted = false;
        Assert.IsTrue(task.IsProgressIndeterminate);
        CollectionAssert.AreEqual(new[] { false, true }, states);
    }

    [TestMethod]
    public void BothSourcePropertiesNotifyPresentationAndCompletedTasksCannotRestartAnimation()
    {
        var task = new BackupTask();
        var states = new List<bool>();
        task.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(BackupTask.IsProgressIndeterminate)) states.Add(task.IsProgressIndeterminate);
        };

        task.IsIndeterminate = false;
        task.IsCompleted = true;
        task.IsIndeterminate = true;
        Assert.IsFalse(task.IsProgressIndeterminate, "A late progress-mode update must not animate a terminal task.");
        task.IsCompleted = false;
        task.IsCompleted = false;
        task.IsIndeterminate = true;

        CollectionAssert.AreEqual(new[] { false, false, false, true }, states);
    }

    [TestMethod]
    public void PresentationPropertyDoesNotExpandSerializedTaskState()
    {
        var task = new BackupTask { IsCompleted = true };
        var serialized = JsonSerializer.SerializeToElement(task);
        Assert.IsFalse(serialized.TryGetProperty(nameof(BackupTask.IsProgressIndeterminate), out _));
        Assert.IsTrue(serialized.GetProperty(nameof(BackupTask.IsIndeterminate)).GetBoolean());
        Assert.IsTrue(serialized.GetProperty(nameof(BackupTask.IsCompleted)).GetBoolean());
    }
}

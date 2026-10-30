using FolderRewind.Services;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.History.Cloud;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
public sealed class NotificationOrderingTests
{
    [TestMethod]
    public void LateOldCloudSuccessCannotReplaceNewFailureOrOtherProjects()
    {
        var tracker = new LatestNotificationTracker(); var now = DateTimeOffset.UtcNow;
        Assert.IsTrue(tracker.Accept(new("cloud:a", "old", now, now)));
        Assert.IsTrue(tracker.Accept(new("cloud:a", "new", now.AddMinutes(1), now.AddMinutes(2))));
        Assert.IsFalse(tracker.Accept(new("cloud:a", "old", now, now.AddMinutes(3))));
        Assert.IsFalse(tracker.Accept(new("cloud:a", "new", now.AddMinutes(1), now.AddMinutes(1))));
        Assert.IsTrue(tracker.Accept(new("cloud:b", "old", now, now)));
        Assert.IsTrue(tracker.Accept(null));
    }

}

using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class BackupConfigurationHandoffTests
{
    [TestMethod]
    public void CheckpointKeepsTheSavedRegistrationRevisionAndEntireSourceRoster()
    {
        var registration = new object(); var id = Guid.NewGuid().ToString("D");
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var sourceIds = new List<string> { a.ToString("D"), b.ToString("D") };
        var saved = new BackupConfigurationHandoff(registration, id, "saved-revision", sourceIds);
        sourceIds[0] = Guid.NewGuid().ToString("N");
        Assert.IsTrue(saved.Matches(registration, Guid.Parse(id).ToString("N"), "saved-revision", [b.ToString("N"), a.ToString("N")]));
        Assert.IsFalse(saved.Matches(new object(), id, "saved-revision", [a.ToString(), b.ToString()]));
        Assert.IsFalse(saved.Matches(registration, id, "replacement-revision", [a.ToString(), b.ToString()]));
        Assert.IsFalse(saved.Matches(registration, id, "saved-revision", [a.ToString()]));
        Assert.IsFalse(saved.Matches(registration, id, "saved-revision", sourceIds));
        Assert.IsFalse(saved.Matches(registration, id, "saved-revision", [a.ToString(), a.ToString()]));
    }

    [TestMethod]
    public void InvocationModifiersRetainTheSavedHandoff()
    {
        var handoff = new BackupConfigurationHandoff(new object(), Guid.NewGuid().ToString(), "saved", [Guid.NewGuid().ToString()]);
        var options = new BackupInvocationOptions { Source = BackupInvocationSource.Manual, ConfigurationHandoff = handoff };
        foreach (var next in new[] { options.WithComment("checkpoint"), options.WithProtection(true), options.WithApplicationConsistentSnapshot(), options.WithSemanticCheckpoint() })
            Assert.AreSame(handoff, next.ConfigurationHandoff);
    }
}

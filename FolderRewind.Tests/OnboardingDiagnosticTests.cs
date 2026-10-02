using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class OnboardingDiagnosticTests
{
    [TestMethod]
    public void ExternalMessageCannotSelectARepairAction()
    {
        Assert.AreEqual(OnboardingRepairTarget.KnotLink, OnboardingRepairPolicy.Resolve("minecraft.host-connection"));
        Assert.AreEqual(OnboardingRepairTarget.None, OnboardingRepairPolicy.Resolve("https://server.test/run?command=delete"));
        Assert.AreEqual(OnboardingRepairTarget.None, OnboardingRepairPolicy.Resolve("minecraft.host-connection;run"));
    }

    [TestMethod]
    [DataRow("cloud.access-verified", OnboardingRepairTarget.CloudConnection)]
    [DataRow("cloud.copy-incomplete", OnboardingRepairTarget.CloudConnection)]
    [DataRow("cloud.copy-complete", OnboardingRepairTarget.CloudConnection)]
    [DataRow("openlist.endpoint-unconfirmed", OnboardingRepairTarget.OpenListEnvironment)]
    [DataRow("openlist.endpoint-observed", OnboardingRepairTarget.CloudConnection)]
    public void CloudAndOpenListChecksUseOnlyHostRepairTargets(string code, OnboardingRepairTarget target)
        => Assert.AreEqual(target, OnboardingRepairPolicy.Resolve(code));

    [TestMethod]
    public void RepairRejectsDeletedChangedOrDifferentSourceContext()
    {
        var context = new OnboardingRepairContext("project", "world", "revision", DateTimeOffset.UtcNow);
        Assert.IsTrue(OnboardingRepairPolicy.IsCurrent(context, "project", "world", "revision"));
        Assert.IsFalse(OnboardingRepairPolicy.IsCurrent(context, "other", "world", "revision"));
        Assert.IsFalse(OnboardingRepairPolicy.IsCurrent(context, "project", "other-world", "revision"));
        Assert.IsFalse(OnboardingRepairPolicy.IsCurrent(context, "project", "world", "changed"));
    }

    [TestMethod]
    public void SummaryIncludesOnlyKnownFieldsAndIsBounded()
    {
        var observations = Enumerable.Range(0, 30).Select(_ => ("minecraft.host-connection", "NeedsInput", DateTimeOffset.UnixEpoch))
            .Prepend(("password=secret-value", "token=secret-token", DateTimeOffset.UnixEpoch));
        var output = OnboardingRepairPolicy.ExportSummary(observations);
        Assert.HasCount(20, output.Split(Environment.NewLine));
        Assert.IsFalse(output.Contains("secret", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(output, "unknown\tUnknown");
        StringAssert.Contains(output, "minecraft.host-connection\tNeedsInput");
    }
}

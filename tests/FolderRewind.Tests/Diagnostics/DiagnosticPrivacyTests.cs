using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class DiagnosticPrivacyTests
{
    [TestMethod]
    [DataRow("{\"token\":\"raw-secret\"}")]
    [DataRow("https://example.invalid/object?sig=raw-secret&expires=1")]
    [DataRow("Authorization: Bearer raw-secret")]
    public void QuotedAssignmentsAndSignedUrlsAreRedacted(string value)
        => Assert.DoesNotContain("raw-secret", DiagnosticPrivacy.Text(value));

    [TestMethod]
    public void SensitiveExceptionProjectionExcludesBareSecretsIncludingNestedExceptions()
    {
        var error = new AggregateException("outer-secret", new IOException("bare-secret"), new ArgumentException("other-secret"));
        error.Data["password"] = "data-secret";
        var details = DiagnosticPrivacy.ExceptionMetadata(error);
        StringAssert.Contains(details, "System.IO.IOException");
        StringAssert.Contains(details, "hresult=0x");
        Assert.DoesNotContain("secret", details);
        var safe = DiagnosticPrivacy.SensitiveFailure("openlist.initialization-failed", "initialize", error);
        Assert.IsNull(safe.InnerException);
        Assert.DoesNotContain("secret", safe.ToString());
        Assert.DoesNotContain("secret", safe.Data["SafeDiagnostic"]!.ToString()!);
    }

    [TestMethod]
    public void PluginTechnicalDetailsOmitUntrustedArgumentsWithoutChangingBusinessObject()
    {
        var arguments = new Dictionary<string, string> { ["message"] = "bare-secret", ["password"] = "other-secret", ["hresult"] = "0x80070005" };
        var diagnostic = new PluginDiagnostic("plugin.test", DiagnosticSeverity.Error, "Capture", "plugin.test", arguments);
        var text = OperationDiagnosticFormatter.Technical(diagnostic);
        Assert.DoesNotContain("bare-secret", text);
        Assert.DoesNotContain("other-secret", text);
        StringAssert.Contains(text, "0x80070005");
        Assert.AreEqual("bare-secret", arguments["message"]);
    }

    [TestMethod]
    public void DiagnosticChildrenPreserveNotificationStateButInternalOperationsHaveTheirOwnState()
    {
        using var parent = new NotificationOperationScope(new(NotificationTargetKind.Tasks, "parent", "config", "root-source"));
        using (var child = NotificationOperationScope.EnterChild(c => c with { SourceId = "child-source" }))
        {
            child!.Target = child.Target with { RunId = "run" };
            child.ResultReported = true;
            Assert.AreEqual("child-source", child.Capture().SourceId);
        }
        Assert.IsTrue(parent.ResultReported);
        Assert.AreEqual("run", parent.Capture().RunId);
        Assert.AreEqual("root-source", parent.Capture().SourceId);
        using (var snapshot = new NotificationOperationScope(new(NotificationTargetKind.Tasks, "parent"), true))
            Assert.IsFalse(snapshot.ResultReported);
        Assert.IsTrue(parent.ResultReported);
    }

    [TestMethod]
    public async Task NestedAndParallelScopesRestoreParentsAndKeepSourceIdentity()
    {
        using var parent = new NotificationOperationScope(new(NotificationTargetKind.Tasks, "parent", "config"), context: new("parent", "Backup"));
        var sources = await Task.WhenAll(new[] { "a", "b" }.Select(async source =>
        {
            using var child = NotificationOperationScope.EnterChild(c => c with { SourceId = source });
            await Task.Yield();
            return NotificationOperationScope.Current!.Capture();
        }));
        CollectionAssert.AreEquivalent(new[] { "a", "b" }, sources.Select(c => c.SourceId).ToArray());
        Assert.IsTrue(sources.All(c => c.OperationId == "parent"));
        Assert.AreSame(parent, NotificationOperationScope.Current);
        Assert.IsNull(parent.Capture().SourceId);
        try { using var child = new NotificationOperationScope(new(NotificationTargetKind.History, "child")); await Task.Yield(); throw new OperationCanceledException(); }
        catch (OperationCanceledException) { }
        Assert.AreSame(parent, NotificationOperationScope.Current);
    }
}

using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class CloudCommandSecurityTests
{
    [TestMethod]
    public void Redact_RemovesOptionsUrlsHeadersAndAssignments()
    {
        var text = """
            powershell.exe -File "sync wrapper.ps1" --password "p@ss word" --token=token-value
            endpoint=https://alice:url-secret@example.test/path?access_token=query-secret
            Authorization: Bearer bearer-secret
            X-Api-Key: header-secret
            client_secret='quoted-secret'
            refresh_token = line-secret
            """;

        var redacted = CloudCommandSecurity.Redact(text);

        foreach (var secret in new[]
        {
            "p@ss word",
            "token-value",
            "alice",
            "url-secret",
            "query-secret",
            "bearer-secret",
            "header-secret",
            "quoted-secret",
            "line-secret"
        })
        {
            Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        }

        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
        Assert.Contains("https://[REDACTED]@example.test", redacted, StringComparison.Ordinal);
        Assert.Contains("Authorization: [REDACTED]", redacted, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Redact_HandlesQuotedAndMultilineScriptArguments()
    {
        var text = "cmd.exe /c \"upload.cmd /password:line-one-secret\r\n--api-key 'line-two-secret'\"";

        var redacted = CloudCommandSecurity.Redact(text);

        Assert.DoesNotContain("line-one-secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("line-two-secret", redacted, StringComparison.Ordinal);
        Assert.AreEqual(2, redacted.Split("[REDACTED]", StringSplitOptions.None).Length - 1);
    }

    [TestMethod]
    public void BuildPreviews_KeepsDisplayAndExecutionArgumentsButOmitsThemFromLog()
    {
        const string executable = @"C:\Tools\rclone.exe";
        const string arguments = "copyto source remote --password actual-secret";

        var previews = CloudCommandSecurity.BuildPreviews(executable, arguments, "transfer");

        Assert.AreEqual($"{executable} {arguments}", previews.DisplayPreview);
        Assert.AreEqual("rclone.exe [operation=transfer; arguments omitted]", previews.LogPreview);
        Assert.DoesNotContain("actual-secret", previews.LogPreview, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Tools", previews.LogPreview, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    [DataRow("copyto source destination", "transfer")]
    [DataRow("lsf remote:path", "list")]
    [DataRow("deletefile remote:file", "delete")]
    [DataRow("mkdir remote:path", "create-directory")]
    [DataRow("run-custom --flag", "custom")]
    public void DetectOperationCategory_ReturnsAllowlistedCategory(string arguments, string expected)
    {
        Assert.AreEqual(expected, CloudCommandSecurity.DetectOperationCategory(arguments));
    }

    [TestMethod]
    public void BuildLogPreview_NormalizesUntrustedCategoryAndExecutableWhitespace()
    {
        var preview = CloudCommandSecurity.BuildLogPreview(
            "C:\\Tools\\rclone.exe\r\n--token leaked",
            "transfer\r\nsecret");

        Assert.AreEqual("rclone.exe [operation=custom; arguments omitted]", preview);
        Assert.DoesNotContain("leaked", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", preview, StringComparison.Ordinal);
    }
}

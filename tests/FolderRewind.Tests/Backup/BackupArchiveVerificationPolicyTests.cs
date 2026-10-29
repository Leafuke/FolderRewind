using FolderRewind.Models;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class BackupArchiveVerificationPolicyTests
{
    [TestMethod]
    [DataRow(BackupArchiveVerificationLevel.Balanced, true, true, true, 0)]
    [DataRow(BackupArchiveVerificationLevel.Strict, true, true, true, 1)]
    [DataRow(BackupArchiveVerificationLevel.Strict, true, false, false, 1)]
    [DataRow(BackupArchiveVerificationLevel.Balanced, false, true, false, 0)]
    [DataRow(BackupArchiveVerificationLevel.Strict, false, true, false, 0)]
    public async Task LogicalMismatchAndTestFailureCannotBeAdmitted(BackupArchiveVerificationLevel level,
        bool logicalValid, bool contentValid, bool accepted, int testCount)
    {
        int listings = 0, tests = 0;
        var result = await BackupArchiveVerificationPolicy.VerifyFinalAsync(level,
            () => { listings++; return Task.FromResult(logicalValid); },
            () => { tests++; return Task.FromResult(contentValid); });
        Assert.AreEqual(accepted, result); Assert.AreEqual(1, listings); Assert.AreEqual(testCount, tests);
    }
}

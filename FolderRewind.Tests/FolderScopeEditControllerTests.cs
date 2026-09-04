using FolderRewind.Models;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class FolderScopeEditControllerTests
{
    [TestMethod]
    public async Task BroadExpansionRequiresBothConfirmations()
    {
        var current = new BackupSourceScope { Mode = BackupSourceScopeMode.Include };
        var after = new BackupSourceScope { Mode = BackupSourceScopeMode.All };
        var confirmations = new List<bool>();
        var result = await FolderScopeEditController.ApplyAsync(current, after, true,
            (broad, _) => { confirmations.Add(broad); return Task.FromResult(true); },
            value => current = value, () => Task.FromResult(new ConfigSaveResult { Success = true }), "failed", default);
        Assert.IsTrue(result);
        CollectionAssert.AreEqual(new[] { false, true }, confirmations);
        Assert.AreSame(after, current);
    }

    [TestMethod]
    public async Task DeclinedBroadConfirmationDoesNotMutateOrSave()
    {
        var before = new BackupSourceScope { Mode = BackupSourceScopeMode.Include };
        var current = before;
        var result = await FolderScopeEditController.ApplyAsync(before, new() { Mode = BackupSourceScopeMode.All }, true,
            (broad, _) => Task.FromResult(!broad), value => current = value,
            () => throw new Exception("must not save"), "failed", default);
        Assert.IsFalse(result);
        Assert.AreSame(before, current);
    }

    [TestMethod]
    public async Task FailedSaveRestoresOriginalScope()
    {
        var before = new BackupSourceScope();
        var current = before;
        var saves = 0;
        await Assert.ThrowsExactlyAsync<IOException>(() => FolderScopeEditController.ApplyAsync(before,
            new() { Mode = BackupSourceScopeMode.Include }, false, (_, _) => throw new Exception("not expanding"),
            value => current = value, () => Task.FromResult(new ConfigSaveResult { Success = ++saves > 1 }), "failed", default));
        Assert.AreSame(before, current);
        Assert.AreEqual(2, saves);
    }

    [TestMethod]
    public async Task CancellationDuringConfirmationPreventsMutation()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => FolderScopeEditController.ApplyAsync(
            new() { Mode = BackupSourceScopeMode.Include }, new() { Mode = BackupSourceScopeMode.All }, false,
            (_, _) => { cancellation.Cancel(); return Task.FromResult(true); }, _ => Assert.Fail(),
            () => throw new Exception("must not save"), "failed", cancellation.Token));
    }
}

using FolderRewind.Models;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class BackupSourceFileEnumeratorTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "FolderRewindSourceEnumeratorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "Saves", "nested"));
        Directory.CreateDirectory(Path.Combine(_root, "Config"));
        File.WriteAllText(Path.Combine(_root, "Saves", "slot1.sav"), "one");
        File.WriteAllText(Path.Combine(_root, "Saves", "nested", "slot2.SAV"), "two");
        File.WriteAllText(Path.Combine(_root, "Config", "settings.json"), "{}");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public void AllSelectionKeepsLegacyWholeDirectoryBehavior()
    {
        var files = BackupSourceFileEnumerator.Enumerate(_root, new BackupSourceScope());

        Assert.HasCount(3, files);
        CollectionAssert.AreEquivalent(
            new[] { "Saves/slot1.sav", "Saves/nested/slot2.SAV", "Config/settings.json" },
            files.Select(file => file.RelativePath).ToArray());
    }

    [TestMethod]
    public void IncludeSelectionIsMaximumBoundaryAndFilterCanOnlyShrinkIt()
    {
        var selection = new BackupSourceScope
        {
            Mode = BackupSourceScopeMode.Include,
            IncludePatterns = new() { "Saves/**/*.sav" }
        };

        var selected = BackupSourceFileEnumerator.Enumerate(_root, selection);
        var filtered = BackupSourceFileEnumerator.Enumerate(
            _root,
            selection,
            fullPath => !fullPath.Contains("nested", StringComparison.OrdinalIgnoreCase));

        Assert.HasCount(2, selected);
        Assert.HasCount(1, filtered);
        Assert.AreEqual("Saves/slot1.sav", filtered[0].RelativePath);
        Assert.IsFalse(selected.Any(file => file.RelativePath.StartsWith("Config/", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void IncludeSelectionAutomaticallyFindsFutureMatchingSlots()
    {
        var selection = new BackupSourceScope
        {
            Mode = BackupSourceScopeMode.Include,
            IncludePatterns = new() { "Saves/*.sav" }
        };
        Assert.HasCount(1, BackupSourceFileEnumerator.Enumerate(_root, selection));

        File.WriteAllText(Path.Combine(_root, "Saves", "slot3.sav"), "three");

        Assert.HasCount(2, BackupSourceFileEnumerator.Enumerate(_root, selection));
    }

    [TestMethod]
    public void IncludeSelectionRejectsAbsoluteTraversalAndEmptyPatterns()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => BackupSourceFileEnumerator.ValidateAndNormalize(
            new BackupSourceScope { Mode = BackupSourceScopeMode.Include }));
        Assert.ThrowsExactly<InvalidDataException>(() => BackupSourceFileEnumerator.ValidateAndNormalize(
            new BackupSourceScope
            {
                Mode = BackupSourceScopeMode.Include,
                IncludePatterns = new() { "../outside/*.sav" }
            }));
        Assert.ThrowsExactly<InvalidDataException>(() => BackupSourceFileEnumerator.ValidateAndNormalize(
            new BackupSourceScope
            {
                Mode = BackupSourceScopeMode.Include,
                IncludePatterns = new() { Path.Combine(_root, "*.sav") }
            }));
    }

    [TestMethod]
    public void IncludeSelectionRejectsExcessivePatternCountAndLength()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => BackupSourceFileEnumerator.ValidateAndNormalize(
            new BackupSourceScope
            {
                Mode = BackupSourceScopeMode.Include,
                IncludePatterns = new(Enumerable.Range(0, BackupSourceScopePatternSet.MaximumPatternCount + 1)
                    .Select(index => $"Saves/{index}.sav"))
            }));
        Assert.ThrowsExactly<InvalidDataException>(() => BackupSourceFileEnumerator.ValidateAndNormalize(
            new BackupSourceScope
            {
                Mode = BackupSourceScopeMode.Include,
                IncludePatterns = new() { new string('a', BackupSourceScopePatternSet.MaximumPatternLength + 1) }
            }));
    }

    [TestMethod]
    public void EnumerationHonorsCancellationBeforeWalkingSource()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() => BackupSourceFileEnumerator.Enumerate(
            _root,
            new BackupSourceScope(),
            cancellationToken: cancellation.Token));
    }
}

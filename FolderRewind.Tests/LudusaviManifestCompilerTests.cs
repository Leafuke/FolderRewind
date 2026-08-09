using FolderRewind.Models;
using FolderRewind.Services.Discovery;
using System.Text;

namespace FolderRewind.Tests;

[TestClass]
public sealed class LudusaviManifestCompilerTests
{
    [TestMethod]
    public void CompilerKeepsWindowsFilesAndVisibleRegistryMetadata()
    {
        using var manifest = StreamOf(
            """
            Hades:
              installDir:
                Hades: {}
                Hades Demo: {}
              files:
                '<home>/Saved Games/Hades/*.sav':
                  tags: [save]
                  when:
                    - os: windows
                      store: steam
                '<home>/.config/hades':
                  when:
                    - os: linux
              registry:
                'HKEY_CURRENT_USER/Software/Hades':
                  tags: [config]
              steam:
                id: 1145360
              id:
                steamExtra: [1145361]
                gogExtra: [123456]
              cloud:
                steam: true
            Hades Game:
              alias: Hades
            """);

        var index = new LudusaviManifestCompiler().Compile(
            manifest,
            null,
            null,
            "source",
            CancellationToken.None);

        Assert.HasCount(1, index.Games);
        var game = index.Games[0];
        Assert.AreEqual("1145360", game.ExternalIds["steam"]);
        Assert.AreEqual("1145361", game.ExternalIds["steamExtra"]);
        Assert.AreEqual("123456", game.ExternalIds["gogExtra"]);
        CollectionAssert.AreEquivalent(
            new[] { "Hades", "Hades Demo" },
            game.InstallDirectoryHints.ToArray());
        Assert.HasCount(1, game.Files);
        Assert.HasCount(1, game.Registry);
        Assert.AreEqual(BackupResourceKind.Registry, game.Registry[0].Kind);
        Assert.AreEqual("true", game.NativeCloud["steam"]);
        CollectionAssert.Contains(game.Aliases.ToList(), "Hades Game");
        Assert.AreEqual(3, index.SchemaVersion);
    }

    [TestMethod]
    public void CompilerResolvesAliasChainsAndDiagnosesInvalidAliases()
    {
        using var manifest = StreamOf(
            """
            Canonical:
              files:
                '<home>/Canonical/save.dat': {}
            Nickname:
              alias: Canonical
            Older Nickname:
              alias: Nickname
            Cycle A:
              alias: Cycle B
            Cycle B:
              alias: Cycle A
            Missing:
              alias: Does Not Exist
            Empty:
              alias: ''
            """);

        var index = new LudusaviManifestCompiler().Compile(
            manifest,
            null,
            null,
            "source",
            CancellationToken.None);

        Assert.HasCount(1, index.Games);
        CollectionAssert.AreEquivalent(
            new[] { "Nickname", "Older Nickname" },
            index.Games[0].Aliases.ToArray());
        Assert.IsTrue(index.Diagnostics.Any(item => item.Code == "alias-cycle"));
        Assert.IsTrue(index.Diagnostics.Any(item => item.Code == "alias-missing-target"));
        Assert.IsTrue(index.Diagnostics.Any(item => item.Code == "alias-empty-target"));
    }

    [TestMethod]
    public void SecondaryManifestMergesBeforeFolderRewindOverride()
    {
        using var primary = StreamOf(
            """
            Game:
              files:
                '<home>/Game/old.sav':
                  tags: [save]
            """);
        using var secondary = StreamOf(
            """
            Game:
              files:
                '<home>/Game/extra.cfg':
                  tags: [config]
            """);
        var overrides = new FolderRewindGameOverrideDocument
        {
            Entries = new[]
            {
                new FolderRewindGameOverrideEntry
                {
                    DefinitionId = "Game",
                    Operations = new[]
                    {
                        new FolderRewindGameOverrideOperation
                        {
                            Action = FolderRewindGameOverrideAction.Disable,
                            Kind = BackupResourceKind.FileSet,
                            Expression = "<home>/Game/old.sav"
                        },
                        new FolderRewindGameOverrideOperation
                        {
                            Action = FolderRewindGameOverrideAction.Add,
                            Kind = BackupResourceKind.FileSet,
                            Expression = "<home>/Game/new.sav",
                            Tags = new[] { "save" }
                        }
                    }
                }
            }
        };

        var index = new LudusaviManifestCompiler().Compile(
            primary,
            secondary,
            overrides,
            "source",
            CancellationToken.None);

        var files = index.Games[0].Files;
        Assert.HasCount(3, files);
        Assert.IsTrue(files.Single(item => item.Expression.EndsWith("old.sav", StringComparison.Ordinal)).IsDisabled);
        Assert.IsTrue(files.Any(item => item.Expression.EndsWith("extra.cfg", StringComparison.Ordinal)));
        Assert.IsTrue(files.Any(item => item.Expression.EndsWith("new.sav", StringComparison.Ordinal)));
    }

    private static MemoryStream StreamOf(string value) =>
        new(Encoding.UTF8.GetBytes(value), writable: false);
}

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
              alias:
                - Hades Game
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
              cloud:
                steam: true
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
        Assert.HasCount(1, game.Files);
        Assert.HasCount(1, game.Registry);
        Assert.AreEqual(BackupResourceKind.Registry, game.Registry[0].Kind);
        Assert.AreEqual("true", game.NativeCloud["steam"]);
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

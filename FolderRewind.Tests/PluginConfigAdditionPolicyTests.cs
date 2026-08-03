using FolderRewind.Services.Plugins;

namespace FolderRewind.Tests;

[TestClass]
public sealed class PluginConfigAdditionPolicyTests
{
    [TestMethod]
    public void ResolveUniqueNameUsesRequestedNameWhenAvailable()
    {
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string name = PluginConfigAdditionPolicy.ResolveUniqueName(
            "Minecraft - Default",
            usedNames,
            usedDestinations,
            candidate => Path.Combine("backup", candidate),
            out string destination);

        Assert.AreEqual("Minecraft - Default", name);
        Assert.AreEqual(Path.Combine("backup", "Minecraft - Default"), destination);
    }

    [TestMethod]
    public void ResolveUniqueNameSkipsNameAndDestinationConflicts()
    {
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Minecraft - Pack"
        };
        var usedDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            PluginConfigAdditionPolicy.NormalizePath(Path.Combine("backup", "Minecraft - Pack (2)"))
        };

        string name = PluginConfigAdditionPolicy.ResolveUniqueName(
            "Minecraft - Pack",
            usedNames,
            usedDestinations,
            candidate => Path.Combine("backup", candidate),
            out string destination);

        Assert.AreEqual("Minecraft - Pack (3)", name);
        Assert.AreEqual(Path.Combine("backup", "Minecraft - Pack (3)"), destination);
    }

    [TestMethod]
    public void NormalizePathCollapsesRelativeSegmentsAndTrailingSeparator()
    {
        string input = Path.Combine("root", "child", "..", "world") + Path.DirectorySeparatorChar;
        string expected = Path.GetFullPath(Path.Combine("root", "world"));

        Assert.AreEqual(expected, PluginConfigAdditionPolicy.NormalizePath(input));
    }
}

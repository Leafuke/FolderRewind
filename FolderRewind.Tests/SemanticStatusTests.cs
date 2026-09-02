using FolderRewind.Models;

namespace FolderRewind.Tests;

[TestClass]
public sealed class SemanticStatusTests
{
    [TestMethod]
    public void EverySemanticStatusHasAPresentationGlyph()
    {
        foreach (var status in Enum.GetValues<SemanticStatus>())
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(SemanticStatusGlyphs.GetGlyph(status)));
        }
    }

    [TestMethod]
    public void WarningAndErrorUseDistinctGlyphs()
    {
        Assert.AreNotEqual(
            SemanticStatusGlyphs.GetGlyph(SemanticStatus.Warning),
            SemanticStatusGlyphs.GetGlyph(SemanticStatus.Error));
    }
}

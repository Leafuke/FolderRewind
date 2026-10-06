namespace FolderRewind.Models;

public enum SemanticStatus
{
    Neutral,
    Info,
    Success,
    Warning,
    Error
}

public static class SemanticStatusGlyphs
{
    public static string GetGlyph(SemanticStatus status) => status switch
    {
        SemanticStatus.Success => "\uE73E",
        SemanticStatus.Warning => "\uE7BA",
        SemanticStatus.Error => "\uE783",
        _ => "\uE946"
    };
}

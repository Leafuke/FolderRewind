using System;
using System.IO;
using System.Text.Json;

namespace FolderRewind.Services;

internal sealed record MergeViewState(double ListWidth = 270, string Filter = "All", string Search = "",
    string? SelectedId = null, double ScrollOffset = 0, bool ShowBase = false);
internal static class MergeViewStateStore
{
    private static string PathFor(string root, Guid id) => Path.Combine(root, "merge-view-state", id.ToString("N") + ".json");
    internal static MergeViewState Load(string root, Guid id)
    {
        try
        {
            var value = JsonSerializer.Deserialize<MergeViewState>(File.ReadAllText(PathFor(root, id))) ?? new();
            return value with { Search = value.Search ?? "", Filter = value.Filter is "All" or "Unresolved" or "Resolved" or "Automatic" ? value.Filter : "All",
                ListWidth = double.IsFinite(value.ListWidth) ? Math.Clamp(value.ListWidth, 200, 420) : 270,
                ScrollOffset = double.IsFinite(value.ScrollOffset) ? Math.Max(0, value.ScrollOffset) : 0 };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    internal static void Save(string root, Guid id, MergeViewState state)
        => AtomicFileService.Write(PathFor(root, id), stream => JsonSerializer.Serialize(stream, state));
}

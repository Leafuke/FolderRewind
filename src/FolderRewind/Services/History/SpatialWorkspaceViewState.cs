using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace FolderRewind.Services;

internal sealed record SpatialWorkspaceViewState(string? Left = null, string? Right = null, string? World = null,
    string? Layer = null, string? Mode = null, double Height = 64, double X = 0, double Y = 0, double Scale = 0,
    string Search = "", string Filter = "", string Category = "$default")
{
    public int PresentationVersion { get; init; }
    public Dictionary<string, SpatialCameraState> Cameras { get; init; } = new();
}
internal sealed record SpatialCameraState(double X, double Y, double Scale);
internal static class SpatialWorkspaceViewStateStore
{
    internal static SpatialWorkspaceViewState Load(string path)
    {
        try
        {
            var state = JsonSerializer.Deserialize<SpatialWorkspaceViewState>(File.ReadAllText(path)) ?? new();
            if (state.Cameras is null || state.Cameras.Count > 1024 || state.Cameras.Any(p => p.Key.Length > 8192 || p.Value is null || !double.IsFinite(p.Value.Scale) || Math.Abs(p.Value.X) > 1e9 || Math.Abs(p.Value.Y) > 1e9 || !double.IsFinite(p.Value.X) || !double.IsFinite(p.Value.Y) || p.Value.Scale is < 1d/4096 or > 16)) return new();
            return double.IsFinite(state.X) && double.IsFinite(state.Y) && double.IsFinite(state.Height)
                && state.Scale is >= 0 and <= 16 && Math.Abs(state.X) <= 1e9 && Math.Abs(state.Y) <= 1e9 ? state : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    internal static void Save(string path, SpatialWorkspaceViewState state)
        => AtomicFileService.Write(path, stream => JsonSerializer.Serialize(stream, state));
}

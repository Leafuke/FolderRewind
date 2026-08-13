using System.Text.Json;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Packaging;

public sealed record LegacyPluginQuarantineResult(
    bool Quarantined,
    string QuarantinePath,
    IReadOnlyList<string> Entries);

public static class LegacyPluginQuarantineService
{
    private static readonly HashSet<string> V3Entries = new(StringComparer.OrdinalIgnoreCase)
    {
        "versions",
        "install-state.v1.json"
    };

    public static async ValueTask<LegacyPluginQuarantineResult> QuarantineFlatPayloadAsync(
        PluginId pluginId,
        string pluginRoot,
        string quarantineBase,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(pluginRoot);
        var basePath = Path.GetFullPath(quarantineBase);
        if (!Directory.Exists(root)) return new LegacyPluginQuarantineResult(false, string.Empty, Array.Empty<string>());
        var entries = Directory.EnumerateFileSystemEntries(root)
            .Where(path => !V3Entries.Contains(Path.GetFileName(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (entries.Length == 0) return new LegacyPluginQuarantineResult(false, string.Empty, Array.Empty<string>());
        var quarantine = Path.Combine(
            basePath,
            pluginId.Value,
            DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(quarantine);
        var moved = new List<string>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(entry);
            var destination = Path.Combine(quarantine, name);
            if (Directory.Exists(entry)) Directory.Move(entry, destination);
            else File.Move(entry, destination);
            moved.Add(name);
        }
        var receipt = new
        {
            schemaVersion = 1,
            pluginId = pluginId.Value,
            quarantinedAtUtc = DateTimeOffset.UtcNow,
            originalRoot = root,
            entries = moved,
            executable = false
        };
        await File.WriteAllTextAsync(
            Path.Combine(quarantine, "quarantine-receipt.json"),
            JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken).ConfigureAwait(false);
        return new LegacyPluginQuarantineResult(true, quarantine, moved);
    }
}

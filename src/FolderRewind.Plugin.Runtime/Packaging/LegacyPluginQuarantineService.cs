using System.Text.Json;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Packaging;

public sealed record LegacyPluginQuarantineResult(
    bool Quarantined,
    string QuarantinePath,
    IReadOnlyList<string> Entries);

public sealed class LegacyPluginQuarantineRecoveryException : IOException
{
    public LegacyPluginQuarantineRecoveryException(string quarantinePath, Exception innerException)
        : base(
            "Legacy payload quarantine failed and one or more entries could not be restored. "
            + "The quarantine directory was preserved for recovery.",
            innerException)
    {
        QuarantinePath = quarantinePath;
    }

    public string QuarantinePath { get; }
}

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
        try
        {
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
        catch (Exception operationError)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var name in moved.AsEnumerable().Reverse())
            {
                try
                {
                    var source = Path.Combine(quarantine, name);
                    var destination = Path.Combine(root, name);
                    if (Directory.Exists(source)) Directory.Move(source, destination);
                    else if (File.Exists(source)) File.Move(source, destination);
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add(rollbackError);
                }
            }
            if (rollbackErrors.Count == 0)
            {
                try
                {
                    if (Directory.Exists(quarantine)) Directory.Delete(quarantine, recursive: true);
                }
                catch
                {
                }
            }

            if (rollbackErrors.Count > 0)
            {
                throw new LegacyPluginQuarantineRecoveryException(
                    quarantine,
                    new AggregateException(new[] { operationError }.Concat(rollbackErrors)));
            }
            throw;
        }
    }
}

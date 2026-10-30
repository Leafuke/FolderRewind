using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FolderRewind.Services;

/// <summary>The shipped preset selects an exact package; leftover older bundles are not upgrade candidates.</summary>
internal static class BundledMineRewindPackage
{
    internal static string Resolve(string applicationDirectory)
    {
        var root = Path.GetFullPath(applicationDirectory);
        const string presetName = "minecraft-enhanced-experience.v1.json";
        var preset = new[] { Path.Combine(root, "Assets", "PluginPresets", presetName), Path.Combine(root, presetName) }.FirstOrDefault(File.Exists);
        if (preset is not null)
        {
            if (new FileInfo(preset).Length > 1024 * 1024) throw new InvalidDataException("Bundled preset is too large.");
            using var json = JsonDocument.Parse(File.ReadAllBytes(preset), new JsonDocumentOptions { MaxDepth = 32 });
            var action = json.RootElement.GetProperty("actions").EnumerateArray().Single(a => a.GetProperty("id").GetString() == "install-minerewind");
            var relative = action.GetProperty("packagePath").GetString() ?? "";
            var segments = relative.Replace('\\', '/').Split('/');
            if (Path.IsPathRooted(relative) || segments.Length != 3 || segments[0] != "Assets" || segments[1] != "Plugins"
                || !segments[2].StartsWith("MineRewind-", StringComparison.Ordinal) || !segments[2].EndsWith(".frplugin", StringComparison.Ordinal)
                || segments.Any(s => s is "" or "." or ".." || s.Contains(':')))
                throw new InvalidDataException("Bundled MineRewind preset path is invalid.");
            var selected = Path.Combine(root, "Assets", "Plugins", segments[2]);
            if (File.Exists(selected)) return selected;
            // MSIX loose layouts may flatten content. The exact preset filename still wins.
            var flattened = Path.Combine(root, segments[2]);
            if (File.Exists(flattened)) return flattened;
            throw new FileNotFoundException("The MineRewind package selected by the shipped preset is missing.", selected);
        }
        var candidates = new[] { Path.Combine(root, "Assets", "Plugins"), root }.Where(Directory.Exists)
            .SelectMany(d => Directory.GetFiles(d, "MineRewind-*.frplugin")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return candidates.Length switch
        {
            1 => candidates[0],
            0 => throw new FileNotFoundException("Bundled MineRewind package is missing."),
            _ => throw new InvalidDataException("Multiple bundles require an exact shipped MineRewind preset.")
        };
    }
}

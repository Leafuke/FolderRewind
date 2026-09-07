using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.Plugin.Runtime.Operations;

public static class RestoreStagingProposalValidator
{
    // 在 Host 写 staging 前验证整批产物，并复制插件持有的内存，避免验证后内容被修改。
    public static IReadOnlyList<RestoreStagedFileProposal> ValidateAndFreeze(
        RestoreStagingPreparationResult result,
        Func<string, bool> isManagedPath,
        int maximumFiles = 256,
        long maximumBytes = 64 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(isManagedPath);
        if (maximumFiles < 0 || maximumBytes < 0 || result.Files.Count > maximumFiles)
            throw new InvalidDataException("Restore preparation exceeds the proposal limit.");
        long bytes = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var frozen = new List<RestoreStagedFileProposal>();
        foreach (var file in result.Files)
        {
            if (Path.IsPathRooted(file.RelativePath))
                throw new InvalidDataException("Restore preparation paths must be relative.");
            var path = ArtifactPathRules.NormalizeRelativePath(file.RelativePath);
            if (path.Split('/').Any(segment => segment.EndsWith('.') || segment.EndsWith(' ')
                || segment.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0
                || segment.Any(char.IsControl)
                || IsDeviceName(segment.Split('.')[0])))
                throw new InvalidDataException("Restore preparation contains an unsafe Windows path.");
            bytes = checked(bytes + file.Content.Length);
            if (bytes > maximumBytes || !isManagedPath(path) || !paths.Add(path))
                throw new InvalidDataException("Restore preparation contains an oversized, duplicate or unmanaged path.");
            frozen.Add(new(path, file.Content.ToArray()));
        }
        foreach (var path in paths)
        {
            for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                if (paths.Contains(path[..slash]))
                    throw new InvalidDataException("Restore preparation contains a file/directory conflict.");
        }
        return frozen.AsReadOnly();
    }

    private static bool IsDeviceName(string name)
        => name.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (name.Length == 4 && name[3] is >= '1' and <= '9'
                && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)));
}

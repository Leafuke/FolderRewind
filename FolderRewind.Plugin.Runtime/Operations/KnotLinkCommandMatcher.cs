using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Operations;

public static class KnotLinkCommandMatcher
{
    public static bool Matches(
        KnotLinkCommandDescriptor descriptor,
        string command,
        IReadOnlyDictionary<string, string> arguments)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(arguments);
        if (!string.Equals(descriptor.Command, command, StringComparison.OrdinalIgnoreCase)) return false;

        foreach (var required in descriptor.RequiredArguments)
        {
            var actual = arguments.FirstOrDefault(pair =>
                string.Equals(pair.Key, required.Key, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(actual.Key) || !SemanticEquals(actual.Value, required.Value)) return false;
        }
        return true;
    }

    private static bool SemanticEquals(string actual, string expected)
    {
        if (TryBoolean(expected, out var expectedBoolean)
            && TryBoolean(actual, out var actualBoolean))
        {
            return actualBoolean == expectedBoolean;
        }
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryBoolean(string? value, out bool result)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "true" or "1" or "yes" or "y" or "on":
                result = true;
                return true;
            case "false" or "0" or "no" or "n" or "off":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }
}

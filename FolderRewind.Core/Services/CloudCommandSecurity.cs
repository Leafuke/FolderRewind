using System;
using System.IO;
using System.Text.RegularExpressions;

namespace FolderRewind.Services;

public readonly record struct CloudCommandPreviews(string DisplayPreview, string LogPreview);

public static partial class CloudCommandSecurity
{
    private const string RedactedValue = "[REDACTED]";

    [GeneratedRegex(
        """(?<prefix>(?<![\w-])(?:--|/)(?:password|passwd|passphrase|token|access[-_]?token|refresh[-_]?token|secret|client[-_]?secret|api[-_]?key|apikey|authorization)(?:\s*[:=]\s*|\s+))(?:"[^"\r\n]*"|'[^'\r\n]*'|[^\s\r\n]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveOptionRegex();

    [GeneratedRegex(
        """(?<prefix>\b(?:password|passwd|passphrase|token|access[-_]?token|refresh[-_]?token|secret|client[-_]?secret|api[-_]?key|apikey|aws_secret_access_key)\s*[:=]\s*)(?:"[^"\r\n]*"|'[^'\r\n]*'|[^\s,;\r\n]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveAssignmentRegex();

    [GeneratedRegex(
        @"(?<prefix>\b(?:authorization|proxy-authorization|x-api-key|x-auth-token|cookie|set-cookie)\s*:\s*)[^\r\n]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveHeaderRegex();

    [GeneratedRegex(
        @"(?<prefix>\b(?:bearer|basic)\s+)[A-Za-z0-9._~+/=-]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationSchemeRegex();

    [GeneratedRegex(
        @"(?<scheme>\b[a-z][a-z0-9+.-]*://)[^/@\s\r\n]+@",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlUserInfoRegex();

    [GeneratedRegex(@"[\r\n\t]+", RegexOptions.CultureInvariant)]
    private static partial Regex ControlWhitespaceRegex();

    public static CloudCommandPreviews BuildPreviews(
        string executablePath,
        string arguments,
        string operationCategory)
    {
        var executable = executablePath?.Trim() ?? string.Empty;
        var resolvedArguments = arguments?.Trim() ?? string.Empty;
        var displayPreview = string.IsNullOrWhiteSpace(resolvedArguments)
            ? executable
            : $"{executable} {resolvedArguments}";
        return new CloudCommandPreviews(
            displayPreview.Trim(),
            BuildLogPreview(executable, operationCategory));
    }

    public static string BuildLogPreview(string executablePath, string operationCategory)
    {
        var executableName = GetExecutableName(executablePath);
        var category = NormalizeOperationCategory(operationCategory);
        return $"{executableName} [operation={category}; arguments omitted]";
    }

    public static string DetectOperationCategory(string arguments)
    {
        var value = arguments?.TrimStart() ?? string.Empty;
        if (value.Length == 0) return "custom";
        var separator = value.IndexOfAny([' ', '\t', '\r', '\n']);
        var verb = (separator < 0 ? value : value[..separator]).Trim('"', '\'').ToLowerInvariant();
        return verb switch
        {
            "copy" or "copyto" or "sync" or "move" or "moveto" => "transfer",
            "ls" or "lsf" or "lsl" or "listremotes" => "list",
            "delete" or "deletefile" or "purge" => "delete",
            "mkdir" => "create-directory",
            _ => "custom"
        };
    }

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var result = UrlUserInfoRegex().Replace(text, "${scheme}" + RedactedValue + "@");
        result = SensitiveHeaderRegex().Replace(result, "${prefix}" + RedactedValue);
        result = SensitiveOptionRegex().Replace(result, "${prefix}" + RedactedValue);
        result = SensitiveAssignmentRegex().Replace(result, "${prefix}" + RedactedValue);
        result = AuthorizationSchemeRegex().Replace(result, "${prefix}" + RedactedValue);
        return result;
    }

    private static string GetExecutableName(string executablePath)
    {
        var normalized = executablePath?.Trim().Trim('"') ?? string.Empty;
        var controlWhitespaceIndex = normalized.IndexOfAny(['\r', '\n', '\t']);
        if (controlWhitespaceIndex >= 0)
            normalized = normalized[..controlWhitespaceIndex];
        try
        {
            normalized = Path.GetFileName(normalized);
        }
        catch
        {
        }

        normalized = Redact(normalized);
        return string.IsNullOrWhiteSpace(normalized) ? "unknown-executable" : normalized;
    }

    private static string NormalizeOperationCategory(string operationCategory)
    {
        var normalized = ControlWhitespaceRegex().Replace(operationCategory?.Trim() ?? string.Empty, "-");
        return normalized switch
        {
            "transfer" or "list" or "delete" or "create-directory" or "custom" => normalized,
            _ => "custom"
        };
    }
}

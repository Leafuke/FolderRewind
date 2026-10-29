using FolderRewind.Models;
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal static class OpenListEnvironmentReader
{
    // This reads only the selected configuration. CLI directory arguments are not stored
    // in arbitrary config files, so only the standard data/config.json layout is suggested.
    internal static async Task<OpenListRuntimeSettings> ReadAsync(string path, OpenListRuntimeSettings current, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || new FileInfo(path).Length > 1024 * 1024)
            throw new InvalidDataException(I18n.GetString("OpenList_ConfigUnreadable"));
        RcloneCredentialRefresh.RejectReparsePoints(path);
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false),
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var result = ConfigSnapshotMapper.Copy(current);
        var changedConfig = !StringComparer.OrdinalIgnoreCase.Equals(result.ConfigFilePath, Path.GetFullPath(path));
        result.ConfigFilePath = Path.GetFullPath(path);
        // Existing explicit CLI directory parameters take precedence over layout guesses.
        if (changedConfig) result.ServiceBaseUri = "";
        if (!document.RootElement.TryGetProperty("scheme", out var scheme))
            throw new InvalidDataException(I18n.GetString("OpenList_ConfigUnreadable"));
        var address = scheme.TryGetProperty("address", out var host) ? host.GetString() ?? "" : "";
        if (address is "" or "0.0.0.0" or "::" or "[::]") address = "127.0.0.1";
        var httpPort = scheme.TryGetProperty("http_port", out var http) ? http.GetInt32() : -1;
        var httpsPort = scheme.TryGetProperty("https_port", out var https) ? https.GetInt32() : -1;
        var forceHttps = scheme.TryGetProperty("force_https", out var force) && force.GetBoolean();
        var secure = httpsPort > 0 && (forceHttps || httpPort < 1);
        var port = secure ? httpsPort : httpPort;
        if (port is > 0 and <= 65535)
            result.ServiceBaseUri = new UriBuilder(secure ? "https" : "http", address, port).Uri.AbsoluteUri.TrimEnd('/');
        var data = Path.GetDirectoryName(result.ConfigFilePath)!;
        if (Path.GetFileName(result.ConfigFilePath).Equals("config.json", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(data).Equals("data", StringComparison.OrdinalIgnoreCase))
        {
            if (result.DataDirectory.Length == 0) result.DataDirectory = data;
            if (result.WorkingDirectory.Length == 0) result.WorkingDirectory = Path.GetDirectoryName(data) ?? "";
            var candidate = Path.Combine(result.WorkingDirectory, "openlist.exe");
            if (result.ExecutablePath.Length == 0 && File.Exists(candidate)) result.ExecutablePath = candidate;
        }
        if (!OpenListEnvironmentEdits.SameIdentity(result, current)) OpenListEnvironmentEdits.ClearAuthorization(result);
        OpenListRuntimeService.ValidateDraft(result);
        return result;
    }
}

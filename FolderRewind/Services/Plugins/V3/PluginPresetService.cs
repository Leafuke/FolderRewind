using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Packaging;

namespace FolderRewind.Services.Plugins.V3;

public enum PluginPresetStepOutcome { Success, SuccessWithWarnings, Blocked, Failed }
public sealed record PluginPresetStepResult(string ActionId, PluginPresetStepOutcome Outcome, string Message);
public sealed record PluginPresetRunResult(
    PluginPresetStepOutcome Outcome,
    IReadOnlyList<PluginPresetStepResult> Steps)
{
    public bool Success => Outcome is PluginPresetStepOutcome.Success or PluginPresetStepOutcome.SuccessWithWarnings;
}

public interface IPluginPresetConsentBroker
{
    ValueTask<bool> ConfirmExternalDownloadAsync(string name, string url, string sha256, CancellationToken cancellationToken);
    ValueTask<bool> ConfirmExternalLaunchAsync(string name, string localPath, CancellationToken cancellationToken);
}

public static class PluginPresetService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(2) };

    public static async ValueTask<PluginPresetRunResult> ExecuteAsync(
        string presetPath,
        IPluginPresetConsentBroker consent,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consent);
        var preset = Parse(presetPath);
        var results = new List<PluginPresetStepResult>();
        foreach (var action in preset.Actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(action.Id);
            try
            {
                results.Add(await ExecuteActionAsync(action, consent, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                results.Add(new PluginPresetStepResult(action.Id, PluginPresetStepOutcome.Failed, ex.Message));
            }
        }
        var outcome = results.Any(value => value.Outcome == PluginPresetStepOutcome.Failed)
            ? PluginPresetStepOutcome.Failed
            : results.Any(value => value.Outcome == PluginPresetStepOutcome.Blocked)
                ? PluginPresetStepOutcome.Blocked
                : results.Any(value => value.Outcome == PluginPresetStepOutcome.SuccessWithWarnings)
                    ? PluginPresetStepOutcome.SuccessWithWarnings
                    : PluginPresetStepOutcome.Success;
        return new PluginPresetRunResult(outcome, results);
    }

    public static string MinecraftEnhancedExperiencePath => Path.Combine(
        AppContext.BaseDirectory, "Assets", "PluginPresets", "minecraft-enhanced-experience.v1.json");

    private static async ValueTask<PluginPresetStepResult> ExecuteActionAsync(
        PresetAction action,
        IPluginPresetConsentBroker consent,
        CancellationToken cancellationToken)
    {
        switch (action.Type)
        {
            case "installBundledPlugin":
                var packagePath = ResolveBundledPath(action.PackagePath!);
                var install = await PluginV3PackageService.InstallAsync(
                    packagePath,
                    PluginInstallProvenance.BundledOfficial,
                    action.Sha256,
                    cancellationToken).ConfigureAwait(false);
                if (!StringComparer.Ordinal.Equals(install.State.PluginId.Value, action.PluginId))
                    throw new InvalidDataException("Preset PluginId does not match bundled package.");
                return Success(action, $"Installed {install.State.PluginId} {install.State.CurrentVersion}.");
            case "enablePlugin":
                var transition = await PluginV3PackageService.SetEnabledAsync(
                    new PluginId(action.PluginId!), true, cancellationToken).ConfigureAwait(false);
                return transition.Success
                    ? Success(action, $"Enabled {action.PluginId}.")
                    : new PluginPresetStepResult(action.Id, PluginPresetStepOutcome.Failed,
                        string.Join(",", transition.Diagnostics.Select(value => value.Code)));
            case "setHostFeature" when action.Feature == "knotLink":
                ConfigService.CurrentConfig.GlobalSettings.EnableKnotLink = action.Enabled;
                ConfigService.Save();
                if (action.Enabled) KnotLinkService.Initialize();
                return Success(action, "KnotLink Host integration configured.");
            case "setupExternalIntegration":
                return await DownloadAndLaunchAsync(action, consent, cancellationToken).ConfigureAwait(false);
            case "notice":
                return new PluginPresetStepResult(
                    action.Id,
                    action.Severity == "warning" ? PluginPresetStepOutcome.SuccessWithWarnings : PluginPresetStepOutcome.Success,
                    action.Message!);
            default:
                throw new InvalidDataException($"Preset action type '{action.Type}' is not allowed.");
        }
    }

    private static async ValueTask<PluginPresetStepResult> DownloadAndLaunchAsync(
        PresetAction action,
        IPluginPresetConsentBroker consent,
        CancellationToken cancellationToken)
    {
        if (!await consent.ConfirmExternalDownloadAsync(
                action.Name!, action.Url!, action.Sha256!, cancellationToken).ConfigureAwait(false))
            return new PluginPresetStepResult(action.Id, PluginPresetStepOutcome.Blocked, "External download was not confirmed.");
        var directory = Path.Combine(Path.GetTempPath(), "FolderRewind", "preset-downloads");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, action.FileName!);
        var bytes = await Client.GetByteArrayAsync(action.Url!, cancellationToken).ConfigureAwait(false);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!StringComparer.OrdinalIgnoreCase.Equals(hash, action.Sha256))
            throw new InvalidDataException("External installer SHA-256 does not match curated facts.");
        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        if (!await consent.ConfirmExternalLaunchAsync(action.Name!, path, cancellationToken).ConfigureAwait(false))
            return new PluginPresetStepResult(action.Id, PluginPresetStepOutcome.Blocked, "External launch was not confirmed.");
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true, Verb = "open" });
        return Success(action, $"Started {action.Name}.");
    }

    private static PresetDocument Parse(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("Unsupported Preset schema.");
        var actions = new List<PresetAction>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in root.GetProperty("actions").EnumerateArray())
        {
            var action = JsonSerializer.Deserialize<PresetAction>(value.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException("Preset action is empty.");
            if (string.IsNullOrWhiteSpace(action.Id) || !ids.Add(action.Id)) throw new InvalidDataException("Preset action IDs must be unique.");
            Validate(action);
            actions.Add(action);
        }
        if (actions.Count is 0 or > 32) throw new InvalidDataException("Preset action count is outside its bound.");
        return new PresetDocument(actions);
    }

    private static void Validate(PresetAction action)
    {
        if (action.Type is not ("installBundledPlugin" or "enablePlugin" or "setHostFeature" or "setupExternalIntegration" or "notice"))
            throw new InvalidDataException("Preset contains a forbidden action type.");
        if (action.Type is "installBundledPlugin" or "enablePlugin") _ = new PluginId(action.PluginId!);
        if (action.Type == "installBundledPlugin") RequireHashAndRelativePath(action.Sha256, action.PackagePath);
        if (action.Type == "setHostFeature" && action.Feature != "knotLink") throw new InvalidDataException("Preset requests an unknown Host feature.");
        if (action.Type == "setupExternalIntegration")
        {
            if (!Uri.TryCreate(action.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("External integration URL must use HTTPS.");
            if (string.IsNullOrWhiteSpace(action.Name) || string.IsNullOrWhiteSpace(action.FileName)
                || Path.GetFileName(action.FileName) != action.FileName) throw new InvalidDataException("External integration identity is invalid.");
            RequireHash(action.Sha256);
        }
        if (action.Type == "notice" && string.IsNullOrWhiteSpace(action.Message)) throw new InvalidDataException("Notice message is required.");
    }

    private static void RequireHashAndRelativePath(string? hash, string? path)
    {
        RequireHash(hash);
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathFullyQualified(path) || path.Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException("Bundled package path must be relative.");
    }
    private static void RequireHash(string? hash)
    {
        if (hash?.Length != 64 || !hash.All(char.IsAsciiHexDigit)) throw new InvalidDataException("SHA-256 is required.");
    }
    private static string ResolveBundledPath(string relative)
    {
        var root = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, relative));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Bundled path escapes application root.");
        return candidate;
    }
    private static PluginPresetStepResult Success(PresetAction action, string message)
        => new(action.Id, PluginPresetStepOutcome.Success, message);

    private sealed record PresetDocument(IReadOnlyList<PresetAction> Actions);
    private sealed class PresetAction
    {
        public string Id { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string? PluginId { get; set; }
        public string? PackagePath { get; set; }
        public string? Sha256 { get; set; }
        public string? Feature { get; set; }
        public bool Enabled { get; set; }
        public string? Name { get; set; }
        public string? Url { get; set; }
        public string? FileName { get; set; }
        public string? Severity { get; set; }
        public string? Message { get; set; }
    }
}

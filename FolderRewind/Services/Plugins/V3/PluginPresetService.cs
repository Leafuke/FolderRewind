using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    public static async ValueTask<PluginPresetRunResult> ExecuteAsync(
        string presetPath,
        IPluginPresetConsentBroker consent,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        IReadOnlySet<string>? selectedActions = null)
    {
        ArgumentNullException.ThrowIfNull(consent);
        var preset = Parse(presetPath);
        var results = new List<PluginPresetStepResult>();
        foreach (var action in preset.Actions)
        {
            if (selectedActions is not null && !selectedActions.Contains(action.Id)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            if (action.DependsOn.Any(id => !results.Any(r => r.ActionId == id && r.Outcome == PluginPresetStepOutcome.Success)))
            {
                results.Add(new(action.Id, PluginPresetStepOutcome.Blocked, I18n.GetString("Onboarding_DependencyBlocked")));
                continue;
            }
            progress?.Report(GetProgressMessage(action.Id));
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
        // Installation/enable actions can finish after the initial UI inventory was populated.
        // Refresh it before callers create drafts that validate required plugins.
        await UiDispatcherService.RunOnUiAsync(PluginService.RefreshRuntimeUi).ConfigureAwait(false);
        return new PluginPresetRunResult(outcome, results);
    }

    public static string MinecraftEnhancedExperiencePath => Path.Combine(
        AppContext.BaseDirectory, "Assets", "PluginPresets", "minecraft-enhanced-experience.v1.json");

    public static string GetActionDisplayName(string actionId)
        => actionId switch
        {
            "install-minerewind" => I18n.GetString("PluginPreset_Step_InstallMineRewind"),
            "enable-minerewind" => I18n.GetString("PluginPreset_Step_EnableMineRewind"),
            "enable-knotlink-host" => I18n.GetString("PluginPreset_Step_EnableKnotLink"),
            "knotlink-installer-awaits-curation" => I18n.GetString("PluginPreset_Step_KnotLinkNotice"),
            "mine-backup-reminder" => I18n.GetString("PluginPreset_Step_MineBackupReminder"),
            _ => actionId
        };

    private static string GetProgressMessage(string actionId)
        => actionId switch
        {
            "install-minerewind" => I18n.GetString("PluginPreset_Progress_InstallMineRewind"),
            "enable-minerewind" => I18n.GetString("PluginPreset_Progress_EnableMineRewind"),
            "enable-knotlink-host" => I18n.GetString("PluginPreset_Progress_EnableKnotLink"),
            "knotlink-installer-awaits-curation" => I18n.GetString("PluginPreset_Progress_KnotLinkNotice"),
            "mine-backup-reminder" => I18n.GetString("PluginPreset_Progress_MineBackupReminder"),
            _ => actionId
        };

    private static async ValueTask<PluginPresetStepResult> ExecuteActionAsync(
        PresetAction action,
        IPluginPresetConsentBroker consent,
        CancellationToken cancellationToken)
    {
        switch (action.Type)
        {
            case "installBundledPlugin":
                if (await PluginV3PackageService.IsInstalledAsync(new PluginId(action.PluginId!), cancellationToken).ConfigureAwait(false))
                    return Success(action, I18n.GetString("Onboarding_ComponentReused"));
                var packagePath = ResolveBundledPath(action.PackagePath!);
                var install = await PluginV3PackageService.InstallAsync(
                    packagePath,
                    PluginInstallProvenance.BundledOfficial,
                    action.Sha256,
                    cancellationToken).ConfigureAwait(false);
                if (!install.Success || install.InstalledPackage is null)
                {
                    return new PluginPresetStepResult(
                        action.Id,
                        PluginPresetStepOutcome.Failed,
                        PluginV3PackageService.FormatInstallOutcome(install));
                }
                if (!StringComparer.Ordinal.Equals(install.InstalledPackage.State.PluginId.Value, action.PluginId))
                    throw new InvalidDataException("Preset PluginId does not match bundled package.");
                return new(action.Id, install.RequiresRestart ? PluginPresetStepOutcome.SuccessWithWarnings : PluginPresetStepOutcome.Success, I18n.Format(
                    "PluginPreset_Installed",
                    install.InstalledPackage.State.PluginId,
                    install.InstalledPackage.State.CurrentVersion));
            case "enablePlugin":
                var transition = await PluginV3PackageService.SetEnabledAsync(
                    new PluginId(action.PluginId!), true, cancellationToken).ConfigureAwait(false);
                return transition.Success
                    ? new(action.Id, transition.RequiresRestart ? PluginPresetStepOutcome.SuccessWithWarnings : PluginPresetStepOutcome.Success,
                        I18n.Format("PluginPreset_Enabled", action.PluginId!) + (transition.RequiresRestart ? "\n" + I18n.GetString("Onboarding_RestartRequired") : ""))
                    : new PluginPresetStepResult(action.Id, PluginPresetStepOutcome.Failed,
                        PluginV3PackageService.FormatRuntimeDiagnostics(transition.Diagnostics));
            case "setHostFeature" when action.Feature == "knotLink":
                await UiDispatcherService.RunOnUiAsync(async () =>
                {
                    var settings = ConfigService.CurrentConfig.GlobalSettings;
                    var previous = settings.EnableKnotLink;
                    await ConfigEditTransaction.ApplyAsync(() => settings.EnableKnotLink = action.Enabled,
                        () => settings.EnableKnotLink = previous, () => ConfigService.SaveAsync(),
                        I18n.GetString("Common_Failed"), cancellationToken);
                }).ConfigureAwait(false);
                if (action.Enabled) await KnotLinkService.InitializeAsync(cancellationToken).ConfigureAwait(false);
                return Success(action, I18n.GetString("PluginPreset_KnotLinkConfigured"));
            case "setupExternalIntegration":
                return await DownloadAndLaunchAsync(action, consent, cancellationToken).ConfigureAwait(false);
            case "notice":
                return new PluginPresetStepResult(
                    action.Id,
                    action.Severity == "warning" ? PluginPresetStepOutcome.SuccessWithWarnings : PluginPresetStepOutcome.Success,
                    I18n.GetString(action.MessageResourceKey!));
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
            return new PluginPresetStepResult(
                action.Id,
                PluginPresetStepOutcome.Blocked,
                I18n.GetString("PluginPreset_ExternalDownloadDeclined"));
        var directory = Path.Combine(Path.GetTempPath(), "FolderRewind", "preset-downloads", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, action.FileName!);
        var bytes = await GitHubReleaseService.DownloadAssetAsync(action.Url!, cancellationToken, ToolArchiveInstaller.MaximumArchiveBytes).ConfigureAwait(false);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!StringComparer.OrdinalIgnoreCase.Equals(hash, action.Sha256))
            throw new InvalidDataException(I18n.GetString("PluginPreset_ExternalHashMismatch"));
        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        if (!await consent.ConfirmExternalLaunchAsync(action.Name!, path, cancellationToken).ConfigureAwait(false))
            return new PluginPresetStepResult(
                action.Id,
                PluginPresetStepOutcome.Blocked,
                I18n.GetString("PluginPreset_ExternalLaunchDeclined"));
        using var process = Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true, Verb = "open" })
            ?? throw new IOException(I18n.GetString("Onboarding_InstallerNotStarted"));
        try { await process.WaitForExitAsync(cancellationToken).WaitAsync(OnboardingOperationBudgets.ServiceWait, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { /* The user-owned installer keeps running; installation still requires a post-check. */ }
        return new(action.Id, PluginPresetStepOutcome.SuccessWithWarnings,
            I18n.Format("PluginPreset_ExternalStarted", action.Name!) + "\n" + I18n.GetString("Onboarding_InstallerNeedsCheck"));
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
        var preceding = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in actions)
        {
            if (action.DependsOn.Any(id => !preceding.Contains(id))) throw new InvalidDataException("Preset dependencies must refer to preceding actions.");
            preceding.Add(action.Id);
        }
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
        if (action.Type == "notice" && string.IsNullOrWhiteSpace(action.MessageResourceKey))
            throw new InvalidDataException("Notice messageResourceKey is required.");
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
        public string? MessageResourceKey { get; set; }
        public string[] DependsOn { get; set; } = [];
    }
}

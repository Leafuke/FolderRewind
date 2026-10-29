using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Packaging;

/// <summary>Updates an existing official installation without activating plugin code or changing settings.</summary>
public static class PluginBundledPackageUpdater
{
    // The host must serialize this with all other installation operations and run it before activation.
    public static async ValueTask<PluginInstallResult?> UpdateAsync(
        PluginPackageInstaller installer,
        PluginId pluginId,
        string packagePath,
        string expectedSha256,
        Func<ParsedPluginPackageManifest, CancellationToken, ValueTask<PluginPackageInstallValidationFacts>> validationFacts,
        CancellationToken cancellationToken = default)
    {
        var prior = await installer.ReadStateAsync(pluginId, cancellationToken).ConfigureAwait(false);
        if (prior is null || prior.Provenance is not
            (PluginInstallProvenance.BundledOfficial or PluginInstallProvenance.OfficialCatalog))
            return null;

        var package = await PluginPackageValidator.ValidateAsync(
            packagePath, expectedSha256, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (package.Manifest.Contract.PluginId != pluginId)
            throw new InvalidDataException("Bundled update does not match the installed PluginId.");
        if (PluginSemanticVersion.ComparePrecedence(package.Manifest.Contract.Version, prior.CurrentVersion) <= 0)
            return null;

        return await installer.InstallAsync(packagePath, prior.Provenance, expectedSha256,
            await validationFacts(package.Manifest, cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }
}

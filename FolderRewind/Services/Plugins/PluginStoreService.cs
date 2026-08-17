using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Packaging;
using FolderRewind.Services.Plugins.V3;
using ResourceLoader = FolderRewind.Services.AppResourceLoader;

namespace FolderRewind.Services.Plugins
{
    /// <summary>Official Catalog and manual .frplugin installation entry point.</summary>
    public static class PluginStoreService
    {
        public const string OfficialCatalogUrl =
            "https://leafuke.github.io/FolderRewind-Plugin-Catalog/catalog.v1.json";
        private const int MaximumCatalogBytes = 4 * 1024 * 1024;
        private const int MaximumEntries = 1000;
        private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };
        private static readonly ResourceLoader Rl = ResourceLoader.GetForViewIndependentUse();
        private static string CachePath => Path.Combine(
            AppRuntimeInfo.WritableAppDataBaseDirectory,
            "FolderRewind", "catalog-cache", "catalog.v1.json");

        public sealed class PluginStoreLoadResult
        {
            public IReadOnlyList<PluginStoreAssetItem> Items { get; set; } = Array.Empty<PluginStoreAssetItem>();
            public string? Summary { get; set; }
            public string? ErrorMessage { get; set; }
            public bool FromCache { get; set; }
        }

        public static async Task<PluginStoreLoadResult> GetOfficialCatalogAsync(CancellationToken ct)
        {
            Exception? onlineError = null;
            try
            {
                var bytes = await Client.GetByteArrayAsync(OfficialCatalogUrl, ct).ConfigureAwait(false);
                var items = ParseCatalog(bytes);
                await WriteCacheAtomicallyAsync(bytes, ct).ConfigureAwait(false);
                return new PluginStoreLoadResult
                {
                    Items = items,
                    Summary = $"Official Catalog · {items.Count} plugin(s)"
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                onlineError = ex;
            }

            try
            {
                if (!File.Exists(CachePath)) throw new FileNotFoundException("Official Catalog cache is unavailable.");
                var bytes = await File.ReadAllBytesAsync(CachePath, ct).ConfigureAwait(false);
                var items = ParseCatalog(bytes);
                return new PluginStoreLoadResult
                {
                    Items = items,
                    Summary = $"Official Catalog (offline cache) · {items.Count} plugin(s)",
                    FromCache = true
                };
            }
            catch (Exception cacheError) when (cacheError is not OperationCanceledException)
            {
                return new PluginStoreLoadResult
                {
                    ErrorMessage = $"Official Catalog unavailable: {onlineError?.Message ?? cacheError.Message}"
                };
            }
        }

        public static async Task<(bool Success, string Message)> DownloadAndInstallAsync(
            PluginStoreAssetItem asset,
            CancellationToken ct)
        {
            if (asset is null || string.IsNullOrWhiteSpace(asset.DownloadUrl)
                || string.IsNullOrWhiteSpace(asset.Sha256))
                return (false, Rl.GetString("PluginStore_InvalidItem"));
            var downloadRoot = Path.Combine(PluginService.PluginRootDirectory, ".downloads");
            Directory.CreateDirectory(downloadRoot);
            var path = Path.Combine(downloadRoot, Guid.NewGuid().ToString("N") + ".frplugin");
            try
            {
                var bytes = await Client.GetByteArrayAsync(asset.DownloadUrl, ct).ConfigureAwait(false);
                await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
                var package = await PluginPackageValidator.ValidateAsync(path, asset.Sha256, cancellationToken: ct)
                    .ConfigureAwait(false);
                ValidateCatalogBinding(asset, package.Manifest);
                var result = await PluginV3PackageService.InstallAsync(
                    path, PluginInstallProvenance.OfficialCatalog, asset.Sha256, ct).ConfigureAwait(false);
                return (true, PluginV3PackageService.FormatInstallOutcome(result));
            }
            catch (OperationCanceledException) { return (false, Rl.GetString("Common_Canceled")); }
            catch (Exception ex)
            {
                LogService.LogError($"Official Catalog install failed: {ex.Message}", nameof(PluginStoreService), ex);
                return (false, ex.Message);
            }
            finally { try { if (File.Exists(path)) File.Delete(path); } catch { } }
        }

        public static async Task<(bool Success, string Message)> InstallManualAsync(
            string packagePath,
            CancellationToken ct = default)
        {
            try
            {
                var result = await PluginV3PackageService.InstallAsync(
                    packagePath, PluginInstallProvenance.Manual, cancellationToken: ct).ConfigureAwait(false);
                return (true, PluginV3PackageService.FormatInstallOutcome(result));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { return (false, ex.Message); }
        }

        private static IReadOnlyList<PluginStoreAssetItem> ParseCatalog(byte[] bytes)
        {
            if (bytes.Length is 0 or > MaximumCatalogBytes) throw new InvalidDataException("Catalog size is outside its bound.");
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("Unsupported Catalog schema.");
            var entries = root.GetProperty("entries");
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > MaximumEntries)
                throw new InvalidDataException("Catalog entry count is outside its bound.");
            var result = new List<PluginStoreAssetItem>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries.EnumerateArray())
            {
                var pluginId = new PluginId(entry.GetProperty("pluginId").GetString()!).Value;
                if (!ids.Add(pluginId)) throw new InvalidDataException("Catalog contains duplicate PluginIds.");
                var version = PluginSemanticVersion.RequireStrict(
                    entry.GetProperty("version").GetString(),
                    "Catalog version");
                var artifact = entry.GetProperty("artifact");
                var url = artifact.GetProperty("url").GetString()!;
                var sha = artifact.GetProperty("sha256").GetString()!;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                    || sha.Length != 64 || !sha.All(char.IsAsciiHexDigit))
                    throw new InvalidDataException("Catalog release facts are invalid.");
                var api = entry.GetProperty("pluginApi");
                result.Add(new PluginStoreAssetItem
                {
                    PluginId = pluginId,
                    Name = pluginId,
                    Version = version,
                    ReleaseTag = version,
                    DownloadUrl = url,
                    Sha256 = sha,
                    PluginApiMajor = api.GetProperty("major").GetInt32(),
                    PluginApiMinor = api.GetProperty("minor").GetInt32(),
                    Channel = entry.GetProperty("channel").GetString()!,
                    TrustClassification = entry.GetProperty("trustClassification").GetString()!,
                    Architectures = entry.GetProperty("architectures").EnumerateArray().Select(value => value.GetString()!).ToArray()
                });
            }
            return result;
        }

        private static void ValidateCatalogBinding(PluginStoreAssetItem item, ParsedPluginPackageManifest manifest)
        {
            if (!StringComparer.Ordinal.Equals(item.PluginId, manifest.Contract.PluginId.Value)
                || !StringComparer.Ordinal.Equals(item.Version, manifest.Contract.Version)
                || manifest.Contract.RequiredApi.Major != item.PluginApiMajor
                || manifest.Contract.RequiredApi.Minor != item.PluginApiMinor
                || !item.Architectures.Order(StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(manifest.Architectures.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Catalog identity, API, or architecture does not match package Manifest.");
        }

        private static async Task WriteCacheAtomicallyAsync(byte[] bytes, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var temporary = CachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes, ct).ConfigureAwait(false);
            File.Move(temporary, CachePath, overwrite: true);
        }
    }
}

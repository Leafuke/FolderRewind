using System;
using System.Globalization;

namespace FolderRewind.Services.KnotLink
{
    internal sealed class KnotLinkBackupOverrides
    {
        public string? BackupMode { get; init; }

        public string? CompressionMethod { get; init; }

        public int? CompressionLevel { get; init; }

        public bool HasOverrides =>
            BackupMode != null
            || CompressionMethod != null
            || CompressionLevel.HasValue;
    }

    internal static class KnotLinkBackupOverrideResolver
    {
        public static bool TryResolve(
            KnotLinkCommandRequest request,
            string configuredCompressionMethod,
            int configuredCompressionLevel,
            out KnotLinkBackupOverrides overrides,
            out string error)
        {
            ArgumentNullException.ThrowIfNull(request);

            overrides = new KnotLinkBackupOverrides();
            error = string.Empty;

            string? backupMode = null;
            if (request.HasOption("backup_mode"))
            {
                var requestedMode = request.GetStringOrDefault("backup_mode").Trim();
                if (requestedMode.Equals("full", StringComparison.OrdinalIgnoreCase))
                {
                    backupMode = "Full";
                }
                else if (requestedMode.Equals("incremental", StringComparison.OrdinalIgnoreCase))
                {
                    backupMode = "Incremental";
                }
                else
                {
                    error = $"Invalid backup_mode '{requestedMode}'. Allowed values: full, incremental.";
                    return false;
                }
            }

            string? compressionMethod = null;
            if (request.HasOption("compression_method"))
            {
                var requestedMethod = request.GetStringOrDefault("compression_method").Trim();
                if (!ArchiveCompressionPolicy.TryNormalizeMethod(requestedMethod, out compressionMethod))
                {
                    error = $"Invalid compression_method '{requestedMethod}'. Allowed values: LZMA2, Deflate, BZip2, zstd.";
                    return false;
                }
            }

            int? compressionLevel = null;
            if (request.HasOption("compression_level"))
            {
                var requestedLevel = request.GetStringOrDefault("compression_level").Trim();
                if (!int.TryParse(requestedLevel, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLevel))
                {
                    error = $"Invalid compression_level '{requestedLevel}'. Expected an integer.";
                    return false;
                }

                compressionLevel = parsedLevel;
            }

            if (compressionMethod != null || compressionLevel.HasValue)
            {
                var effectiveMethod = compressionMethod ?? configuredCompressionMethod?.Trim() ?? string.Empty;
                if (!ArchiveCompressionPolicy.TryNormalizeMethod(effectiveMethod, out var normalizedEffectiveMethod))
                {
                    error = $"Cannot validate compression_level because the effective compression method '{effectiveMethod}' is unsupported.";
                    return false;
                }

                var effectiveLevel = compressionLevel ?? configuredCompressionLevel;
                var (minimum, maximum) = ArchiveCompressionPolicy.GetLevelRange(normalizedEffectiveMethod);
                if (effectiveLevel < minimum || effectiveLevel > maximum)
                {
                    error = $"Invalid compression_level '{effectiveLevel}' for {normalizedEffectiveMethod}. Allowed range: {minimum}-{maximum}.";
                    return false;
                }
            }

            overrides = new KnotLinkBackupOverrides
            {
                BackupMode = backupMode,
                CompressionMethod = compressionMethod,
                CompressionLevel = compressionLevel
            };
            return true;
        }

    }
}

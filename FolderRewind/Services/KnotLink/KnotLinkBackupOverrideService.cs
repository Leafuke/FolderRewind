using FolderRewind.Models;
using FolderRewind.Services;
using System;

namespace FolderRewind.Services.KnotLink
{
    /// <summary>
    /// Creates a non-persistent backup configuration with the one-shot archive
    /// overrides carried by a KnotLink request.
    /// </summary>
    public static class KnotLinkBackupOverrideService
    {
        public static bool TryCreateEffectiveConfig(
            KnotLinkCommandRequest request,
            BackupConfig source,
            out BackupConfig effectiveConfig,
            out string error)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(source);

            effectiveConfig = source;
            error = string.Empty;

            if (!KnotLinkBackupOverrideResolver.TryResolve(
                    request,
                    source.Archive?.Method ?? "LZMA2",
                    source.Archive?.CompressionLevel ?? 5,
                    out var overrides,
                    out error))
            {
                return false;
            }

            if (!overrides.HasOverrides)
            {
                return true;
            }

            try
            {
                var clone = BackupConfigCloneService.CloneForRuntimeMutation(
                    source,
                    "Failed to clone the backup configuration for one-shot overrides.");

                if (overrides.BackupMode != null)
                {
                    clone.Archive.Mode = overrides.BackupMode == "Incremental"
                        ? BackupMode.Incremental
                        : BackupMode.Full;
                }

                if (overrides.CompressionMethod != null)
                {
                    clone.Archive.Method = overrides.CompressionMethod;
                }

                if (overrides.CompressionLevel is int compressionLevel)
                {
                    clone.Archive.CompressionLevel = compressionLevel;
                }

                effectiveConfig = clone;
                return true;
            }
            catch (Exception ex)
            {
                error = $"Failed to create an effective backup configuration: {ex.Message}";
                return false;
            }
        }
    }
}

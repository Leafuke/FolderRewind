using System;

namespace FolderRewind.Services
{
    internal static class ArchiveCompressionPolicy
    {
        public static (int Min, int Max) GetLevelRange(string? method) =>
            method switch
            {
                "zstd" => (1, 22),
                "BZip2" => (1, 9),
                "LZMA2" => (0, 9),
                "Deflate" => (0, 9),
                _ => (0, 9)
            };

        public static bool TryNormalizeMethod(string? value, out string normalized)
        {
            if (string.Equals(value, "lzma2", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "LZMA2";
                return true;
            }

            if (string.Equals(value, "deflate", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "Deflate";
                return true;
            }

            if (string.Equals(value, "bzip2", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "BZip2";
                return true;
            }

            if (string.Equals(value, "zstd", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "zstd";
                return true;
            }

            normalized = string.Empty;
            return false;
        }
    }
}

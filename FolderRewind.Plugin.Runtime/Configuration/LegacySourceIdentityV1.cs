using System.Security.Cryptography;
using System.Text;

namespace FolderRewind.Plugin.Runtime.Configuration;

/// <summary>
/// 正式版 1.8.x 配置与历史迁移共用的冻结身份算法。它只服务 migration/recovery；
/// Native 新对象仍使用随机 Guid，不能把路径重新变成运行时身份。
/// </summary>
public static class LegacySourceIdentityV1
{
    public static readonly Guid ConfigNamespace = Guid.Parse("3fd4b61b-2f4c-5a9e-91f4-719a67cc26c0");
    public static readonly Guid SourceNamespace = Guid.Parse("a7b19a4e-3c87-5de5-8e0b-45bf7a85747f");
    public static readonly Guid HistoryObjectNamespace = Guid.Parse("5d4558b2-5977-5ab4-934c-0aa5ae4cc7ea");

    public static Guid CreateConfigId(
        string? name,
        string? destinationPath,
        int duplicateOrdinal)
    {
        var key = string.Join('|',
            "legacy-config-v1",
            NormalizeText(name),
            NormalizePath(destinationPath),
            duplicateOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return CreateUuid5(ConfigNamespace, key);
    }

    public static Guid CreateSourceId(
        string configId,
        string? originalPath,
        int duplicateOrdinal)
    {
        var key = string.Join('|',
            "legacy-source-v1",
            CanonicalizeConfigId(configId),
            NormalizePath(originalPath),
            duplicateOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return CreateUuid5(SourceNamespace, key);
    }

    public static string CreateHistoryOriginKey(
        string configId,
        string? originalPath,
        string? fileName,
        DateTime timestamp)
    {
        var stableTicks = timestamp.Kind == DateTimeKind.Unspecified
            ? timestamp.Ticks
            : timestamp.ToUniversalTime().Ticks;
        return string.Join('|',
            "legacy-history-v1",
            CanonicalizeConfigId(configId),
            NormalizePath(originalPath),
            NormalizeText(fileName),
            stableTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public static Guid CreateHistoryObjectId(
        string configId,
        string? originalPath,
        string? fileName,
        DateTime timestamp)
        => CreateUuid5(
            HistoryObjectNamespace,
            CreateHistoryOriginKey(configId, originalPath, fileName, timestamp));

    public static string CanonicalizeConfigId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Config identity cannot be empty.", nameof(value));
        }

        var trimmed = value.Trim();
        return Guid.TryParse(trimmed, out var guid) && guid != Guid.Empty
            ? guid.ToString("N")
            : trimmed.ToUpperInvariant();
    }

    public static string NormalizePath(string? value)
    {
        var replaced = (value ?? string.Empty).Trim().Replace('\\', '/');
        if (replaced.Length == 0)
        {
            return string.Empty;
        }

        var isUnc = replaced.StartsWith("//", StringComparison.Ordinal);
        var isRooted = !isUnc && replaced.StartsWith("/", StringComparison.Ordinal);
        var body = string.Join('/', replaced.Split('/', StringSplitOptions.RemoveEmptyEntries));
        var normalized = (isUnc ? "//" : isRooted ? "/" : string.Empty) + body;
        if (normalized.Length > 1
            && !IsDriveRoot(normalized)
            && normalized.EndsWith("/", StringComparison.Ordinal))
        {
            normalized = normalized.TrimEnd('/');
        }

        return normalized.ToUpperInvariant();
    }

    public static Guid CreateUuid5(Guid namespaceId, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Span<byte> namespaceBytes = stackalloc byte[16];
        namespaceId.TryWriteBytes(namespaceBytes, bigEndian: true, out _);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[namespaceBytes.Length + nameBytes.Length];
        namespaceBytes.CopyTo(input);
        nameBytes.CopyTo(input.AsSpan(namespaceBytes.Length));
        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(input, hash);
        hash[6] = (byte)((hash[6] & 0x0f) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
    }

    private static string NormalizeText(string? value)
        => (value ?? string.Empty).Trim().ToUpperInvariant();

    private static bool IsDriveRoot(string value)
        => value.Length == 3
           && char.IsLetter(value[0])
           && value[1] == ':'
           && value[2] == '/';
}

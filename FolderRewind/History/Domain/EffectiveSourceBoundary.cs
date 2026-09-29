using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FolderRewind.History.Domain;

public enum EffectiveBoundaryScopeMode
{
    All = 0,
    Include = 1
}

public enum EffectiveBoundaryFilterMode
{
    Blacklist = 0,
    Whitelist = 1
}

/// <summary>
/// 捕获时受管理文件集合的不可变定义。Exact/Full 均相对此边界成立，而非相对物理根目录。
/// </summary>
public sealed record EffectiveSourceBoundarySnapshot
{
    public const int CurrentSchemaVersion = 1;
    private const string FingerprintDomain = "folderrewind/effective-source-boundary/v1\n";

    public EffectiveSourceBoundarySnapshot(
        EffectiveBoundaryScopeMode scopeMode,
        IEnumerable<string>? scopeRules,
        EffectiveBoundaryFilterMode filterMode,
        IEnumerable<string>? filterRules,
        bool useRegex)
        : this(
            CurrentSchemaVersion,
            scopeMode,
            Freeze(scopeRules),
            filterMode,
            Freeze(filterRules),
            useRegex)
    {
    }

    [JsonConstructor]
    public EffectiveSourceBoundarySnapshot(
        int schemaVersion,
        EffectiveBoundaryScopeMode scopeMode,
        ImmutableArray<string> scopeRules,
        EffectiveBoundaryFilterMode filterMode,
        ImmutableArray<string> filterRules,
        bool useRegex)
    {
        if (schemaVersion != CurrentSchemaVersion)
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        if (scopeMode == EffectiveBoundaryScopeMode.Include && scopeRules.IsDefaultOrEmpty)
            throw new ArgumentException("Include boundary requires at least one scope rule.", nameof(scopeRules));

        SchemaVersion = schemaVersion;
        ScopeMode = scopeMode;
        ScopeRules = scopeRules.IsDefault ? [] : scopeRules;
        FilterMode = filterMode;
        FilterRules = filterRules.IsDefault ? [] : filterRules;
        UseRegex = useRegex;
    }

    public int SchemaVersion { get; }
    public EffectiveBoundaryScopeMode ScopeMode { get; }
    public ImmutableArray<string> ScopeRules { get; }
    public EffectiveBoundaryFilterMode FilterMode { get; }
    public ImmutableArray<string> FilterRules { get; }
    public bool UseRegex { get; }

    [JsonIgnore]
    public string Fingerprint => ComputeFingerprint(this);

    public static EffectiveSourceBoundarySnapshot All { get; } = new(
        EffectiveBoundaryScopeMode.All,
        [],
        EffectiveBoundaryFilterMode.Blacklist,
        [],
        false);

    public static string ComputeFingerprint(EffectiveSourceBoundarySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var canonical = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        var domain = Encoding.UTF8.GetBytes(FingerprintDomain);
        var input = new byte[domain.Length + canonical.Length];
        domain.CopyTo(input, 0);
        canonical.CopyTo(input, domain.Length);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    private static ImmutableArray<string> Freeze(IEnumerable<string>? values)
        => values is null
            ? []
            : [.. values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)];
}

public static class CaptureScopePolicy
{
    public static CaptureScope Determine(
        EffectiveSourceBoundarySnapshot effectiveBoundary,
        EffectiveSourceBoundarySnapshot operationSelection)
    {
        ArgumentNullException.ThrowIfNull(effectiveBoundary);
        ArgumentNullException.ThrowIfNull(operationSelection);
        return StringComparer.Ordinal.Equals(
            effectiveBoundary.Fingerprint,
            operationSelection.Fingerprint)
            ? CaptureScope.FullSource
            : CaptureScope.PartialSource;
    }
}

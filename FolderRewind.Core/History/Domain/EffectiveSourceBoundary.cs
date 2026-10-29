using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FolderRewind.Plugin.Abstractions;

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
    public const int CurrentSchemaVersion = 2;

    public EffectiveSourceBoundarySnapshot(
        EffectiveBoundaryScopeMode scopeMode,
        IEnumerable<string>? scopeRules,
        EffectiveBoundaryFilterMode filterMode,
        IEnumerable<string>? filterRules,
        bool useRegex)
        : this(
            1,
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
        bool useRegex,
        ImmutableArray<PathFilterRule>? typedFilterRules = null)
    {
        if (schemaVersion is not (1 or 2) || schemaVersion == 1 && typedFilterRules is not null
            || schemaVersion == 2 && (typedFilterRules is null || typedFilterRules.Value.IsDefault || !filterRules.IsDefaultOrEmpty))
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        if (typedFilterRules is { } rules && rules.Any(rule => rule is null || rule.Pattern is null || !Enum.IsDefined(rule.Target)
            || rule.SemanticsVersion is not (1 or 2) || rule.SemanticsVersion == 1 && rule.Target != PathFilterTarget.Any))
            throw new ArgumentException("Invalid typed historical filter rules.", nameof(typedFilterRules));
        if (scopeMode == EffectiveBoundaryScopeMode.Include && scopeRules.IsDefaultOrEmpty)
            throw new ArgumentException("Include boundary requires at least one scope rule.", nameof(scopeRules));

        SchemaVersion = schemaVersion;
        ScopeMode = scopeMode;
        ScopeRules = scopeRules.IsDefault ? [] : scopeRules;
        FilterMode = filterMode;
        FilterRules = filterRules.IsDefault ? [] : filterRules;
        UseRegex = useRegex;
        TypedFilterRules = typedFilterRules;
        Rules = schemaVersion == 1 ? [.. FilterRules.Select(PathFilterRule.Legacy)] : typedFilterRules!.Value;
    }

    public int SchemaVersion { get; }
    public EffectiveBoundaryScopeMode ScopeMode { get; }
    public ImmutableArray<string> ScopeRules { get; }
    public EffectiveBoundaryFilterMode FilterMode { get; }
    public ImmutableArray<string> FilterRules { get; }
    public bool UseRegex { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<PathFilterRule>? TypedFilterRules { get; }
    [JsonIgnore]
    public ImmutableArray<PathFilterRule> Rules { get; }

    public static EffectiveSourceBoundarySnapshot CreateTyped(EffectiveBoundaryScopeMode scopeMode, IEnumerable<string>? scopeRules,
        EffectiveBoundaryFilterMode filterMode, IEnumerable<PathFilterRule>? rules, bool useRegex)
        => new(2, scopeMode, Freeze(scopeRules), filterMode, [], useRegex,
            [.. (rules ?? []).Distinct().OrderBy(r => r.SemanticsVersion).ThenBy(r => r.Target).ThenBy(r => r.Pattern, StringComparer.Ordinal)]);

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
        var domain = Encoding.UTF8.GetBytes($"folderrewind/effective-source-boundary/v{snapshot.SchemaVersion}\n");
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

using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.Plugin.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using FolderRewind.Services;

namespace FolderRewind.History.Merge;

/// <summary>Proof against immutable historical boundaries. No current configuration or live inventory participates.</summary>
internal sealed class SemanticMergeCoverage(EffectiveSourceBoundarySnapshot boundary, string comparisonRoot, IEnumerable<string>? fixedInventory = null)
{
    private readonly Dictionary<string, MergeCoverageResult> _cache = new(StringComparer.Ordinal);
    private readonly HashSet<string>? _inventory = fixedInventory?.ToHashSet(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _requiredAbsent = new(StringComparer.OrdinalIgnoreCase);
    internal string[] RequiredAbsentPaths { get { lock (_cache) return _requiredAbsent.Order(StringComparer.Ordinal).ToArray(); } }
    internal string Fingerprint => boundary.Fingerprint;
    internal MergePathPresence Inspect(string path, bool? isDirectory = null)
    {
        SemanticMergeSourceView.Canonical(path);
        if (!Path.IsPathFullyQualified(comparisonRoot) || !Enum.IsDefined(boundary.ScopeMode) || !Enum.IsDefined(boundary.FilterMode)) return MergePathPresence.Unknown;
        var scope = new HistoryRestoreTreeScope(new(SourceId.New(), comparisonRoot, boundary));
        var file = scope.Includes(path);
        var directory = scope.Includes(path, isDirectory: true);
        if (isDirectory is null && file != directory) return MergePathPresence.Unknown;
        return (isDirectory == true ? directory : file) ? MergePathPresence.Absent : MergePathPresence.Filtered;
    }
    internal MergeCoverageResult Check(MergeCoverageQuery query, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        query = query with { Patterns = query.Patterns.ToArray(), IgnoredBlacklistRules = query.IgnoredBlacklistRules.ToArray() };
        if (string.IsNullOrWhiteSpace(query.RequirementId) || query.RequirementId.Length > 512
            || query.Patterns.Count is < 1 or > 64 || query.Patterns.Sum(p => p.Length) > 16_384)
            throw new ArgumentException("Invalid Merge coverage query.");
        if (query.IgnoredBlacklistRules.Count > 64 || query.IgnoredBlacklistRules.Any(r => r is null || !boundary.Rules.Contains(r)))
            throw new ArgumentException("Ignored blacklist rules must identify exact historical rules.");
        if (query.RelativeRoot.Length != 0) SemanticMergeSourceView.Canonical(query.RelativeRoot);
        if (query.Patterns.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 1024 || p.StartsWith('/') || p.EndsWith('/')
            || p.Any(c => char.IsControl(c) || c is '\\' or ':' or '[' or ']') || p.Split('/').Any(s => s is "" or "." or "..")))
            throw new ArgumentException("Unsupported Merge coverage pattern.");
        var key = JsonSerializer.Serialize(new { query.RelativeRoot, Patterns = query.Patterns.ToArray(), query.IgnoredBlacklistRules });
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached with { RequirementId = query.RequirementId };
            MergeCoverageResult result;
            try
            {
                result = Prove(query, token);
                if (result.State != MergeCoverageState.Complete && _inventory is not null)
                {
                    var absent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var remaining = query.Patterns.Where(pattern => !AbsentAncestor(query.RelativeRoot, pattern, absent)).ToArray();
                    if (remaining.Length < query.Patterns.Count)
                    {
                        var reduced = remaining.Length == 0 ? new(query.RequirementId, Fingerprint, MergeCoverageState.Complete, Array.Empty<MergeCoverageEvidence>())
                            : Prove(query with { Patterns = remaining }, token);
                        if (reduced.State == MergeCoverageState.Complete)
                        {
                            if (_requiredAbsent.Union(absent, StringComparer.OrdinalIgnoreCase).Count() > 1024)
                                throw new MergePathLanguage.LimitException();
                            _requiredAbsent.UnionWith(absent);
                            result = reduced with { Evidence = Array.AsReadOnly(absent.Order(StringComparer.Ordinal)
                                .Select(p => new MergeCoverageEvidence("covered_absent_directory", null, p)).ToArray()) };
                        }
                    }
                }
            }
            catch (MergePathLanguage.LimitException) { result = Result(query, MergeCoverageState.Unknown, "proof_budget", null, null); }
            if (_cache.Count < 1024) _cache.Add(key, result);
            return result;
        }
    }
    private bool AbsentAncestor(string root, string pattern, HashSet<string> absent)
    {
        var parts = (root.Length == 0 ? pattern : root + "/" + pattern).Split('/');
        var prefix = "";
        // Only literal directory ancestors can prove a whole family absent. A missing file
        // matching a wildcard never proves that filtered siblings were absent at capture.
        foreach (var segment in parts.SkipLast(1))
        {
            if (segment.Contains('*') || segment.Contains('?')) break;
            prefix = prefix.Length == 0 ? segment : prefix + "/" + segment;
            if (_inventory!.Contains(prefix) || _inventory.Any(p => p.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))) continue;
            if (Inspect(prefix, isDirectory: true) != MergePathPresence.Absent) continue;
            absent.Add(prefix); return true;
        }
        return false;
    }
    private MergeCoverageResult Prove(MergeCoverageQuery query, CancellationToken token)
    {
        if (!Enum.IsDefined(boundary.ScopeMode) || !Enum.IsDefined(boundary.FilterMode))
            return Result(query, MergeCoverageState.Unknown, "unsupported_boundary", null, null);
        var required = new MergePathLanguage();
        foreach (var pattern in query.Patterns) required.AddGlob(query.RelativeRoot, pattern);
        if (boundary.ScopeMode == EffectiveBoundaryScopeMode.Include)
        {
            var scope = new MergePathLanguage();
            foreach (var pattern in boundary.ScopeRules)
            {
                if (pattern.Contains('[')) return Result(query, MergeCoverageState.Unknown, "unsupported_scope_rule", pattern, null);
                scope.AddGlob("", pattern);
            }
            if (MergePathLanguage.Counterexample(required, scope, true, token) is { } missing)
                return Result(query, MergeCoverageState.Filtered, "outside_scope", string.Join("; ", boundary.ScopeRules.Take(8)), missing);
        }
        var whitelist = new MergePathLanguage();
        foreach (var entry in boundary.Rules)
        {
            token.ThrowIfCancellationRequested();
            if (boundary.FilterMode == EffectiveBoundaryFilterMode.Blacklist && query.IgnoredBlacklistRules.Contains(entry)) continue;
            var raw = entry.Pattern;
            if (entry.SemanticsVersion == 2)
            {
                var parsed = PathFilterPattern.Parse(entry, comparisonRoot);
                if (!parsed.Applicable || parsed.IsRegex && !boundary.UseRegex) continue;
                if (parsed.IsRegex) return Result(query, MergeCoverageState.Unknown, "unsupported_regex", raw, null);
                if (parsed.Pattern.Any(char.IsSurrogate)) return Result(query, MergeCoverageState.Unknown, "unsupported_unicode_literal", raw, null);
                var typed = boundary.FilterMode == EffectiveBoundaryFilterMode.Blacklist ? new MergePathLanguage() : whitelist;
                typed.AddTypedFilter(parsed);
                if (boundary.FilterMode == EffectiveBoundaryFilterMode.Blacklist
                    && MergePathLanguage.Counterexample(required, typed, false, token) is { } typedExcluded)
                    return Result(query, MergeCoverageState.Filtered, "blacklist_overlap", raw, typedExcluded);
                continue;
            }
            var rule = raw.Trim().Replace('\\', '/').Trim('/');
            if (rule.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
            {
                if (!boundary.UseRegex) continue;
                return Result(query, MergeCoverageState.Unknown, "unsupported_regex", raw, null);
            }
            if (Path.IsPathRooted(raw) || rule.Contains(':')) return Result(query, MergeCoverageState.Unknown, "absolute_filter_rule", raw, null);
            var filter = boundary.FilterMode == EffectiveBoundaryFilterMode.Blacklist ? new MergePathLanguage() : whitelist;
            if (rule.Contains('*') || rule.Contains('?'))
            {
                filter.AddGlob("", rule, filter: true);
                if (!rule.Contains('/')) filter.AddGlob("", rule, filter: true, filename: true);
            }
            else
            {
                if (!Path.IsPathFullyQualified(comparisonRoot)) return Result(query, MergeCoverageState.Unknown, "unknown_capture_root", raw, null);
                if (rule.Any(char.IsSurrogate) || comparisonRoot.Any(char.IsSurrogate))
                    return Result(query, MergeCoverageState.Unknown, "unsupported_unicode_literal", raw, null);
                filter.AddLiteralFilter(rule, comparisonRoot.Replace('\\', '/'));
            }
            if (boundary.FilterMode == EffectiveBoundaryFilterMode.Blacklist
                && MergePathLanguage.Counterexample(required, filter, false, token) is { } excluded)
                return Result(query, MergeCoverageState.Filtered, "blacklist_overlap", raw, excluded);
        }
        if (boundary.FilterMode == EffectiveBoundaryFilterMode.Whitelist
            && MergePathLanguage.Counterexample(required, whitelist, true, token) is { } outside)
            return Result(query, MergeCoverageState.Filtered, "outside_whitelist", string.Join("; ", boundary.Rules.Take(8)), outside);
        return new(query.RequirementId, Fingerprint, MergeCoverageState.Complete, []);
    }
    private MergeCoverageResult Result(MergeCoverageQuery query, MergeCoverageState state, string code, string? rule, string? path)
        => new(query.RequirementId, Fingerprint, state, Array.AsReadOnly(new[] { new MergeCoverageEvidence(code, rule, path) }));
}

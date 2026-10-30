using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using FolderRewind.Services;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.History.Merge;

/// <summary>Bounded regular-language proof for the supported historical glob grammar, never a sample-based proof.</summary>
internal sealed class MergePathLanguage
{
    private sealed record Edge(int To, char? Literal = null, bool Any = false, bool Segment = false);
    private readonly List<List<Edge>> _edges = [[]];
    private readonly List<List<int>> _epsilon = [[]];
    private readonly HashSet<int> _accept = [];
    private readonly HashSet<int> _starts = [0];
    private readonly HashSet<char> _alphabet = ['/'];
    private static readonly ConcurrentDictionary<(char Character, bool Ordinal), char[]> Cases = new();
    internal sealed class LimitException : Exception { }
    private int NewState()
    {
        if (_edges.Count >= 4096) throw new LimitException();
        _edges.Add([]); _epsilon.Add([]); return _edges.Count - 1;
    }
    private int Literal(int from, char c, bool ordinal = false)
    {
        var next = NewState();
        foreach (var value in CaseClass(c, ordinal)) { _edges[from].Add(new(next, value)); _alphabet.Add(value); }
        return next;
    }
    private static char[] CaseClass(char c, bool ordinal)
    {
        if (!char.IsLetter(c)) return [c];
        if (Cases.TryGetValue((c, ordinal), out var known)) return known;
        // Match the RegexOptions.IgnoreCase | CultureInvariant used by the persisted Host matcher,
        // including non-ASCII equivalents of ASCII letters. One representative ASCII alphabet is insufficient.
        var literal = c.ToString();
        var regex = new Regex("\\A" + Regex.Escape(literal) + "\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var result = new List<char>(); Span<char> candidate = stackalloc char[1];
        for (var n = 0; n <= char.MaxValue; n++)
        { candidate[0] = (char)n; if (ordinal ? candidate.Equals(literal.AsSpan(), StringComparison.OrdinalIgnoreCase) : regex.IsMatch(candidate)) result.Add((char)n); }
        var values = result.ToArray();
        if (Cases.Count < 256) Cases.TryAdd((c, ordinal), values);
        return values;
    }
    private int Star(int from, bool segment)
    {
        var next = NewState(); _epsilon[from].Add(next); _edges[from].Add(new(from, Any: !segment, Segment: segment)); return next;
    }
    private int Directories(int from)
    {
        var next = NewState(); var loop = NewState();
        _epsilon[from].Add(next); _epsilon[from].Add(loop);
        _edges[loop].Add(new(loop, Any: true)); _edges[loop].Add(new(next, '/')); return next;
    }
    private int Glob(int from, string pattern, bool filter, bool ordinalLiterals = false)
    {
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*')
            {
                var deep = !filter && i + 1 < pattern.Length && pattern[i + 1] == '*';
                if (deep && i + 2 < pattern.Length && pattern[i + 2] == '/') { from = Directories(from); i += 2; }
                else { from = Star(from, !filter && !deep); if (deep) i++; }
            }
            else if (c == '?')
            { var next = NewState(); _edges[from].Add(new(next, Any: filter, Segment: !filter)); from = next; }
            else from = Literal(from, c, ordinalLiterals);
        }
        return from;
    }
    internal void AddTypedFilter(PathFilterPattern pattern)
    {
        var start = NewState(); _epsilon[0].Add(start);
        if (pattern.NameOnly) start = Directories(start);
        var end = Glob(start, pattern.Pattern, filter: false, ordinalLiterals: pattern.IsLiteral);
        if (pattern.Rule.Target != PathFilterTarget.Directory) _accept.Add(end);
        if (pattern.Rule.Target != PathFilterTarget.File)
        {
            end = Literal(end, '/');
            _accept.Add(Star(end, false));
        }
    }
    internal void AddGlob(string literalRoot, string pattern, bool filter = false, bool filename = false)
    {
        var start = NewState(); _epsilon[0].Add(start);
        foreach (var c in literalRoot) start = Literal(start, c);
        if (literalRoot.Length > 0) start = Literal(start, '/');
        if (filename) start = Directories(start);
        _accept.Add(Glob(start, pattern, filter));
    }
    internal void AddLiteralFilter(string rule, string comparisonRoot)
    {
        var branch = NewState(); _epsilon[0].Add(branch);
        var end = Directories(branch);
        foreach (var c in rule) end = Literal(end, c, ordinal: true);
        _accept.Add(end);
        end = Literal(end, '/'); end = Star(end, false); _accept.Add(end);
        // Literal rules also match segments of the original absolute capture path, including
        // rules which begin in an ancestor and end inside the relative source tree.
        var states = Closure([branch]);
        foreach (var c in comparisonRoot.TrimEnd('/') + '/') states = Move(states, c);
        _starts.UnionWith(states);
    }
    private int[] Closure(IEnumerable<int> states)
    {
        var seen = states.ToHashSet(); var todo = new Stack<int>(seen);
        while (todo.TryPop(out var state)) foreach (var next in _epsilon[state]) if (seen.Add(next)) todo.Push(next);
        return seen.Order().ToArray();
    }
    private int[] Move(int[] states, char c) => Closure(states.SelectMany(s => _edges[s])
        .Where(e => e.Any || e.Segment && c != '/' || e.Literal == c).Select(e => e.To));
    private sealed record Node(int[] Required, int[] Other, int Previous, char Character);

    /// <summary>Find a member of required ∩ other, or required \ other. Null is a complete proof of emptiness.</summary>
    internal static string? Counterexample(MergePathLanguage required, MergePathLanguage other, bool difference, CancellationToken token)
    {
        var alphabet = required._alphabet.Concat(other._alphabet).ToHashSet();
        if (alphabet.Count > 512) throw new LimitException();
        var extra = '~'; while (alphabet.Contains(extra)) extra++;
        alphabet.Add(extra);
        var symbols = alphabet.Order().ToArray();
        var nodes = new List<Node> { new(required.Closure(required._starts), other.Closure(other._starts), -1, '\0') };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var steps = 0;
        for (var index = 0; index < nodes.Count; index++)
        {
            token.ThrowIfCancellationRequested(); var node = nodes[index];
            if (node.Required.Any(required._accept.Contains) && (node.Other.Any(other._accept.Contains) != difference))
            {
                var value = new StringBuilder(); var current = index;
                while (nodes[current].Previous >= 0) { value.Append(nodes[current].Character); current = nodes[current].Previous; }
                return new string(value.ToString().Reverse().ToArray());
            }
            foreach (var symbol in symbols)
            {
                if (++steps > 500_000 || nodes.Count >= 16_384) throw new LimitException();
                var a = required.Move(node.Required, symbol); if (a.Length == 0) continue;
                var b = other.Move(node.Other, symbol); if (!difference && b.Length == 0) continue;
                var key = string.Join(',', a) + "|" + string.Join(',', b);
                if (seen.Add(key)) nodes.Add(new(a, b, index, symbol));
            }
        }
        return null;
    }
}

using System;
using System.Collections.Generic;

namespace FolderRewind.History.Storage;

/// <summary>操作内的相对路径索引；按路径段比较，保留最后写入的原始拼写。不是磁盘身份或完整性证据。</summary>
internal sealed class RestorePathIndex<T>
{
    private sealed class Node(Node? parent = null, string segment = "")
    {
        internal readonly Dictionary<string, Node> Children = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Node? Parent = parent;
        internal readonly string Segment = segment;
        internal string Path = "";
        internal T Value = default!;
        internal bool HasValue;
        internal int Count;
    }
    private readonly Node _root = new();
    internal int Count => _root.Count;

    internal void Set(string path, T value)
    {
        var node = Find(path, create: true)!;
        if (!node.HasValue)
            for (var ancestor = node; ancestor is not null; ancestor = ancestor.Parent) ancestor.Count++;
        node.HasValue = true; node.Path = path; node.Value = value;
    }

    internal bool Contains(string path) => Find(path)?.HasValue == true;
    internal bool HasDescendants(string path)
        => Find(path) is { } node && node.Count > (node.HasValue ? 1 : 0);
    internal bool HasAncestor(string path, bool includeSelf = false)
    {
        var node = _root;
        int start = 0;
        while (start < path.Length)
        {
            if (node.HasValue) return true;
            int end = path.IndexOf('/', start); if (end < 0) end = path.Length;
            if (!node.Children.TryGetValue(path[start..end], out node!)) return false;
            start = end + 1;
        }
        return includeSelf && node.HasValue;
    }

    internal IReadOnlyList<KeyValuePair<string, T>> EntriesUnder(string path, bool includeSelf = false)
    {
        var result = new List<KeyValuePair<string, T>>();
        if (Find(path) is not { } root) return result;
        var pending = new Stack<Node>(); pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (node.HasValue && (includeSelf || node != root)) result.Add(new(node.Path, node.Value));
            foreach (var child in node.Children.Values) pending.Push(child);
        }
        return result;
    }

    internal IReadOnlyList<KeyValuePair<string, T>> RemoveSubtree(string path)
    {
        var entries = EntriesUnder(path, includeSelf: true);
        if (Find(path) is not { } node || node.Count == 0) return entries;
        int removed = node.Count;
        for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent) ancestor.Count -= removed;
        if (node.Parent is { } parent)
        {
            parent.Children.Remove(node.Segment);
            while (parent.Parent is { } above && parent.Count == 0)
            { above.Children.Remove(parent.Segment); parent = above; }
        }
        else { node.Children.Clear(); node.Count = 0; node.HasValue = false; node.Value = default!; }
        return entries;
    }

    private Node? Find(string path, bool create = false)
    {
        var node = _root;
        int start = 0;
        while (start < path.Length)
        {
            int end = path.IndexOf('/', start); if (end < 0) end = path.Length;
            var segment = path[start..end];
            if (!node.Children.TryGetValue(segment, out var child))
            {
                if (!create) return null;
                node.Children.Add(segment, child = new(node, segment));
            }
            node = child; start = end + 1;
        }
        return node;
    }

    internal static bool IsBelow(string path, string parent)
        => path.Length > parent.Length && path[parent.Length] == '/'
            && path.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
}

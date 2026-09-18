using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace FolderRewind.Services;

public static class ConfigMutationProtection
{
    public static object Sync { get; } = new();
    private static readonly Dictionary<object, int> Frozen = new(ReferenceEqualityComparer.Instance);
    public static bool Set<T>(object owner, ref T field, T value)
    {
        lock (Sync)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            RequireWritable(owner); field = value; return true;
        }
    }
    public static void RequireWritable(object owner)
    {
        lock (Sync) if (Frozen.ContainsKey(owner)) throw new InvalidOperationException("Configuration is in use by a workspace transaction.");
    }
    public static IDisposable Freeze(IEnumerable<object> objects)
    {
        lock (Sync)
        {
            var items = objects.Distinct(ReferenceEqualityComparer.Instance).ToArray();
            foreach (var item in items) Frozen[item] = Frozen.GetValueOrDefault(item) + 1;
            return new Lease(items);
        }
    }
    private sealed class Lease(object[] items) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            lock (Sync)
            {
                if (_disposed) return; _disposed = true;
                foreach (var item in items) if (--Frozen[item] == 0) Frozen.Remove(item);
            }
        }
    }
}

public sealed class GuardedObservableCollection<T> : ObservableCollection<T>
{
    public GuardedObservableCollection() { }
    public GuardedObservableCollection(IEnumerable<T> values) : base(values) { }
    public static ObservableCollection<T> Wrap(IEnumerable<T>? values) => values is GuardedObservableCollection<T> guarded
        ? guarded : new GuardedObservableCollection<T>(values ?? []);
    protected override void InsertItem(int index, T item) { lock (ConfigMutationProtection.Sync) { ConfigMutationProtection.RequireWritable(this); base.InsertItem(index, item); } }
    protected override void SetItem(int index, T item) { lock (ConfigMutationProtection.Sync) { ConfigMutationProtection.RequireWritable(this); base.SetItem(index, item); } }
    protected override void RemoveItem(int index) { lock (ConfigMutationProtection.Sync) { ConfigMutationProtection.RequireWritable(this); base.RemoveItem(index); } }
    protected override void ClearItems() { lock (ConfigMutationProtection.Sync) { ConfigMutationProtection.RequireWritable(this); base.ClearItems(); } }
    protected override void MoveItem(int oldIndex, int newIndex) { lock (ConfigMutationProtection.Sync) { ConfigMutationProtection.RequireWritable(this); base.MoveItem(oldIndex, newIndex); } }
}

public sealed class GuardedDictionary<T> : IDictionary<string, T>
{
    private readonly Dictionary<string, T> _values;
    public GuardedDictionary(IEnumerable<KeyValuePair<string, T>>? values = null)
        => _values = new Dictionary<string, T>(values ?? [], StringComparer.OrdinalIgnoreCase);
    public T this[string key] { get => _values[key]; set { lock (ConfigMutationProtection.Sync) { ConfigMutationProtection.RequireWritable(this); _values[key] = value; } } }
    public ICollection<string> Keys => _values.Keys;
    public ICollection<T> Values => _values.Values;
    public int Count => _values.Count;
    public bool IsReadOnly => false;
    public void Add(string key, T value) { lock (ConfigMutationProtection.Sync) { ConfigMutationProtection.RequireWritable(this); _values.Add(key, value); } }
    public void Add(KeyValuePair<string, T> item) => Add(item.Key, item.Value);
    public bool Remove(string key) { lock (ConfigMutationProtection.Sync) { ConfigMutationProtection.RequireWritable(this); return _values.Remove(key); } }
    public bool Remove(KeyValuePair<string, T> item) { lock (ConfigMutationProtection.Sync) { ConfigMutationProtection.RequireWritable(this); return ((ICollection<KeyValuePair<string, T>>)_values).Remove(item); } }
    public void Clear() { lock (ConfigMutationProtection.Sync) { ConfigMutationProtection.RequireWritable(this); _values.Clear(); } }
    public bool ContainsKey(string key) => _values.ContainsKey(key);
    public bool TryGetValue(string key, out T value) => _values.TryGetValue(key, out value!);
    public bool Contains(KeyValuePair<string, T> item) => ((ICollection<KeyValuePair<string, T>>)_values).Contains(item);
    public void CopyTo(KeyValuePair<string, T>[] array, int index) => ((ICollection<KeyValuePair<string, T>>)_values).CopyTo(array, index);
    public IEnumerator<KeyValuePair<string, T>> GetEnumerator() => _values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

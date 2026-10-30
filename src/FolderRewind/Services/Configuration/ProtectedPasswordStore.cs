using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FolderRewind.Services;

/// <summary>Device protection is injected so persistence failures can be exercised without changing a user's store.</summary>
internal sealed class ProtectedPasswordStore(string path, Func<byte[], byte[]> protect, Func<byte[], byte[]> unprotect)
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private object Gate => Gates.GetOrAdd(Path.GetFullPath(path), _ => new());
    public string? Read(string id)
    {
        lock (Gate)
        {
            var store = Load();
            if (!store.TryGetValue(id, out var value)) return null;
            var plain = unprotect(Convert.FromBase64String(value));
            try { return Encoding.UTF8.GetString(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
    }
    public void Write(string id, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(id); ArgumentException.ThrowIfNullOrEmpty(password);
        lock (Gate)
        {
            var store = Load();
            var plain = Encoding.UTF8.GetBytes(password);
            try { store[id] = Convert.ToBase64String(protect(plain)); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            Save(store);
            if (!StringComparer.Ordinal.Equals(Read(id), password)) throw new IOException("Password store readback failed.");
        }
    }
    public void Remove(string id)
    {
        lock (Gate) { var store = Load(); if (store.Remove(id)) Save(store); }
    }
    private Dictionary<string, string> Load() => File.Exists(path)
        ? JsonSerializer.Deserialize(File.ReadAllBytes(path), PasswordStoreJsonContext.Default.DictionaryStringString)
            ?? throw new InvalidDataException("Password store is empty.") : new(StringComparer.Ordinal);
    private void Save(Dictionary<string, string> store)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(store, PasswordStoreJsonContext.Default.DictionaryStringString));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class PasswordStoreJsonContext : JsonSerializerContext;

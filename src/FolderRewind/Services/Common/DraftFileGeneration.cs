using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal static class DraftFileGeneration
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    internal static string Token(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static async Task<string> SaveAsync(string path, byte[] bytes)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await AtomicFileService.WriteAsync(path, (stream, token) => stream.WriteAsync(bytes, token).AsTask()).ConfigureAwait(false);
            return Token(bytes);
        }
        finally { Gate.Release(); }
    }
    internal static async Task<bool> DiscardIfCurrentAsync(string path, string? expected)
    {
        if (string.IsNullOrEmpty(expected)) return false;
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 256 * 1024) return false;
            if (Token(await File.ReadAllBytesAsync(path).ConfigureAwait(false)) != expected) return false;
            File.Delete(path);
            return true;
        }
        finally { Gate.Release(); }
    }
}

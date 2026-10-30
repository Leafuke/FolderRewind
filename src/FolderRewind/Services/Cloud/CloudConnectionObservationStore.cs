using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal sealed record CloudConnectionObservation(string TargetKey, bool Writable, bool Succeeded, DateTimeOffset CheckedAtUtc, string FailureKind);

internal static class CloudConnectionObservationStore
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    internal static string Key(string executable, string configPath, string working, string remote, string revision)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', executable, configPath, working, remote, revision))));
    private static string PathFor(string root, string key)
    {
        if (key.Length != 64 || !System.Linq.Enumerable.All(key, Uri.IsHexDigit)) throw new ArgumentException("Invalid observation key.");
        return Path.Combine(root, "local-state", "cloud-checks", key + ".json");
    }
    internal static CloudConnectionObservation? Load(string root, string key)
    {
        try
        {
            var path = PathFor(root, key);
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return null;
            var observation = JsonSerializer.Deserialize(File.ReadAllBytes(path), CloudObservationJsonContext.Default.CloudConnectionObservation);
            return observation?.TargetKey == key ? observation : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    internal static async Task SaveAsync(string root, CloudConnectionObservation observation)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Load(root, observation.TargetKey) is { } prior && prior.CheckedAtUtc > observation.CheckedAtUtc) return;
            var path = PathFor(root, observation.TargetKey);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(observation, CloudObservationJsonContext.Default.CloudConnectionObservation);
            await AtomicFileService.WriteAsync(path, (stream, token) => stream.WriteAsync(bytes, token).AsTask()).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }
}

[JsonSerializable(typeof(CloudConnectionObservation))]
internal partial class CloudObservationJsonContext : JsonSerializerContext;

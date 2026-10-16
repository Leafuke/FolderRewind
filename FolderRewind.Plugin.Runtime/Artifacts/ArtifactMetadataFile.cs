using System.Text.Json;

namespace FolderRewind.Plugin.Runtime.Artifacts;

/// <summary>Reads an immutable-size metadata snapshot without trusting a prior filesystem stat.</summary>
public static class ArtifactMetadataFile
{
    public static byte[] ReadBytes(string path, int maximumBytes)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > maximumBytes) throw new InvalidDataException("Artifact metadata quota exceeded.");
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw new InvalidDataException("Artifact metadata changed during inspection.");
        return bytes;
    }

    public static async Task<byte[]> ReadStreamAsync(Stream input, int maximumBytes, CancellationToken token)
    {
        using var output = new MemoryStream(); var buffer = new byte[16384]; int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > maximumBytes) throw new InvalidDataException("Artifact metadata quota exceeded.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    public static T? ReadJson<T>(string path, int maximumBytes, JsonSerializerOptions? options = null)
        => JsonSerializer.Deserialize<T>(ReadBytes(path, maximumBytes), options);
}

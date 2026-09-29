using FolderRewind.History.Domain;
using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace FolderRewind.History.Storage;

public sealed record HistoryRepositoryDescriptor(string Magic, int FormatVersion, HistoryConfigId ConfigId)
{
    public const string CurrentMagic = "FolderRewindHistoryRepository";
    public const int CurrentFormatVersion = 1;

    public static HistoryRepositoryDescriptor Create(HistoryConfigId configId)
        => new(CurrentMagic, CurrentFormatVersion, configId);

    public byte[] ToCanonicalBytes()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("magic", CurrentMagic);
            writer.WriteNumber("formatVersion", CurrentFormatVersion);
            writer.WriteString("configId", ConfigId.Value);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static HistoryRepositoryDescriptor Parse(ReadOnlySpan<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes.ToArray());
        var root = document.RootElement;
        var descriptor = new HistoryRepositoryDescriptor(
            root.GetProperty("magic").GetString() ?? string.Empty,
            root.GetProperty("formatVersion").GetInt32(),
            new HistoryConfigId(root.GetProperty("configId").GetString() ?? string.Empty));
        if (!StringComparer.Ordinal.Equals(descriptor.Magic, CurrentMagic)
            || descriptor.FormatVersion != CurrentFormatVersion)
        {
            throw new HistoryPackCompatibilityException("Unsupported history repository descriptor.");
        }

        return descriptor;
    }
}

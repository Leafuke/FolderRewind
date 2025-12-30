using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace FolderRewind.Models
{
    [JsonSerializable(typeof(AppConfig))]
    [JsonSerializable(typeof(BackupConfig))]
    [JsonSerializable(typeof(BackupMetadata))]
    [JsonSerializable(typeof(FileState))]
    [JsonSerializable(typeof(GlobalSettings))]
    [JsonSerializable(typeof(ManagedFolder))]
    [JsonSerializable(typeof(HistoryItem))]
    [JsonSerializable(typeof(List<HistoryItem>))]
    [JsonSourceGenerationOptions(WriteIndented = true)]
    internal partial class AppJsonContext : JsonSerializerContext
    {
    }
}

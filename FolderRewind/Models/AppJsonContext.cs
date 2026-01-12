using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace FolderRewind.Models
{
    [JsonSerializable(typeof(AppConfig))]
    [JsonSerializable(typeof(BackupConfig))]
    [JsonSerializable(typeof(BackupMetadata))]
    [JsonSerializable(typeof(FileState))]
    [JsonSerializable(typeof(GlobalSettings))]
    [JsonSerializable(typeof(ArchiveSettings))]
    [JsonSerializable(typeof(AutomationSettings))]
    [JsonSerializable(typeof(FilterSettings))]
    [JsonSerializable(typeof(ManagedFolder))]
    [JsonSerializable(typeof(HistoryItem))]
    [JsonSerializable(typeof(List<HistoryItem>))]
    [JsonSerializable(typeof(PluginHostSettings))]
    [JsonSerializable(typeof(PluginInstallManifest))]
    [JsonSerializable(typeof(PluginSettingDefinition))]
    [JsonSerializable(typeof(InstalledPluginInfo))]
    [JsonSerializable(typeof(Dictionary<string, bool>))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    [JsonSerializable(typeof(Dictionary<string, Dictionary<string, string>>))]
    [JsonSourceGenerationOptions(WriteIndented = true)]
    internal partial class AppJsonContext : JsonSerializerContext
    {
    }
}

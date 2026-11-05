using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Models;

// WinUI-free persisted fields used by the linked Host repair service.
public enum BackupFilterMode { Blacklist, Whitelist }
public sealed class FilterSettings : ObservableObject
{
    public BackupFilterMode BackupFilterMode { get; set; }
    public ObservableCollection<PathFilterRule> Blacklist { get; set; } = [];
    public ObservableCollection<PathFilterRule> BackupWhitelist { get; set; } = [];
    public ObservableCollection<PathFilterRule> RestoreWhitelist { get; set; } = [];
    public bool UseRegex { get; set; }
}

[JsonSerializable(typeof(ManagedFolder))]
[JsonSerializable(typeof(FilterSettings))]
internal partial class AppJsonContext : JsonSerializerContext;

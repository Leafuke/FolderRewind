using System.Collections.Generic;

namespace FolderRewind.Models;

public enum BackupSetupStage { Content, Location, Review, Result }
public sealed class BackupSetupSession
{
    public int SchemaVersion { get; set; } = 1;
    public BackupSetupStage Stage { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public List<string> SourcePaths { get; set; } = [];
    public string PresetShareId { get; set; } = string.Empty;
    public ConfigKindReference Kind { get; set; } = new();
    public bool IsEncrypted { get; set; }
    public string IconGlyph { get; set; } = "\uE8B7";
    public GameDiscoveryNavigationParameter? DiscoveryReentry { get; set; }
    public List<BackupSetupDraftSelection> Selections { get; set; } = [];
}

// Only host-owned choices are persisted. Provider payloads, cloud settings, credentials, and previous readiness are excluded.
public sealed class BackupSetupDraftSelection
{
    public DiscoverySetIdentity Identity { get; set; } = new();
    public string Name { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public ConfigKindReference Kind { get; set; } = new();
    public bool IsEncrypted { get; set; }
    public string IconGlyph { get; set; } = "\uE8B7";
}

// 非持久输入可携带预设已生成的完整草稿，不伪造发现身份。
public sealed record BackupSetupNavigationParameter(BackupConfig? Draft = null, string? PresetShareId = null,
    IReadOnlyList<BackupConfig>? Drafts = null, GameDiscoveryNavigationParameter? DiscoveryReentry = null);

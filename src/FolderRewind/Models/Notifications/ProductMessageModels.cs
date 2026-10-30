using FolderRewind.Services.Plugins;
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FolderRewind.Models;

public sealed class ProductMessageState
{
    public bool IsRead { get; set; }
    public bool Archived { get; set; }
    public bool Ignored { get; set; }
    public bool Discovered { get; set; }
    public bool AlertDelivered { get; set; }
    public string ReminderRevision { get; set; } = "";
    public DateTimeOffset? SnoozedUntil { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    public ProductMessageState Copy() => (ProductMessageState)MemberwiseClone();
}

internal sealed class AnnouncementDocument
{
    public int SchemaVersion { get; set; }
    public List<AnnouncementItem> Items { get; set; } = [];
}

internal sealed class AnnouncementItem
{
    public string Id { get; set; } = "";
    public string Revision { get; set; } = "";
    public DateTimeOffset PublishedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string? MinVersion { get; set; }
    public string? MaxVersionExclusive { get; set; }
    public List<string> Channels { get; set; } = [];
    public string Kind { get; set; } = "general";
    public string? ReleaseVersion { get; set; }
    public List<string> LegacyVersions { get; set; } = [];
    public Dictionary<string, AnnouncementText> Locales { get; set; } = new();
    [JsonIgnore] public string Key => "notice:" + Id;
}

internal sealed class AnnouncementText
{
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Body { get; set; } = "";
}

internal sealed class ProductMessageCacheDocument
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset NoticesFetchedUtc { get; set; }
    public AnnouncementDocument Notices { get; set; } = new() { SchemaVersion = 1 };
    public DateTimeOffset UpdateFetchedUtc { get; set; }
    public GitHubReleaseService.GitHubLatestReleaseResult? Release { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AnnouncementDocument))]
[JsonSerializable(typeof(ProductMessageCacheDocument))]
internal partial class ProductMessageJsonContext : JsonSerializerContext;

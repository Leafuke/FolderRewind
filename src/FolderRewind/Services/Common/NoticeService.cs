using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

/// <summary>Versioned, bounded announcement content. User choices belong to ProductMessageService.</summary>
internal static class NoticeService
{
    internal const string IndexUrl = "https://raw.githubusercontent.com/Leafuke/FolderRewind/dev/announcements/v1/index.json";
    internal const int MaximumBytes = 2 * 1024 * 1024;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    internal static AnnouncementDocument Document { get; private set; } = new() { SchemaVersion = 1 };
    internal static DateTimeOffset FetchedUtc { get; private set; }
    internal static bool LastCheckFailed { get; private set; }

    internal static async Task CheckAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, IndexUrl);
            request.Headers.UserAgent.ParseAdd("FolderRewind-Announcements/1.0");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumBytes) throw new InvalidDataException("Announcement index is too large.");
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            var bytes = await ReadBoundedAsync(input, ct);
            var document = JsonSerializer.Deserialize(bytes, ProductMessageJsonContext.Default.AnnouncementDocument);
            if (!Validate(document)) throw new InvalidDataException("Invalid announcement index.");
            Document = document!; FetchedUtc = DateTimeOffset.UtcNow; LastCheckFailed = false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            LastCheckFailed = true;
            LogService.LogError("Announcement refresh failed; retaining valid cached content.", nameof(NoticeService), error);
        }
    }

    internal static async Task<byte[]> ReadBoundedAsync(Stream input, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[16384]; int count;
        while ((count = await input.ReadAsync(buffer, ct)) != 0)
        {
            if (output.Length + count > MaximumBytes) throw new InvalidDataException("Product cache exceeds its size bound.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    internal static bool Validate(AnnouncementDocument? document)
    {
        if (document is null || document.SchemaVersion != 1 || document.Items is null || document.Items.Count > 50) return false;
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in document.Items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 100
                || !item.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
                || !identities.Add(item.Id) || string.IsNullOrWhiteSpace(item.Revision) || item.Revision.Length > 100
                || item.PublishedAt == default || item.ExpiresAt <= item.PublishedAt
                || item.Kind is not ("general" or "release" or "criticalCompatibility")
                || item.Channels is null || item.Channels.Count == 0
                || item.Channels.Any(c => c is not ("Store" or "Setup" or "Sideload" or "Developer" or "Unknown"))
                || item.Locales is null || item.LegacyVersions is null) return false;
            if (item.MinVersion is not null && !Version.TryParse(item.MinVersion, out _)
                || item.MaxVersionExclusive is not null && !Version.TryParse(item.MaxVersionExclusive, out _)
                || item.MinVersion is not null && item.MaxVersionExclusive is not null
                   && Version.Parse(item.MinVersion) >= Version.Parse(item.MaxVersionExclusive)) return false;
            if (item.Kind == "criticalCompatibility" && (item.MinVersion is null || item.MaxVersionExclusive is null)) return false;
            if (item.Kind == "release" && AppUpdatePolicy.ParseReleaseVersion("v" + item.ReleaseVersion) is null) return false;
            foreach (var locale in new[] { "zh-CN", "en-US" })
                if (!item.Locales.TryGetValue(locale, out var text) || text is null || string.IsNullOrWhiteSpace(text.Title)
                    || text.Title.Length > 256 || string.IsNullOrWhiteSpace(text.Summary) || text.Summary.Length > 1024
                    || string.IsNullOrWhiteSpace(text.Body) || text.Body.Length > 32768) return false;
        }
        return true;
    }

    internal static void Restore(AnnouncementDocument document, DateTimeOffset fetched)
    { if (Validate(document) && fetched <= DateTimeOffset.UtcNow.AddMinutes(5)) { Document = document; FetchedUtc = fetched; } }

    internal static IReadOnlyList<AnnouncementItem> Applicable()
    {
        var now = DateTimeOffset.UtcNow;
        var version = AppRuntimeInfo.GetApplicationVersion();
        var channel = AppDistributionService.GetCurrentChannel().ToString();
        if (version is null || now - FetchedUtc > TimeSpan.FromDays(30)) return [];
        return Document.Items.Where(i => i.PublishedAt <= now && i.ExpiresAt > now && i.Channels.Contains(channel)
            && (i.MinVersion is null || version >= Normalize(i.MinVersion))
            && (i.MaxVersionExclusive is null || version < Normalize(i.MaxVersionExclusive)))
            .OrderByDescending(i => i.PublishedAt).ToArray();
    }
    private static Version Normalize(string version)
    { var v = Version.Parse(version); return new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision)); }
    internal static AnnouncementText Text(AnnouncementItem item) => item.Locales[
        I18n.GetCurrentUiLanguage().StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-US"];
}

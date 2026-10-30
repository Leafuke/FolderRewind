using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FolderRewind.Services;

internal sealed class MigrationWorkflowStore(string configDirectory)
{
    private readonly string _root = Path.Combine(Path.GetFullPath(configDirectory), "migration", "workflows");
    internal string DirectoryKey => _root;
    internal MigrationWorkflowDocument? Load(string id)
    {
        var path = PathFor(id); if (!File.Exists(path)) return null;
        var document = JsonSerializer.Deserialize(File.ReadAllBytes(path), MigrationWorkflowJsonContext.Default.MigrationWorkflowDocument)
            ?? throw new InvalidDataException("Migration workflow is empty.");
        Validate(document); if (document.WorkflowId != id) throw new InvalidDataException("Migration workflow identity changed.");
        return document;
    }
    internal ImmutableArray<MigrationWorkflowDocument> LoadAll() => LoadAll(null);
    internal ImmutableArray<MigrationWorkflowDocument> LoadAll(Action<string, Exception>? onInvalid)
    {
        if (!Directory.Exists(_root)) return [];
        var documents = ImmutableArray.CreateBuilder<MigrationWorkflowDocument>();
        foreach (var path in Directory.EnumerateFiles(_root, "*.json"))
        {
            try { if (Load(Path.GetFileNameWithoutExtension(path)) is { } document) documents.Add(document); }
            catch (Exception error) when (onInvalid is not null && error is IOException or JsonException or UnauthorizedAccessException)
            { onInvalid(path, error); }
        }
        return [.. documents.OrderBy(d => d.CreatedAtUtc).ThenBy(d => d.WorkflowId, StringComparer.Ordinal)];
    }
    internal void Save(MigrationWorkflowDocument document)
    {
        Validate(document);
        AtomicFileService.Write(PathFor(document.WorkflowId), stream => JsonSerializer.Serialize(stream,
            document with { UpdatedAtUtc = DateTimeOffset.UtcNow }, MigrationWorkflowJsonContext.Default.MigrationWorkflowDocument));
    }
    private string PathFor(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Migration workflow identity is invalid.");
        return Path.Combine(_root, id + ".json");
    }
    private static void Validate(MigrationWorkflowDocument document)
    {
        if (document.FormatVersion != 1 || !Guid.TryParseExact(document.WorkflowId, "N", out _) || document.Projects.IsDefaultOrEmpty
            || document.Projects.Any(p => !Guid.TryParseExact(p.ConfigId, "N", out _) || p.ArchiveSelections.IsDefault || p.BranchSelections.IsDefault || p.Diagnostics.IsDefault)
            || document.Projects.Select(p => p.ConfigId).Distinct(StringComparer.Ordinal).Count() != document.Projects.Length
            || document.ImportProjectIds.IsDefault || document.ImportProjectIds.Distinct(StringComparer.Ordinal).Count() != document.ImportProjectIds.Length
            || document.ImportProjectIds.Any(id => document.Projects.All(p => p.ConfigId != id))
            || !Enum.IsDefined(document.Stage) || document.Projects.Any(p => !Enum.IsDefined(p.Phase)))
            throw new InvalidDataException("Migration workflow is incompatible.");
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(MigrationWorkflowDocument))]
internal partial class MigrationWorkflowJsonContext : JsonSerializerContext;

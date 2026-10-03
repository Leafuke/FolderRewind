using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace FolderRewind.Services.Discovery;

public sealed partial class LudusaviManifestCompiler
{
    /// <summary>Only the current YAML entry is materialized; overlays and anchors are resolved from disk.</summary>
    internal void CompileToIndex(string primaryPath, string? secondaryPath, string? overridePath,
        string destination, string sourceHash, string stagingDirectory, CancellationToken token)
    {
        using var store = new EntryStore(Path.Combine(stagingDirectory, "entries.spool"));
        var entries = new Dictionary<string, EntryHeader>(StringComparer.Ordinal);
        ReadEntries(primaryPath, false, entries, store, token);
        if (secondaryPath is not null) ReadEntries(secondaryPath, true, entries, store, token);
        var diagnostics = new List<LudusaviCompilerDiagnostic>();
        var aliases = ResolveDiskAliases(entries, diagnostics, token);
        var overrides = new FolderRewindGameOverrideDocument();
        if (overridePath is not null)
        {
            using var file = File.OpenRead(overridePath);
            overrides = JsonSerializer.Deserialize<FolderRewindGameOverrideDocument>(file,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true,
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })
                ?? throw new InvalidDataException("The override document is empty.");
        }
        if (overrides.Magic != FolderRewindGameOverrideDocument.CurrentMagic
            || overrides.SchemaVersion != FolderRewindGameOverrideDocument.CurrentSchemaVersion)
            throw new InvalidDataException("Unsupported FolderRewind game override format.");

        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var gzip = new GZipStream(output, CompressionLevel.Optimal);
        using var writer = new Utf8JsonWriter(gzip);
        var options = new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        writer.WriteStartObject();
        writer.WriteNumber("SchemaVersion", LudusaviCompiledIndex.CurrentSchemaVersion);
        writer.WriteString("SourceSha256", sourceHash);
        writer.WriteString("CompiledAtUtc", DateTime.UtcNow);
        writer.WriteStartArray("Games");
        foreach (var pair in entries.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (!pair.Value.IsMapping || pair.Value.IsAlias) continue;
            var node = (YamlMappingNode)store.Load(pair.Value.Offset);
            aliases.TryGetValue(pair.Key, out var names);
            var game = CompileGame(pair.Key, node, names ?? Array.Empty<string>());
            JsonSerializer.Serialize(writer, ApplyGameOverrides(game, overrides.Entries), options);
            writer.Flush();
        }
        writer.WriteEndArray();
        writer.WritePropertyName("Diagnostics");
        JsonSerializer.Serialize(writer, diagnostics, options);
        writer.WriteEndObject();
    }

    private static void ReadEntries(string path, bool overlay, Dictionary<string, EntryHeader> entries,
        EntryStore store, CancellationToken token)
    {
        using var text = new StreamReader(path, Encoding.UTF8, true, 64 * 1024);
        var parser = new Parser(text);
        parser.Consume<StreamStart>();
        parser.Consume<DocumentStart>();
        parser.Consume<MappingStart>();
        var anchors = new Dictionary<string, long>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (!parser.Accept<MappingEnd>(out _))
        {
            token.ThrowIfCancellationRequested();
            var key = ReadEntryNode(parser, store, anchors, token, 0);
            var node = ReadEntryNode(parser, store, anchors, token, 0);
            if (key is not YamlScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value)) continue;
            var name = scalar.Value.Trim();
            if (!seen.Add(name)) throw new InvalidDataException($"Duplicate game entry '{name}'.");
            if (overlay && entries.TryGetValue(name, out var previous) && previous.IsMapping
                && node is YamlMappingNode additions)
            {
                var original = (YamlMappingNode)store.Load(previous.Offset);
                MergeMappings(original, additions);
                node = original;
            }
            var alias = node is YamlMappingNode mapping ? GetNode(mapping, "alias") : null;
            entries[name] = new EntryHeader(store.Save(node), node is YamlMappingNode,
                alias is not null, alias is null ? string.Empty : ScalarValue(alias).Trim());
        }
        parser.Consume<MappingEnd>();
        parser.Consume<DocumentEnd>();
        parser.Consume<StreamEnd>();
    }

    private static YamlNode ReadEntryNode(IParser parser, EntryStore store, Dictionary<string, long> anchors,
        CancellationToken token, int depth)
    {
        token.ThrowIfCancellationRequested();
        if (depth > 128) throw new InvalidDataException("YAML entry nesting is too deep.");
        YamlNode node;
        AnchorName anchor;
        if (parser.TryConsume<Scalar>(out var scalar))
        { node = new YamlScalarNode(scalar.Value); anchor = scalar.Anchor; }
        else if (parser.TryConsume<SequenceStart>(out var sequence))
        {
            anchor = sequence.Anchor;
            var result = new YamlSequenceNode();
            while (!parser.Accept<SequenceEnd>(out _)) result.Add(ReadEntryNode(parser, store, anchors, token, depth + 1));
            parser.Consume<SequenceEnd>(); node = result;
        }
        else if (parser.TryConsume<MappingStart>(out var mapping))
        {
            anchor = mapping.Anchor;
            var result = new YamlMappingNode();
            while (!parser.Accept<MappingEnd>(out _))
                result.Add(ReadEntryNode(parser, store, anchors, token, depth + 1), ReadEntryNode(parser, store, anchors, token, depth + 1));
            parser.Consume<MappingEnd>(); node = result;
        }
        else if (parser.TryConsume<AnchorAlias>(out var alias))
        {
            if (!anchors.TryGetValue(alias.Value.Value, out var offset))
                throw new InvalidDataException($"Undefined or recursive YAML anchor '{alias.Value}'.");
            return store.Load(offset);
        }
        else throw new InvalidDataException("Unexpected YAML event in a game entry.");
        if (!anchor.IsEmpty) anchors[anchor.Value] = store.Save(node);
        return node;
    }

    private static Dictionary<string, IReadOnlyList<string>> ResolveDiskAliases(Dictionary<string, EntryHeader> entries,
        List<LudusaviCompilerDiagnostic> diagnostics, CancellationToken token)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var pair in entries.Where(pair => pair.Value.IsAlias))
        {
            token.ThrowIfCancellationRequested();
            var target = pair.Value.AliasTarget;
            var visited = new HashSet<string>(StringComparer.Ordinal) { pair.Key };
            while (true)
            {
                if (target.Length == 0)
                { AddAliasDiagnostic(diagnostics, "alias-empty-target", pair.Key, "Alias target is empty."); break; }
                if (!entries.TryGetValue(target, out var header) || !header.IsMapping)
                { AddAliasDiagnostic(diagnostics, "alias-missing-target", pair.Key, $"Alias target '{target}' does not exist."); break; }
                if (!visited.Add(target))
                { AddAliasDiagnostic(diagnostics, "alias-cycle", pair.Key, $"Alias chain contains a cycle at '{target}'."); break; }
                if (!header.IsAlias)
                {
                    if (!result.TryGetValue(target, out var list)) result[target] = list = new();
                    list.Add(pair.Key); break;
                }
                target = header.AliasTarget;
            }
        }
        return result.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.OrderBy(value => value, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
    }

    private sealed record EntryHeader(long Offset, bool IsMapping, bool IsAlias, string AliasTarget);

    private sealed class EntryStore : IDisposable
    {
        private readonly FileStream _file;
        public EntryStore(string path) => _file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 64 * 1024, FileOptions.DeleteOnClose);
        public long Save(YamlNode node)
        {
            using var memory = new MemoryStream();
            using (var writer = new Utf8JsonWriter(memory)) WriteNode(writer, node);
            _file.Position = _file.Length;
            var offset = _file.Position;
            using var header = new BinaryWriter(_file, Encoding.UTF8, true);
            header.Write(checked((int)memory.Length));
            memory.Position = 0; memory.CopyTo(_file);
            return offset;
        }
        public YamlNode Load(long offset)
        {
            _file.Position = offset;
            using var header = new BinaryReader(_file, Encoding.UTF8, true);
            var bytes = header.ReadBytes(header.ReadInt32());
            using var document = JsonDocument.Parse(bytes);
            return ReadNode(document.RootElement);
        }
        private static void WriteNode(Utf8JsonWriter writer, YamlNode node)
        {
            writer.WriteStartArray();
            if (node is YamlScalarNode scalar) { writer.WriteNumberValue(0); writer.WriteStringValue(scalar.Value); }
            else if (node is YamlSequenceNode sequence)
            { writer.WriteNumberValue(1); foreach (var child in sequence.Children) WriteNode(writer, child); }
            else if (node is YamlMappingNode mapping)
            { writer.WriteNumberValue(2); foreach (var pair in mapping.Children) { WriteNode(writer, pair.Key); WriteNode(writer, pair.Value); } }
            writer.WriteEndArray();
        }
        private static YamlNode ReadNode(JsonElement value)
        {
            var values = value.EnumerateArray().ToArray();
            if (values[0].GetInt32() == 0) return new YamlScalarNode(values[1].GetString());
            if (values[0].GetInt32() == 1) return new YamlSequenceNode(values.Skip(1).Select(ReadNode));
            var node = new YamlMappingNode();
            for (var i = 1; i < values.Length; i += 2) node.Add(ReadNode(values[i]), ReadNode(values[i + 1]));
            return node;
        }
        public void Dispose() => _file.Dispose();
    }
}

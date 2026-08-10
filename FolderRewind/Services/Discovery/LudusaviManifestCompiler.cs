using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using YamlDotNet.RepresentationModel;

namespace FolderRewind.Services.Discovery;

public sealed class LudusaviManifestCompiler
{
    public LudusaviCompiledIndex Compile(
        Stream primaryManifest,
        Stream? secondaryManifest,
        FolderRewindGameOverrideDocument? overrides,
        string sourceSha256,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(primaryManifest);

        var root = LoadRoot(primaryManifest);
        if (secondaryManifest != null)
        {
            MergeMappings(root, LoadRoot(secondaryManifest));
        }

        var entries = root.Children
            .Where(pair => pair.Key is YamlScalarNode { Value: not null } && pair.Value is YamlMappingNode)
            .ToDictionary(
                pair => ((YamlScalarNode)pair.Key).Value!.Trim(),
                pair => (YamlMappingNode)pair.Value,
                StringComparer.Ordinal);
        var diagnostics = new List<LudusaviCompilerDiagnostic>();
        var aliasesByCanonical = ResolveAliases(entries, diagnostics, cancellationToken);
        var games = new List<LudusaviCompiledGame>();
        foreach (var pair in root.Children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pair.Key is not YamlScalarNode gameNameNode
                || string.IsNullOrWhiteSpace(gameNameNode.Value)
                || pair.Value is not YamlMappingNode gameNode)
            {
                continue;
            }

            var gameName = gameNameNode.Value.Trim();
            if (GetNode(gameNode, "alias") != null)
            {
                continue;
            }

            aliasesByCanonical.TryGetValue(gameName, out var aliases);
            games.Add(CompileGame(gameName, gameNode, aliases ?? Array.Empty<string>()));
        }

        var compiled = new LudusaviCompiledIndex
        {
            SourceSha256 = sourceSha256,
            CompiledAtUtc = DateTime.UtcNow,
            Diagnostics = diagnostics,
            Games = games
                .OrderBy(game => game.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
        return ApplyOverrides(compiled, overrides);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ResolveAliases(
        IReadOnlyDictionary<string, YamlMappingNode> entries,
        ICollection<LudusaviCompilerDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var output = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (aliasName, aliasEntry) in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var aliasNode = GetNode(aliasEntry, "alias");
            if (aliasNode == null)
            {
                continue;
            }

            var targetName = ScalarValue(aliasNode).Trim();
            if (string.IsNullOrWhiteSpace(targetName))
            {
                AddAliasDiagnostic(diagnostics, "alias-empty-target", aliasName, "Alias target is empty.");
                continue;
            }

            var visited = new HashSet<string>(StringComparer.Ordinal) { aliasName };
            while (true)
            {
                if (!entries.TryGetValue(targetName, out var targetEntry))
                {
                    AddAliasDiagnostic(
                        diagnostics,
                        "alias-missing-target",
                        aliasName,
                        $"Alias target '{targetName}' does not exist.");
                    break;
                }
                if (!visited.Add(targetName))
                {
                    AddAliasDiagnostic(
                        diagnostics,
                        "alias-cycle",
                        aliasName,
                        $"Alias chain contains a cycle at '{targetName}'.");
                    break;
                }

                var nextAliasNode = GetNode(targetEntry, "alias");
                if (nextAliasNode == null)
                {
                    if (!output.TryGetValue(targetName, out var aliases))
                    {
                        aliases = new List<string>();
                        output[targetName] = aliases;
                    }
                    if (!aliases.Contains(aliasName, StringComparer.Ordinal))
                    {
                        aliases.Add(aliasName);
                    }
                    break;
                }

                targetName = ScalarValue(nextAliasNode).Trim();
                if (string.IsNullOrWhiteSpace(targetName))
                {
                    AddAliasDiagnostic(diagnostics, "alias-empty-target", aliasName, "Alias chain contains an empty target.");
                    break;
                }
            }
        }

        return output.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList(),
            StringComparer.Ordinal);
    }

    private static void AddAliasDiagnostic(
        ICollection<LudusaviCompilerDiagnostic> diagnostics,
        string code,
        string entryName,
        string message) => diagnostics.Add(new LudusaviCompilerDiagnostic
        {
            Code = code,
            EntryName = entryName,
            Message = message
        });

    private static YamlMappingNode LoadRoot(Stream stream)
    {
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 64 * 1024,
            leaveOpen: true);
        var yaml = new YamlStream();
        yaml.Load(reader);
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidDataException("The Ludusavi manifest must contain one mapping document.");
        }

        return root;
    }

    private static void MergeMappings(YamlMappingNode target, YamlMappingNode overlay)
    {
        foreach (var overlayPair in overlay.Children)
        {
            var key = ScalarValue(overlayPair.Key);
            var targetPair = FindExactPair(target, key);
            if (targetPair.Key != null
                && targetPair.Value is YamlMappingNode targetMapping
                && overlayPair.Value is YamlMappingNode overlayMapping)
            {
                MergeMappings(targetMapping, overlayMapping);
            }
            else
            {
                if (targetPair.Key != null)
                {
                    target.Children.Remove(targetPair.Key);
                }

                target.Children[overlayPair.Key] = overlayPair.Value;
            }
        }
    }

    private static LudusaviCompiledGame CompileGame(
        string gameName,
        YamlMappingNode gameNode,
        IReadOnlyList<string> aliases)
    {
        var externalIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddStoreId(gameNode, "steam", externalIds);
        AddStoreId(gameNode, "gog", externalIds);
        if (GetMapping(gameNode, "id") is { } idMapping)
        {
            foreach (var pair in idMapping.Children)
            {
                var key = ScalarValue(pair.Key);
                var value = string.Join(",", ScalarValues(pair.Value));
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                {
                    externalIds[key] = value;
                }
            }
        }

        var files = CompileResources(GetMapping(gameNode, "files"), BackupResourceKind.FileSet);
        var registry = CompileResources(GetMapping(gameNode, "registry"), BackupResourceKind.Registry);

        return new LudusaviCompiledGame
        {
            DefinitionId = gameName,
            DisplayName = gameName,
            Aliases = aliases,
            ExternalIds = externalIds,
            InstallDirectoryHints = InstallDirectoryValues(GetNode(gameNode, "installDir"))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Files = files,
            Registry = registry,
            Notes = ScalarValues(GetNode(gameNode, "notes"))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            NativeCloud = FlattenMapping(GetMapping(gameNode, "cloud"))
        };
    }

    private static IReadOnlyList<LudusaviCompiledResource> CompileResources(
        YamlMappingNode? resources,
        BackupResourceKind kind)
    {
        if (resources == null)
        {
            return Array.Empty<LudusaviCompiledResource>();
        }

        var result = new List<LudusaviCompiledResource>();
        foreach (var pair in resources.Children)
        {
            var expression = ScalarValue(pair.Key).Trim();
            if (string.IsNullOrWhiteSpace(expression))
            {
                continue;
            }

            var details = pair.Value as YamlMappingNode;
            var constraints = CompileConstraints(details == null ? null : GetNode(details, "when"));
            if (constraints.Count > 0
                && !constraints.Any(constraint =>
                    constraint.OperatingSystems.Count == 0
                    || constraint.OperatingSystems.Any(os =>
                        string.Equals(os, "windows", StringComparison.OrdinalIgnoreCase))))
            {
                continue;
            }

            result.Add(new LudusaviCompiledResource
            {
                ResourceId = CreateResourceId(kind, expression),
                Kind = kind,
                Expression = expression,
                Tags = ScalarValues(details == null ? null : GetNode(details, "tags"))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Constraints = constraints
            });
        }

        return result;
    }

    private static IReadOnlyList<LudusaviCompiledConstraint> CompileConstraints(YamlNode? node)
    {
        if (node == null)
        {
            return Array.Empty<LudusaviCompiledConstraint>();
        }

        var nodes = node is YamlSequenceNode sequence ? sequence.Children : new[] { node };
        var result = new List<LudusaviCompiledConstraint>();
        foreach (var item in nodes)
        {
            if (item is not YamlMappingNode mapping)
            {
                continue;
            }

            result.Add(new LudusaviCompiledConstraint
            {
                OperatingSystems = ScalarValues(GetNode(mapping, "os"))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Stores = ScalarValues(GetNode(mapping, "store"))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            });
        }

        return result;
    }

    private static void AddStoreId(
        YamlMappingNode gameNode,
        string store,
        IDictionary<string, string> externalIds)
    {
        if (GetMapping(gameNode, store) is not { } storeNode)
        {
            return;
        }

        var ids = ScalarValues(GetNode(storeNode, "id"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        if (ids.Count > 0)
        {
            externalIds[store] = string.Join(",", ids);
        }
    }

    private static IReadOnlyDictionary<string, string> FlattenMapping(YamlMappingNode? mapping)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (mapping == null)
        {
            return result;
        }

        FlattenMapping(mapping, string.Empty, result);
        return result;
    }

    private static void FlattenMapping(
        YamlMappingNode mapping,
        string prefix,
        IDictionary<string, string> output)
    {
        foreach (var pair in mapping.Children)
        {
            var key = ScalarValue(pair.Key);
            var fullKey = string.IsNullOrWhiteSpace(prefix) ? key : $"{prefix}.{key}";
            if (pair.Value is YamlMappingNode nested)
            {
                FlattenMapping(nested, fullKey, output);
                continue;
            }

            var value = string.Join(",", ScalarValues(pair.Value));
            if (!string.IsNullOrWhiteSpace(fullKey) && !string.IsNullOrWhiteSpace(value))
            {
                output[fullKey] = value;
            }
        }
    }

    private static IEnumerable<string> ScalarValues(YamlNode? node)
    {
        switch (node)
        {
            case null:
                yield break;
            case YamlScalarNode scalar when !string.IsNullOrWhiteSpace(scalar.Value):
                yield return scalar.Value.Trim();
                yield break;
            case YamlSequenceNode sequence:
                foreach (var value in sequence.Children.SelectMany(ScalarValues))
                {
                    yield return value;
                }
                yield break;
            case YamlMappingNode mapping:
                foreach (var pair in mapping.Children)
                {
                    foreach (var value in ScalarValues(pair.Value))
                    {
                        yield return value;
                    }
                }
                yield break;
        }
    }

    private static IEnumerable<string> InstallDirectoryValues(YamlNode? node)
    {
        if (node is YamlMappingNode mapping)
        {
            foreach (var pair in mapping.Children)
            {
                var value = ScalarValue(pair.Key).Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    yield return value;
                }
            }
            yield break;
        }

        foreach (var value in ScalarValues(node))
        {
            yield return value;
        }
    }

    private static LudusaviCompiledIndex ApplyOverrides(
        LudusaviCompiledIndex index,
        FolderRewindGameOverrideDocument? overrides)
    {
        if (overrides == null)
        {
            return index;
        }

        if (!string.Equals(overrides.Magic, FolderRewindGameOverrideDocument.CurrentMagic, StringComparison.Ordinal)
            || !string.Equals(overrides.SchemaVersion, FolderRewindGameOverrideDocument.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Unsupported FolderRewind game override format.");
        }

        if (overrides.Entries.Count == 0)
        {
            return index;
        }

        var games = index.Games.Select(game => ApplyGameOverrides(game, overrides.Entries)).ToList();
        return new LudusaviCompiledIndex
        {
            SchemaVersion = index.SchemaVersion,
            SourceSha256 = index.SourceSha256,
            CompiledAtUtc = index.CompiledAtUtc,
            Diagnostics = index.Diagnostics,
            Games = games
        };
    }

    private static LudusaviCompiledGame ApplyGameOverrides(
        LudusaviCompiledGame game,
        IReadOnlyList<FolderRewindGameOverrideEntry> entries)
    {
        var matching = entries.Where(entry => Matches(entry, game)).ToList();
        if (matching.Count == 0)
        {
            return game;
        }

        var files = game.Files.ToList();
        var registry = game.Registry.ToList();
        foreach (var operation in matching.SelectMany(entry => entry.Operations))
        {
            var resources = operation.Kind == BackupResourceKind.Registry ? registry : files;
            ApplyOperation(resources, operation);
        }

        return new LudusaviCompiledGame
        {
            DefinitionId = game.DefinitionId,
            DisplayName = game.DisplayName,
            Aliases = game.Aliases,
            ExternalIds = game.ExternalIds,
            InstallDirectoryHints = game.InstallDirectoryHints,
            Files = files,
            Registry = registry,
            Notes = game.Notes,
            NativeCloud = game.NativeCloud
        };
    }

    private static bool Matches(FolderRewindGameOverrideEntry entry, LudusaviCompiledGame game)
    {
        if (!string.IsNullOrWhiteSpace(entry.DefinitionId)
            && string.Equals(entry.DefinitionId, game.DefinitionId, StringComparison.Ordinal))
        {
            return true;
        }

        return entry.ExternalIds.Any(pair =>
            game.ExternalIds.TryGetValue(pair.Key, out var value)
            && string.Equals(value, pair.Value, StringComparison.OrdinalIgnoreCase));
    }

    private static void ApplyOperation(
        IList<LudusaviCompiledResource> resources,
        FolderRewindGameOverrideOperation operation)
    {
        var existing = resources.FirstOrDefault(resource =>
            (!string.IsNullOrWhiteSpace(operation.ResourceId)
             && string.Equals(resource.ResourceId, operation.ResourceId, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(operation.Expression)
                && string.Equals(resource.Expression, operation.Expression, StringComparison.OrdinalIgnoreCase)));

        switch (operation.Action)
        {
            case FolderRewindGameOverrideAction.Add:
                if (!string.IsNullOrWhiteSpace(operation.Expression) && existing == null)
                {
                    resources.Add(new LudusaviCompiledResource
                    {
                        ResourceId = CreateResourceId(operation.Kind, operation.Expression),
                        Kind = operation.Kind,
                        Expression = operation.Expression,
                        Tags = operation.Tags
                    });
                }
                break;
            case FolderRewindGameOverrideAction.Remove:
                if (existing != null)
                {
                    resources.Remove(existing);
                }
                break;
            case FolderRewindGameOverrideAction.Disable:
                if (existing != null)
                {
                    var index = resources.IndexOf(existing);
                    resources[index] = CopyResource(existing, existing.Expression, isDisabled: true, operation.Tags);
                }
                break;
            case FolderRewindGameOverrideAction.Replace:
                if (existing != null && !string.IsNullOrWhiteSpace(operation.ReplacementExpression))
                {
                    var index = resources.IndexOf(existing);
                    resources[index] = CopyResource(existing, operation.ReplacementExpression, existing.IsDisabled, operation.Tags);
                }
                break;
        }
    }

    private static LudusaviCompiledResource CopyResource(
        LudusaviCompiledResource source,
        string expression,
        bool isDisabled,
        IReadOnlyList<string> tags)
    {
        return new LudusaviCompiledResource
        {
            ResourceId = CreateResourceId(source.Kind, expression),
            Kind = source.Kind,
            Expression = expression,
            Tags = tags.Count == 0 ? source.Tags : tags,
            Constraints = source.Constraints,
            IsDisabled = isDisabled
        };
    }

    private static string CreateResourceId(BackupResourceKind kind, string expression)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}|{expression.Trim()}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static YamlNode? GetNode(YamlMappingNode mapping, string key)
    {
        var pair = FindPair(mapping, key);
        return pair.Key == null ? null : pair.Value;
    }

    private static YamlMappingNode? GetMapping(YamlMappingNode mapping, string key) =>
        GetNode(mapping, key) as YamlMappingNode;

    private static KeyValuePair<YamlNode, YamlNode> FindPair(YamlMappingNode mapping, string key)
    {
        foreach (var pair in mapping.Children)
        {
            if (string.Equals(ScalarValue(pair.Key), key, StringComparison.OrdinalIgnoreCase))
            {
                return pair;
            }
        }

        return default;
    }

    private static KeyValuePair<YamlNode, YamlNode> FindExactPair(YamlMappingNode mapping, string key)
    {
        foreach (var pair in mapping.Children)
        {
            if (string.Equals(ScalarValue(pair.Key), key, StringComparison.Ordinal))
            {
                return pair;
            }
        }

        return default;
    }

    private static string ScalarValue(YamlNode node) =>
        (node as YamlScalarNode)?.Value ?? string.Empty;
}

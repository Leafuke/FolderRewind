using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services.Discovery;

namespace FolderRewind.Tests;

[TestClass]
public sealed class PluginDiscoveryDefinitionPolicyTests
{
    private static readonly DiscoveryProviderId ProviderId = new("com.example.provider");
    private static readonly FolderRewind.Plugin.Abstractions.DiscoveryCandidate Candidate = new(
        "candidate",
        "Candidate",
        Array.Empty<ConfigDraft>());
    private static readonly DiscoveryDefinitionDescriptor Definition = new(
        "game",
        "Game",
        Array.Empty<string>(),
        new Dictionary<string, string>());

    [TestMethod]
    public void UnrelatedTargetSkipsScanAndUnknownDefinitionFailsBeforeScan()
    {
        var definitions = new Dictionary<string, DiscoveryDefinitionDescriptor>(StringComparer.Ordinal)
        {
            [Definition.DefinitionId] = Definition
        };
        var diagnostics = new List<DiscoveryDiagnostic>();
        var unrelated = PluginDiscoveryDefinitionPolicy.SelectTargets(
            ProviderId,
            Targeted("other.provider", "game"),
            definitions,
            diagnostics);
        Assert.IsFalse(unrelated.ShouldScan);
        Assert.IsEmpty(diagnostics);

        var unknown = PluginDiscoveryDefinitionPolicy.SelectTargets(
            ProviderId,
            Targeted(ProviderId.Value, "unknown"),
            definitions,
            diagnostics);
        Assert.IsFalse(unknown.ShouldScan);
        Assert.AreEqual("definition-unavailable", diagnostics.Single().Code);
    }

    [TestMethod]
    [DataRow(null, "definition-unresolved")]
    [DataRow("unknown", "definition-resolution-unknown")]
    [DataRow("throw", "definition-resolution-failed")]
    public void InvalidResolverResultsProduceStableDiagnostics(string? resolution, string expectedCode)
    {
        var catalog = new FakeCatalog(resolution);
        var definitions = new Dictionary<string, DiscoveryDefinitionDescriptor>(StringComparer.Ordinal)
        {
            [Definition.DefinitionId] = Definition
        };
        var diagnostics = new List<DiscoveryDiagnostic>();

        var resolved = PluginDiscoveryDefinitionPolicy.TryResolve(
            ProviderId,
            catalog,
            Candidate,
            definitions,
            requestedDefinitions: null,
            diagnostics,
            out _);

        Assert.IsFalse(resolved);
        Assert.AreEqual(expectedCode, diagnostics.Single().Code);
    }

    [TestMethod]
    public void DefinitionCatalogUsesOrdinalUniqueness()
    {
        var diagnostics = new List<DiscoveryDiagnostic>();
        var definitions = PluginDiscoveryDefinitionPolicy.ValidateDefinitions(
            ProviderId,
            [Definition, Definition with { DefinitionId = "GAME" }],
            diagnostics);

        Assert.HasCount(2, definitions);
        Assert.IsEmpty(diagnostics);
    }

    private static FolderRewind.Models.DiscoveryRequest Targeted(string providerId, string definitionId) => new()
    {
        Mode = DiscoveryRequestMode.PresetTargeted,
        Definitions =
        [
            new DiscoveryDefinitionReference
            {
                ProviderId = providerId,
                DefinitionId = definitionId
            }
        ]
    };

    private sealed class FakeCatalog(string? resolution) : IDiscoveryDefinitionCatalog
    {
        public IReadOnlyList<DiscoveryDefinitionDescriptor> Definitions => [Definition];

        public string? ResolveDefinitionId(FolderRewind.Plugin.Abstractions.DiscoveryCandidate candidate)
        {
            if (string.Equals(resolution, "throw", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("resolver failure");
            }
            return resolution;
        }
    }
}

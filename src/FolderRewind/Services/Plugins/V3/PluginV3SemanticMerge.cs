using FolderRewind.History.Merge;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Plugins.V3;

internal sealed record SemanticMergeOption(SemanticMergeBinding Binding, string Name, string Description);

internal sealed class PluginV3SemanticMerge : ISemanticMergeResolver
{
    internal static IReadOnlyList<SemanticMergeOption> Options(BackupConfig config)
    {
        var kind = PluginV3ModelMapper.ToKind(config);
        var options = new List<SemanticMergeOption>();
        foreach (var id in PluginV3RuntimeService.GetActivePlugins())
        {
            var manifest = PluginV3RuntimeService.FindManifest(id);
            if (manifest is null || PluginV3PackageService.GetActivePackageSha256(id, manifest.Version) is not { } sha) continue;
            foreach (var provider in manifest.MergeProviders.Where(p => p.CompatibleConfigKinds.Contains(kind)))
            foreach (var policy in provider.Policies)
                options.Add(new(new(provider.ProviderId, provider.ContractVersion, manifest.Version, sha, policy.PolicyId, policy.Version, kind),
                    Localize(provider.DisplayName) + " · " + Localize(policy.DisplayName), Localize(policy.Description)));
        }
        return options;
    }
    private static string Localize(LocalizedText text) => I18n.PickBest(text.Translations, text.Default) ?? text.Default;
    internal static bool Matches(SemanticMergeBinding binding)
    {
        var manifest = PluginV3RuntimeService.FindManifest(binding.ProviderId.PluginId);
        return manifest?.Version == binding.PluginVersion
            && PluginV3PackageService.GetActivePackageSha256(binding.ProviderId.PluginId, binding.PluginVersion) == binding.PackageSha256
            && manifest.MergeProviders.Any(p => p.ProviderId == binding.ProviderId && p.ContractVersion == binding.ContractVersion
                && p.CompatibleConfigKinds.Contains(binding.Kind) && p.Policies.Any(policy => policy.PolicyId == binding.PolicyId && policy.Version == binding.PolicyVersion));
    }
    public ValueTask<SemanticMergeInvocation> AcquireAsync(SemanticMergeBinding binding, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var lease = PluginV3RuntimeService.Runtime.TryAcquire<IMergeProviderCapability>(binding.ProviderId.PluginId,
            p => p.ProviderId == binding.ProviderId && p.ContractVersion == binding.ContractVersion, token)
            ?? throw new InvalidOperationException(I18n.GetString("Merge_SemanticUnavailable"));
        try
        {
            if (!Matches(binding)) throw new InvalidOperationException(I18n.GetString("Merge_SemanticUnavailable"));
            return ValueTask.FromResult(new SemanticMergeInvocation(lease.Capability, lease.Context, lease));
        }
        catch { lease.Dispose(); throw; }
    }
}

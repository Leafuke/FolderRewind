using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Merge;

public sealed record SemanticMergeBinding(MergeProviderId ProviderId, int ContractVersion, string PluginVersion,
    string PackageSha256, string PolicyId, string PolicyVersion, ConfigKindRef Kind)
{
    public MergeProviderDescriptor Descriptor => new(ProviderId.ToString(), PluginVersion + "/" + PackageSha256,
        ContractVersion, PolicyId, PolicyVersion);
}

/// <summary>The resolver must compare the complete persisted binding before acquiring the operation.</summary>
public interface ISemanticMergeResolver
{
    ValueTask<SemanticMergeInvocation> AcquireAsync(SemanticMergeBinding binding, CancellationToken token);
}

public sealed class SemanticMergeInvocation(IMergeProviderCapability provider, PluginInvocationContext context, IDisposable lifetime) : IDisposable
{
    public IMergeProviderCapability Provider { get; } = provider;
    public PluginInvocationContext Context { get; } = context;
    public void Dispose() => lifetime.Dispose();
}

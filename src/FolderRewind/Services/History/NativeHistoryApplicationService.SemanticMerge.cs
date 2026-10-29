using FolderRewind.History.Application;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.Models;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Linq;

namespace FolderRewind.Services;

internal static partial class NativeHistoryApplicationService
{
    internal static SemanticMergeEngine? MergeEngine(HistoryRuntime runtime, SemanticMergeBinding? binding)
        => binding is null ? null : new(runtime.MergeSessions, binding, new PluginV3SemanticMerge());
    internal static HistoryMergeService MergeCore(HistoryRuntime runtime, HistoryRestoreService restore, MergeSession? session)
        => new(runtime, restore, semantic: MergeEngine(runtime, session?.Plan.SemanticProvider));
    internal static bool MergeBindingCurrent(MergeSession session)
        => session.Plan.SemanticProvider is { } binding ? PluginV3SemanticMerge.Matches(binding)
            : session.Plan.ProviderVersion == MergeProviderDescriptor.GenericIdentity && session.Plan.PolicyVersion == MergeProviderDescriptor.GenericPolicyIdentity;
    private static SemanticMergeBinding? RefreshMergeBinding(BackupConfig config, SemanticMergeBinding? binding)
        => binding is null ? null : PluginV3SemanticMerge.Options(config).SingleOrDefault(o => o.Binding.ProviderId == binding.ProviderId
            && o.Binding.PolicyId == binding.PolicyId)?.Binding ?? throw new InvalidOperationException(I18n.GetString("Merge_SemanticUnavailable"));
}

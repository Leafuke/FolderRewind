using System;
using System.Collections.Generic;
using System.Linq;
using FolderRewind.Models;

namespace FolderRewind.Services;

public enum OnboardingRepairTarget { None, Plugins, KnotLink, ProjectContent, ProjectResources, CloudConnection, OpenListEnvironment, MinecraftManualCheck }
public sealed record OnboardingRepairContext(string ConfigId, string SourceId, string ConfigSignature, DateTimeOffset CheckedAtUtc);

// Only host-owned diagnostic codes select navigation. Server text and plugin messages never supply an executable action.
public static class OnboardingRepairPolicy
{
    private static readonly IReadOnlyDictionary<string, OnboardingRepairTarget> Targets = new Dictionary<string, OnboardingRepairTarget>(StringComparer.Ordinal)
    {
        ["minecraft.host-plugin"] = OnboardingRepairTarget.Plugins,
        ["minecraft.host-plugin-runtime"] = OnboardingRepairTarget.Plugins,
        ["minecraft.knotlink-server"] = OnboardingRepairTarget.KnotLink,
        ["minecraft.host-connection"] = OnboardingRepairTarget.KnotLink,
        ["minecraft.source-association"] = OnboardingRepairTarget.ProjectContent,
        ["minecraft.game-response"] = OnboardingRepairTarget.MinecraftManualCheck,
        ["minecraft.hot-restore"] = OnboardingRepairTarget.MinecraftManualCheck,
        ["minecraft.hot-backup"] = OnboardingRepairTarget.MinecraftManualCheck,
        ["minecraft.game-schedule"] = OnboardingRepairTarget.MinecraftManualCheck,
        ["openlist.endpoint-unconfirmed"] = OnboardingRepairTarget.OpenListEnvironment,
        ["openlist.endpoint-observed"] = OnboardingRepairTarget.CloudConnection,
        ["cloud.connection-changed"] = OnboardingRepairTarget.CloudConnection,
        ["cloud.access-unconfirmed"] = OnboardingRepairTarget.CloudConnection,
        ["cloud.access-verified"] = OnboardingRepairTarget.CloudConnection,
        ["cloud.copy-incomplete"] = OnboardingRepairTarget.CloudConnection,
        ["cloud.copy-complete"] = OnboardingRepairTarget.CloudConnection,
        ["backup.resource-settings"] = OnboardingRepairTarget.ProjectResources
    };
    public static OnboardingRepairTarget Resolve(string code) => Targets.TryGetValue(code, out var target) ? target : OnboardingRepairTarget.None;
    public static bool IsCurrent(OnboardingRepairContext context, string configId, string sourceId, string signature)
        => context.ConfigId == configId && context.SourceId == sourceId && context.ConfigSignature == signature;

    public static string ExportSummary(IEnumerable<(string Code, string State, DateTimeOffset CheckedAtUtc)> observations)
        => string.Join(Environment.NewLine, observations.Take(OnboardingOperationBudgets.RecentDiagnostics)
            .Select(item => $"{(Targets.ContainsKey(item.Code) ? item.Code : "unknown")}\t{(Enum.TryParse<OnboardingCheckState>(item.State, out var state) && Enum.IsDefined(state) ? state : OnboardingCheckState.Unknown)}\t{item.CheckedAtUtc:O}\t{Resolve(item.Code)}"));
}

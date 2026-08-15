using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Operations;
using FolderRewind.Services.Hotkeys;

namespace FolderRewind.Services.Plugins.V3;

public static class PluginV3CommandService
{
    public static void RegisterHotkeys(PluginId pluginId, string pluginName)
    {
        using var lease = PluginV3RuntimeService.Runtime.TryAcquire<IPluginCommandCapability>(
            pluginId,
            CancellationToken.None);
        if (lease is null) return;
        var commands = lease.Capability.Commands
            .Where(value => !string.IsNullOrWhiteSpace(value.DefaultHotkey))
            .ToArray();
        foreach (var command in commands)
        {
            HotkeyManager.RegisterHandler(
                HotkeyId(command.Id),
                _ => ExecuteAsync(command.Id, new Dictionary<string, JsonElement>(), CancellationToken.None).AsTask());
        }
        HotkeyManager.RegisterDefinitions(commands.Select(command => new HotkeyDefinition
        {
            Id = HotkeyId(command.Id),
            DisplayName = command.DisplayName,
            Description = command.DisplayName,
            DefaultGesture = command.DefaultHotkey!,
            Scope = command.IsGlobalHotkey ? HotkeyScope.GlobalHotkey : HotkeyScope.Shortcut,
            OwnerPluginId = pluginId.Value,
            OwnerPluginName = pluginName
        }));
    }

    public static void UnregisterHotkeys(PluginId pluginId)
        => HotkeyManager.UnregisterPluginHotkeys(pluginId.Value);

    private static string HotkeyId(PluginCommandId commandId)
        => $"plugin.{commandId.PluginId.Value}.{commandId.CommandId}";

    public static async ValueTask<PluginCommandResult> ExecuteAsync(
        PluginCommandId commandId,
        IReadOnlyDictionary<string, JsonElement> arguments,
        CancellationToken cancellationToken = default)
    {
        using var lease = PluginV3RuntimeService.Runtime.TryAcquire<IPluginCommandCapability>(
            commandId.PluginId,
            cancellationToken)
            ?? throw new InvalidOperationException("The command owner is not active.");
        if (!lease.Capability.Commands.Any(value => value.Id == commandId))
            throw new InvalidOperationException("The active plugin did not declare this command.");
        return await lease.Capability.ExecuteAsync(
            new PluginCommandRequest(commandId, arguments),
            lease.Context).ConfigureAwait(false);
    }

    public static async ValueTask<(bool Handled, string Response)> TryExecuteKnotLinkAsync(
        string command,
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken cancellationToken = default)
    {
        foreach (var pluginId in PluginV3RuntimeService.GetActivePlugins())
        {
            using var lease = PluginV3RuntimeService.Runtime.TryAcquire<IKnotLinkIntegrationCapability>(
                pluginId,
                cancellationToken);
            if (lease is null || !lease.Capability.Commands.Any(value =>
                    KnotLinkCommandMatcher.Matches(value, command, arguments)))
                continue;
            var result = await lease.Capability.ExecuteAsync(command, arguments, lease.Context)
                .ConfigureAwait(false);
            var prefix = result.Outcome is OperationOutcome.Success or OperationOutcome.SuccessWithWarnings
                ? "OK:"
                : "ERROR:";
            if (TryString(result.Values, "data", out var data)) return (true, prefix + data);
            if (TryString(result.Values, "message", out var message)) return (true, prefix + message);
            var diagnostic = result.Diagnostics.FirstOrDefault();
            return (true, prefix + (diagnostic?.Code ?? result.Outcome.ToString()));
        }
        return (false, string.Empty);
    }

    private static bool TryString(
        IReadOnlyDictionary<string, JsonElement> values,
        string key,
        out string value)
    {
        value = string.Empty;
        if (!values.TryGetValue(key, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString() ?? string.Empty;
        return true;
    }
}

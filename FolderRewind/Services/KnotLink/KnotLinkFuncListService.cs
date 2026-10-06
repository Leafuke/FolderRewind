using FolderRewind.Services.Plugins;
using FolderRewind.Services.Plugins.V3;
using FolderRewind.Plugin.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace FolderRewind.Services.KnotLink
{
    public static class KnotLinkFuncListService
    {
        public const string SpecVersion = "1.0";
        public const string ManifestVersion = "3.0.0";
        public const string DefaultAppId = "0x00000020";
        public const string DefaultOpenSocketId = "0x00000010";
        public const string DefaultSignalId = "0x00000020";
        private static readonly IReadOnlySet<string> CoreSignalNames = BuildCore().Signal.Keys.ToHashSet(StringComparer.Ordinal);

        public static KnotLinkFuncList BuildCore(
            string appId = DefaultAppId,
            string openSocketId = DefaultOpenSocketId,
            string signalId = DefaultSignalId)
        {
            var manifest = NewManifest();
            AddCoreOpenSocketFunctions(manifest, appId, openSocketId);
            AddCoreSignals(manifest, appId, signalId);
            return manifest;
        }

        public static KnotLinkFuncList BuildRuntime(
            string appId,
            string openSocketId,
            string signalId)
        {
            var manifest = BuildCore(appId, openSocketId, signalId);
            MergeV3PluginCommands(manifest, appId, openSocketId, signalId);
            return manifest;
        }

        private static void MergeV3PluginCommands(
            KnotLinkFuncList manifest,
            string appId,
            string openSocketId,
            string signalId)
        {
            var contributions = new List<(PluginId PluginId, IReadOnlyList<KnotLinkCommandDescriptor> Commands)>();
            foreach (var pluginId in PluginV3RuntimeService.GetActivePlugins())
            {
                using var lease = PluginV3RuntimeService.Runtime
                    .TryAcquire<IKnotLinkIntegrationCapability>(pluginId);
                if (lease is null) continue;
                contributions.Add((pluginId, lease.Capability.Commands.ToArray()));
                MergePluginSignals(manifest, appId, signalId, pluginId, lease.Capability.Signals);
            }

            MergePluginCommands(manifest, appId, openSocketId, contributions);
        }

        internal static void MergePluginCommands(
            KnotLinkFuncList manifest,
            string appId,
            string openSocketId,
            IEnumerable<(PluginId PluginId, IReadOnlyList<KnotLinkCommandDescriptor> Commands)> contributions)
        {
            foreach (var contribution in contributions.OrderBy(item => item.PluginId.Value, StringComparer.Ordinal))
            {
                foreach (var command in contribution.Commands.OrderBy(item => item.Command, StringComparer.OrdinalIgnoreCase))
                {
                    var name = NormalizeCapabilityName(command.FunctionName ?? command.Command);
                    if (string.IsNullOrEmpty(name) || manifest.OpenSocket.ContainsKey(name))
                    {
                        LogCapabilityCollision(contribution.PluginId.Value, name, "openSocket");
                        continue;
                    }

                    var args = new SortedDictionary<string, KnotLinkFuncArgument>(StringComparer.Ordinal)
                    {
                        ["cmd"] = Static(command.Command, command.Description)
                    };
                    var declared = command.IsTargetSelector
                        ? (KnotLinkCoreCommands.Find(command.Command)?.Arguments ?? Array.Empty<KnotLinkArgumentDescriptor>())
                            .Where(argument => argument.Name is not "config_id" and not "folder")
                            .Concat(command.Arguments)
                        : command.Arguments;
                    foreach (var argument in declared) args[argument.Name] = FromDescriptor(argument);
                    foreach (var argument in command.RequiredArguments)
                    {
                        args[argument.Key] = Static(argument.Value, $"Required value for {argument.Key}.");
                    }
                    manifest.OpenSocket[name] = new KnotLinkOpenSocketFunction
                    {
                        AppId = appId,
                        OpenSocketId = openSocketId,
                        Description = command.Description,
                        Args = args,
                        Returns = StatusReturns(command.Returns.ToArray())
                    };
                }
            }
        }

        public static string Serialize(KnotLinkFuncList manifest, bool indented = false)
        {
            ArgumentNullException.ThrowIfNull(manifest);
            var options = new JsonSerializerOptions
            {
                WriteIndented = indented,
                TypeInfoResolver = KnotLinkFuncListJsonContext.Default
            };
            return JsonSerializer.Serialize(manifest, options);
        }

        private static KnotLinkFuncList NewManifest() => new()
        {
            SpecVersion = SpecVersion,
            ManifestVersion = ManifestVersion,
            AppName = "FolderRewind",
            OpenSocket = new SortedDictionary<string, KnotLinkOpenSocketFunction>(StringComparer.Ordinal),
            Signal = new SortedDictionary<string, KnotLinkSignalFunction>(StringComparer.Ordinal)
        };

        private static void AddCoreOpenSocketFunctions(KnotLinkFuncList manifest, string appId, string socketId)
        {
            foreach (var command in KnotLinkCoreCommands.Commands)
                AddFunction(manifest, appId, socketId, command.Command.ToLowerInvariant(), command.Command,
                    command.Description,
                    new SortedDictionary<string, KnotLinkFuncArgument>(command.Arguments.ToDictionary(
                        argument => argument.Name, FromDescriptor), StringComparer.Ordinal),
                    StatusReturns(command.Returns.ToArray()));
        }

        private static KnotLinkFuncArgument FromDescriptor(KnotLinkArgumentDescriptor argument) => new()
        {
            Type = argument.Type, Description = argument.Description, DefaultValue = argument.DefaultValue,
            Value = argument.Value,
            Options = argument.Options.Count == 0 ? null : argument.Options.Select(option => new[] { option.Description, option.Value }).ToList()
        };

        internal static void MergePluginSignals(KnotLinkFuncList manifest, string appId, string signalId,
            PluginId pluginId, IEnumerable<KnotLinkSignalDescriptor> signals)
        {
            foreach (var signal in signals.OrderBy(signal => signal.Name, StringComparer.Ordinal))
            {
                var name = NormalizeCapabilityName(signal.Name);
                if (string.IsNullOrEmpty(name)) { LogCapabilityCollision(pluginId.Value, name, "signal"); continue; }
                if (manifest.Signal.TryGetValue(name, out var existing))
                {
                    if (!CoreSignalNames.Contains(name))
                    {
                        LogCapabilityCollision(pluginId.Value, name, "signal");
                        continue;
                    }
                    // Plugins may enrich an existing host event (notably restore_finished).
                    foreach (var field in signal.Fields)
                        if (!existing.Returns.ContainsKey(field.Key)) existing.Returns[field.Key] = new() { Description = field.Value };
                    continue;
                }
                AddSignal(manifest, appId, signalId, name, signal.Description,
                    signal.Fields.Select(field => (field.Key, field.Value)).ToArray());
            }
        }

        private static void AddCoreSignals(KnotLinkFuncList manifest, string appId, string signalId)
        {
            AddSignal(manifest, appId, signalId, "app_startup", "FolderRewind KnotLink endpoint started.", ("version", "Application version."));
            AddSignal(manifest, appId, signalId, "list_configs", "Backup configuration list was queried.", ("data", "Encoded result data."));
            AddSignal(manifest, appId, signalId, "list_folders", "Managed folder list was queried.", ("config", "Configuration ID."), ("data", "Encoded result data."));
            AddSignal(manifest, appId, signalId, "list_backups", "Backup archive list was queried.", ("config", "Configuration ID."), ("folder", "Folder name."), ("data", "Encoded result data."));
            AddSignal(manifest, appId, signalId, "get_config", "Backup configuration details were queried.", ("config", "Configuration ID."));
            AddSignal(manifest, appId, signalId, "status", "Runtime status was queried.");

            foreach (var name in new[] { "command_accepted", "command_started", "command_progress", "command_completed", "command_failed", "command_error" })
                AddSignal(manifest, appId, signalId, name, $"Command lifecycle event: {name}.", ("command", "Command name."), ("request_id", "Request correlation ID."), ("progress", "Optional progress percentage."), ("result", "Optional completion result."), ("reason", "Optional failure reason."), ("error", "Optional error detail."));

            manifest.Signal["command_completed"].Returns["file"] = new() { Description = "Optional completed backup filename." };
            manifest.Signal["command_completed"].Returns["important"] = new() { Description = "True only for a confirmed protected backup." };

            foreach (var name in new[] { "backup_started", "backup_warning", "backup_success", "backup_failed" })
                AddSignal(manifest, appId, signalId, name, $"Backup event: {name}.", ("config", "Configuration ID."), ("folder", "Folder name."));

            foreach (var name in new[] { "restore_started", "restore_success", "restore_finished", "restore_failed" })
                AddSignal(manifest, appId, signalId, name, $"Restore event: {name}.", ("config", "Configuration ID."), ("folder", "Folder name."));

            foreach (var name in new[] { "backup_all_started", "backup_all_completed", "backup_all_failed" })
                AddSignal(manifest, appId, signalId, name, $"Configuration backup event: {name}.", ("config", "Configuration ID."));

            foreach (var name in new[] { "auto_backup_started", "auto_backup_executed", "auto_backup_error", "auto_backup_stopped" })
                AddSignal(manifest, appId, signalId, name, $"Periodic backup event: {name}.", ("config", "Configuration ID."), ("folder", "Folder name."));

            AddSignal(manifest, appId, signalId, "mark_important", "A backup importance flag changed.", ("config", "Configuration ID."), ("folder", "Folder name."), ("file", "Backup archive file."), ("important", "New importance value."));
        }

        private static void AddFunction(
            KnotLinkFuncList manifest,
            string appId,
            string socketId,
            string name,
            string command,
            string description,
            SortedDictionary<string, KnotLinkFuncArgument>? args = null,
            List<string[]>? returns = null)
        {
            args ??= new SortedDictionary<string, KnotLinkFuncArgument>(StringComparer.Ordinal);
            args["cmd"] = Static(command, "Operation command.");
            manifest.OpenSocket[name] = new KnotLinkOpenSocketFunction
            {
                AppId = appId,
                OpenSocketId = socketId,
                Description = description,
                Args = args,
                Returns = returns ?? StatusReturns("func_list")
            };
        }

        private static void AddSignal(KnotLinkFuncList manifest, string appId, string signalId, string name, string description, params (string Name, string Description)[] fields)
        {
            var returns = new SortedDictionary<string, KnotLinkSignalField>(StringComparer.Ordinal)
            {
                ["event"] = new() { Description = "Signal event name.", Verification = name },
                ["from"] = new() { Description = "Caller identifier when emitted in a command conversation." },
                ["request_id"] = new() { Description = "Request correlation ID when emitted in a command conversation." }
            };
            foreach (var field in fields)
            {
                if (!returns.ContainsKey(field.Name)) returns[field.Name] = new() { Description = field.Description };
            }
            manifest.Signal[name] = new KnotLinkSignalFunction
            {
                AppId = appId,
                SignalId = signalId,
                Description = description,
                Returns = returns
            };
        }

        public static KnotLinkFuncArgument Static(string value, string description) => new() { Type = "static", Value = value, Description = description };
        public static KnotLinkFuncArgument Input(string description, string defaultValue) => new() { Type = "input", Description = description, DefaultValue = defaultValue };
        public static KnotLinkFuncArgument Optional(string description, params (string Description, string Value)[] options) => new()
        {
            Type = "optional",
            Description = description,
            Options = options.Select(item => new[] { item.Description, item.Value }).ToList()
        };

        public static KnotLinkFuncArgument BooleanOption(string description) => Optional(description, ("True", "true"), ("False", "false"));

        public static SortedDictionary<string, KnotLinkFuncArgument> Args(params (string Name, KnotLinkFuncArgument Argument)[] args) =>
            new(args.ToDictionary(item => item.Name, item => item.Argument, StringComparer.Ordinal), StringComparer.Ordinal);

        public static SortedDictionary<string, KnotLinkFuncArgument> WithConversation(SortedDictionary<string, KnotLinkFuncArgument> args)
        {
            args["from"] = Input("Required caller identifier.", "example.client");
            args["request_id"] = Input("Required unique request correlation ID.", "request-001");
            return args;
        }

        public static List<string[]> StatusReturns(params string[] extraFields)
        {
            var result = new List<string[]> { new[] { "Operation status.", "status" } };
            result.AddRange(extraFields.Select(field => new[] { $"Response {field}.", field }));
            return result;
        }

        private static string NormalizeCapabilityName(string name) =>
            string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim().ToLowerInvariant();

        private static void LogCapabilityCollision(string pluginId, string name, string kind) =>
            LogService.LogWarning($"Skipped KnotLink {kind} capability '{name}' from plugin '{pluginId}' because the name is invalid or already registered.", "KnotLink");
    }
}

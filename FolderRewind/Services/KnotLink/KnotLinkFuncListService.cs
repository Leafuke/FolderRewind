using FolderRewind.Services.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace FolderRewind.Services.KnotLink
{
    public static class KnotLinkFuncListService
    {
        public const string SpecVersion = "1.0";
        public const string ManifestVersion = "1.0.0";
        public const string DefaultAppId = "0x00000020";
        public const string DefaultOpenSocketId = "0x00000010";
        public const string DefaultSignalId = "0x00000020";

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
            MergePluginContributions(
                manifest,
                appId,
                openSocketId,
                signalId,
                PluginService.GetKnotLinkCapabilityContributions());
            return manifest;
        }

        internal static void MergePluginContributions(
            KnotLinkFuncList manifest,
            string appId,
            string openSocketId,
            string signalId,
            IEnumerable<(string PluginId, PluginKnotLinkCapabilityContribution Contribution)> contributions)
        {
            foreach (var (pluginId, contribution) in contributions.OrderBy(item => item.PluginId, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var function in contribution.OpenSocket.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var name = NormalizeCapabilityName(function.Name);
                    if (string.IsNullOrEmpty(name) || manifest.OpenSocket.ContainsKey(name))
                    {
                        LogCapabilityCollision(pluginId, name, "openSocket");
                        continue;
                    }

                    manifest.OpenSocket[name] = new KnotLinkOpenSocketFunction
                    {
                        AppId = appId,
                        OpenSocketId = openSocketId,
                        Description = function.Description,
                        Args = new SortedDictionary<string, KnotLinkFuncArgument>(
                            function.Args.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
                            StringComparer.Ordinal),
                        Returns = function.Returns.Select(item => item.ToArray()).ToList()
                    };
                }

                foreach (var signal in contribution.Signal.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var name = NormalizeCapabilityName(signal.Name);
                    if (string.IsNullOrEmpty(name) || manifest.Signal.ContainsKey(name))
                    {
                        LogCapabilityCollision(pluginId, name, "signal");
                        continue;
                    }

                    manifest.Signal[name] = new KnotLinkSignalFunction
                    {
                        AppId = appId,
                        SignalId = signalId,
                        Description = signal.Description,
                        Returns = new SortedDictionary<string, KnotLinkSignalField>(
                            signal.Returns.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
                            StringComparer.Ordinal)
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
            AddFunction(manifest, appId, socketId, "get_capabilities", "GET_CAPABILITIES", "Get the runtime FolderRewind funcList manifest.",
                returns: StatusReturns("content_type", "encoding", "manifest_version", "func_list"));
            AddFunction(manifest, appId, socketId, "ping", "PING", "Check whether the FolderRewind KnotLink endpoint is available.", returns: StatusReturns("message"));
            AddFunction(manifest, appId, socketId, "list_configs", "LIST_CONFIGS", "List backup configurations.", returns: StatusReturns("data"));
            AddFunction(manifest, appId, socketId, "list_folders", "LIST_FOLDERS", "List managed folders in a backup configuration.",
                Args(("config_id", Input("Backup configuration ID.", "config-id"))), StatusReturns("data"));
            AddFunction(manifest, appId, socketId, "list_backups", "LIST_BACKUPS", "List backup archives for a managed folder.",
                Args(("config_id", Input("Backup configuration ID.", "config-id")), ("folder", Input("Folder name or index.", "0"))), StatusReturns("data"));
            AddFunction(manifest, appId, socketId, "get_config", "GET_CONFIG", "Get public settings for a backup configuration.",
                Args(("config_id", Input("Backup configuration ID.", "config-id"))), StatusReturns("data"));
            AddFunction(manifest, appId, socketId, "get_status", "GET_STATUS", "Get FolderRewind runtime status.", returns: StatusReturns("data"));

            AddFunction(manifest, appId, socketId, "backup", "BACKUP", "Start a backup for one managed folder.",
                WithConversation(Args(
                    ("config_id", Input("Backup configuration ID.", "config-id")),
                    ("folder", Input("Folder name or index.", "0")),
                    ("comment", Input("Optional backup comment.", "")),
                    ("force_full", BooleanOption("Force a full backup.")),
                    ("backup_blacklist", Input("Comma-separated one-shot blacklist rules.", "")),
                    ("backup_whitelist", Input("Comma-separated one-shot whitelist rules.", "")),
                    ("backup_scope", Input("Optional plugin backup scope ID.", "")),
                    ("scope_dimensions", Input("Example plugin scope parameter.", "")),
                    ("scope_areas", Input("Example plugin scope parameter.", "")))), StatusReturns("message"));

            AddFunction(manifest, appId, socketId, "restore", "RESTORE", "Start restoring a managed folder from an archive.",
                WithConversation(Args(
                    ("config_id", Input("Backup configuration ID.", "config-id")),
                    ("folder", Input("Folder name or index.", "0")),
                    ("file", Input("Backup archive file name.", "backup.7z")),
                    ("mode", Optional("Restore mode.", ("Overwrite", "overwrite"), ("Clean", "clean"))),
                    ("confirm_partial_clean", BooleanOption("Confirm clean restore from a partial backup.")),
                    ("restore_whitelist", Input("Comma-separated one-shot restore whitelist rules.", "")))), StatusReturns("message"));

            AddFunction(manifest, appId, socketId, "backup_all", "BACKUP_ALL", "Start backing up every folder in a configuration.",
                WithConversation(Args(
                    ("config_id", Input("Backup configuration ID.", "config-id")),
                    ("comment", Input("Optional backup comment.", "")),
                    ("force_full", BooleanOption("Force full backups.")),
                    ("backup_blacklist", Input("Comma-separated one-shot blacklist rules.", "")),
                    ("backup_whitelist", Input("Comma-separated one-shot whitelist rules.", "")),
                    ("backup_scope", Input("Optional plugin backup scope ID.", "")))), StatusReturns("message"));

            AddFunction(manifest, appId, socketId, "auto_backup", "AUTO_BACKUP", "Start periodic backup for one managed folder.",
                WithConversation(Args(
                    ("config_id", Input("Backup configuration ID.", "config-id")),
                    ("folder", Input("Folder name or index.", "0")),
                    ("interval_minutes", Input("Backup interval in minutes.", "10")))), StatusReturns("message"));
            AddFunction(manifest, appId, socketId, "stop_auto_backup", "STOP_AUTO_BACKUP", "Stop periodic backup for one managed folder.",
                WithConversation(Args(
                    ("config_id", Input("Backup configuration ID.", "config-id")),
                    ("folder", Input("Folder name or index.", "0")))), StatusReturns("message"));
            AddFunction(manifest, appId, socketId, "mark_important", "MARK_IMPORTANT", "Mark or unmark a backup archive as important.",
                WithConversation(Args(
                    ("config_id", Input("Backup configuration ID.", "config-id")),
                    ("folder", Input("Folder name or index.", "0")),
                    ("file", Input("Backup archive file name.", "backup.7z")),
                    ("important", BooleanOption("Whether the archive is important.")))), StatusReturns("message"));
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
                AddSignal(manifest, appId, signalId, name, $"Command lifecycle event: {name}.", ("command", "Command name."), ("request_id", "Request correlation ID."));

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
                ["event"] = new() { Description = "Signal event name.", Verification = name }
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

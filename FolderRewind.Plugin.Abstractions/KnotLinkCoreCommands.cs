namespace FolderRewind.Plugin.Abstractions;

/// <summary>Wire definitions shared by validation, discovery and semantic target selectors.</summary>
public static class KnotLinkCoreCommands
{
    public static IReadOnlySet<string> FolderCommands { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "BACKUP", "LIST_BACKUPS", "RESTORE", "AUTO_BACKUP", "STOP_AUTO_BACKUP", "MARK_IMPORTANT" };
    public static IReadOnlySet<string> ConversationCommands { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "BACKUP", "RESTORE", "BACKUP_ALL", "AUTO_BACKUP", "STOP_AUTO_BACKUP", "MARK_IMPORTANT" };

    public static IReadOnlyList<KnotLinkCommandDescriptor> Commands { get; } = Build();
    public static KnotLinkCommandDescriptor? Find(string command) => Commands.FirstOrDefault(
        item => string.Equals(item.Command, command, StringComparison.OrdinalIgnoreCase));

    private static KnotLinkArgumentDescriptor Input(string name, string description, string? defaultValue = null)
        => new(name, description) { DefaultValue = defaultValue };
    private static KnotLinkArgumentDescriptor Choice(string name, string description, string[] values, string? defaultValue = null)
        => new(name, description) { Type = "optional", DefaultValue = defaultValue,
            Options = values.Select(value => new KnotLinkArgumentOption(value, value)).ToArray() };

    private static IReadOnlyList<KnotLinkCommandDescriptor> Build()
    {
        var backup = new KnotLinkArgumentDescriptor[]
        {
            Input("comment", "Optional comment for this operation only."),
            Choice("backup_mode", "Omit to inherit local mode; supplied value overrides this operation only.", ["full", "smart"]),
            Choice("compression_method", "Omit to inherit local compression; supplied value overrides this operation only.", ["LZMA2", "Deflate", "BZip2", "zstd"]),
            Input("compression_level", "Optional integer; omit to inherit local level. Must be valid for the effective compression method."),
            Input("backup_blacklist", "Optional comma-separated rules appended and deduplicated against local rules; empty does not clear."),
            Input("backup_whitelist", "Optional comma-separated rules appended and deduplicated against local rules; nonempty selects whitelist mode."),
            Input("backup_scope", "Optional scope override. full/all/default/none disables the local plugin scope for this operation."),
            Input("scope_dimensions", "Optional scope override: comma-separated overworld, nether, end (including their vanilla Minecraft aliases)."),
            Input("scope_areas", "Optional scope override: x1,z1,x2,z2 block-coordinate rectangles, one per line; requires selected-regions scope.")
        };
        var commands = new List<KnotLinkCommandDescriptor>();
        Add("GET_CAPABILITIES", "Get the runtime FolderRewind funcList manifest.", [], ["content_type", "encoding", "manifest_version", "func_list"]);
        Add("PING", "Check whether the FolderRewind KnotLink endpoint is available.", [], ["message"]);
        Add("LIST_CONFIGS", "List backup configurations.", [], ["data"]);
        Add("LIST_FOLDERS", "List managed folders in a backup configuration.", [], ["data"]);
        Add("LIST_BACKUPS", "List backup archives for a managed folder.", [], ["data"]);
        Add("GET_CONFIG", "Get public settings for a backup configuration.", [], ["data"]);
        Add("GET_STATUS", "Get FolderRewind runtime status.", [], ["data"]);
        Add("BACKUP", "Start a backup for one managed folder.", backup, ["message"]);
        Add("BACKUP_ALL", "Start backing up every folder in a configuration.", backup, ["message"]);
        Add("AUTO_BACKUP", "Start periodic backup bound to this folder and these operation options.",
            backup.Concat([Input("interval_minutes", "Required integer interval, at least 1 minute.", "10")]).ToArray(), ["message"]);
        Add("STOP_AUTO_BACKUP", "Stop periodic backup for one managed folder.", [], ["message"]);
        Add("MARK_IMPORTANT", "Mark or unmark a backup archive as important.",
            [Input("file", "Required backup archive file name."), Choice("important", "Optional importance flag; defaults to true.", ["true", "false"], "true")], ["message"]);
        Add("RESTORE", "Restore a managed folder; default clean, partial backups always overwrite.",
            [Input("file", "Optional archive name; omit for the active Workspace's unique local branch tip."),
             Choice("mode", "Optional restore mode; default clean. Partial backups always overwrite.", ["overwrite", "clean"], "clean"),
             Input("restore_whitelist", "Optional rules appended to local rules: retain matching current paths during clean unless the archive supplies the same path."),
             Choice("preserve_player_data", "Minecraft only: omit to inherit local setting; true/false overrides it. Preserve selected NBT fields for ALL players; players absent from the backup retain their entire current NBT. Advancements/statistics still restore. Cross-26.1-layout preservation is blocked.", ["true", "false"])], ["message"]);
        return commands.AsReadOnly();

        void Add(string command, string description, IReadOnlyList<KnotLinkArgumentDescriptor> options, string[] returns)
        {
            var args = new List<KnotLinkArgumentDescriptor>();
            if (FolderCommands.Contains(command) || command is "LIST_FOLDERS" or "GET_CONFIG" or "BACKUP_ALL")
                args.Add(Input("config_id", "Required backup configuration ID; also accepts name or zero-based index."));
            if (FolderCommands.Contains(command)) args.Add(Input("folder", "Required folder name, path, stable ID or zero-based index."));
            args.AddRange(options);
            if (ConversationCommands.Contains(command))
            {
                args.Add(Input("from", "Required caller identifier."));
                args.Add(Input("request_id", "Required request correlation ID."));
            }
            commands.Add(new(command, description) { Arguments = args.AsReadOnly(), Returns = returns });
        }
    }
}

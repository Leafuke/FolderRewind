using System;
using System.Linq;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace FolderRewind.Services.KnotLink
{
    public static class KnotLinkCommandValidator
    {
        private static readonly IReadOnlySet<string> CommandsRequiringConversationMetadata =
            FolderRewind.Plugin.Abstractions.KnotLinkCoreCommands.ConversationCommands;

        public static IReadOnlySet<string> RequiredMetadataCommandNames => CommandsRequiringConversationMetadata;

        public static bool RequiresConversationMetadata(string command)
        {
            return !string.IsNullOrWhiteSpace(command)
                && CommandsRequiringConversationMetadata.Contains(command.Trim());
        }

        public static KnotLinkCommandValidationResult Validate(KnotLinkCommandContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (!RequiresConversationMetadata(context.Command))
            {
                return KnotLinkCommandValidationResult.Valid;
            }

            var missingMetadataKeys = new List<string>(2);
            if (string.IsNullOrWhiteSpace(context.Metadata.From))
            {
                missingMetadataKeys.Add("from");
            }

            if (string.IsNullOrWhiteSpace(context.Metadata.RequestId))
            {
                missingMetadataKeys.Add("request_id");
            }

            return missingMetadataKeys.Count == 0
                ? KnotLinkCommandValidationResult.Valid
                : KnotLinkCommandValidationResult.MissingConversationMetadata(missingMetadataKeys);
        }

        public static string? ValidateOptions(KnotLinkCommandRequest request)
        {
            foreach (var key in new[] { "current_save", "preserve_player_data", "important" })
                if (request.HasOption(key) && request.GetBool(key) is null)
                    return $"Invalid boolean '{key}'.";
            if (request.GetBool("current_save") == true)
            {
                if (!FolderRewind.Plugin.Abstractions.KnotLinkCoreCommands.FolderCommands.Contains(request.Command))
                    return "current_save is not supported by this command.";
                if (request.HasOption("config_id") || request.HasOption("folder"))
                    return "current_save cannot be combined with config_id or folder.";
            }
            var definition = FolderRewind.Plugin.Abstractions.KnotLinkCoreCommands.Find(request.Command);
            if (definition is null) return null;
            var knownArguments = FolderRewind.Plugin.Abstractions.KnotLinkCoreCommands.Commands
                .SelectMany(command => command.Arguments).Select(argument => argument.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var allowedArguments = definition.Arguments.Select(argument => argument.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var key in request.Options.Keys)
            {
                if (key is "cmd" or "from" or "request_id" or "reply_to" or "protocol_version" or "flow") continue;
                if (key == "current_save")
                {
                    if (!FolderRewind.Plugin.Abstractions.KnotLinkCoreCommands.FolderCommands.Contains(request.Command))
                        return "current_save is not supported by this command.";
                    continue;
                }
                if ((knownArguments.Contains(key) && !allowedArguments.Contains(key))
                    || (key.StartsWith("scope_", StringComparison.OrdinalIgnoreCase) && !allowedArguments.Contains("backup_scope")))
                    return $"Parameter '{key}' is not supported by {request.Command}.";
            }
            foreach (var argument in definition.Arguments)
            {
                if (argument.Options.Count == 0 || !request.HasOption(argument.Name)
                    || argument.Name is "important" or "preserve_player_data") continue;
                var value = request.GetStringOrDefault(argument.Name).Trim();
                if (argument.Name == "backup_mode" && value.Equals("incremental", StringComparison.OrdinalIgnoreCase)) continue;
                if (!argument.Options.Any(option => option.Value.Equals(value, StringComparison.OrdinalIgnoreCase)))
                    return $"Invalid {argument.Name} '{value}'.";
            }
            return null;
        }
    }
}

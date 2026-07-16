using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace FolderRewind.Services.KnotLink
{
    public static class KnotLinkCommandValidator
    {
        private static readonly FrozenSet<string> CommandsRequiringConversationMetadata = new[]
        {
            "BACKUP",
            "RESTORE",
            "BACKUP_ALL",
            "AUTO_BACKUP",
            "STOP_AUTO_BACKUP",
            "MARK_IMPORTANT"
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

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
    }
}

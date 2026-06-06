using System;
using System.Collections.Generic;

namespace FolderRewind.Services.KnotLink
{
    public static class KnotLinkCommandValidator
    {
        private static readonly HashSet<string> CommandsRequiringConversationMetadata = new(StringComparer.OrdinalIgnoreCase)
        {
            "BACKUP",
            "RESTORE",
            "BACKUP_ALL",
            "AUTO_BACKUP",
            "STOP_AUTO_BACKUP",
            "MARK_IMPORTANT"
        };

        public static KnotLinkCommandValidationResult Validate(KnotLinkCommandContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (!context.IsParameterized)
            {
                return KnotLinkCommandValidationResult.Valid;
            }

            if (context.Request.HasOption("world"))
            {
                return KnotLinkCommandValidationResult.DeprecatedWorldOption("world");
            }

            if (!CommandsRequiringConversationMetadata.Contains(context.Request.Command))
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

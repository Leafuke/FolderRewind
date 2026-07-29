using System;
using System.Collections.Generic;

namespace FolderRewind.Services.KnotLink
{
    public enum KnotLinkCommandValidationError
    {
        None = 0,
        MissingConversationMetadata
    }

    public sealed class KnotLinkCommandValidationResult
    {
        private KnotLinkCommandValidationResult(
            bool isValid,
            KnotLinkCommandValidationError error,
            IReadOnlyList<string>? missingMetadataKeys = null)
        {
            IsValid = isValid;
            Error = error;
            MissingMetadataKeys = missingMetadataKeys ?? Array.Empty<string>();
        }

        public bool IsValid { get; }

        public KnotLinkCommandValidationError Error { get; }

        public IReadOnlyList<string> MissingMetadataKeys { get; }

        public static KnotLinkCommandValidationResult Valid { get; } = new(
            true,
            KnotLinkCommandValidationError.None);

        public static KnotLinkCommandValidationResult MissingConversationMetadata(IReadOnlyList<string> missingMetadataKeys)
        {
            return new KnotLinkCommandValidationResult(
                false,
                KnotLinkCommandValidationError.MissingConversationMetadata,
                missingMetadataKeys);
        }

    }
}

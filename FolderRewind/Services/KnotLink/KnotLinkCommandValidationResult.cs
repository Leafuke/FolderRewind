using System;
using System.Collections.Generic;

namespace FolderRewind.Services.KnotLink
{
    public enum KnotLinkCommandValidationError
    {
        None = 0,
        MissingConversationMetadata,
        DeprecatedWorldOption
    }

    public sealed class KnotLinkCommandValidationResult
    {
        private KnotLinkCommandValidationResult(
            bool isValid,
            KnotLinkCommandValidationError error,
            IReadOnlyList<string>? missingMetadataKeys = null,
            string? deprecatedOption = null)
        {
            IsValid = isValid;
            Error = error;
            MissingMetadataKeys = missingMetadataKeys ?? Array.Empty<string>();
            DeprecatedOption = deprecatedOption;
        }

        public bool IsValid { get; }

        public KnotLinkCommandValidationError Error { get; }

        public IReadOnlyList<string> MissingMetadataKeys { get; }

        public string? DeprecatedOption { get; }

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

        public static KnotLinkCommandValidationResult DeprecatedWorldOption(string deprecatedOption)
        {
            return new KnotLinkCommandValidationResult(
                false,
                KnotLinkCommandValidationError.DeprecatedWorldOption,
                deprecatedOption: deprecatedOption);
        }
    }
}

using System;

namespace FolderRewind.Services.KnotLink
{
    public sealed class KnotLinkCommandParseException : Exception
    {
        public KnotLinkCommandParseException(string message) : base(message)
        {
        }
    }

    public static class KnotLinkCommandParser
    {
        public static bool HasV2CommandField(string? rawPayload) =>
            KnotLinkKeyValueCodec.HasCommandField(rawPayload);

        public static KnotLinkCommandRequest Parse(string rawPayload)
        {
            var payload = KnotLinkKeyValueCodec.Parse(rawPayload);
            if (!payload.Values.TryGetValue("cmd", out var command) || string.IsNullOrWhiteSpace(command))
            {
                throw new KnotLinkCommandParseException("Missing or empty cmd field.");
            }

            foreach (var c in command)
            {
                if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_'))
                {
                    throw new KnotLinkCommandParseException("The cmd value may contain only letters, digits, and underscores.");
                }
            }

            return new KnotLinkCommandRequest(command, rawPayload, payload.Values, payload.EncodedValues);
        }
    }
}

using System.Collections.Generic;

namespace FolderRewind.Services.KnotLink
{
    public sealed class KnotLinkCommandMetadata
    {
        private KnotLinkCommandMetadata(
            string? from,
            string? requestId,
            string? replyTo,
            string? protocolVersion,
            string? flow)
        {
            From = from;
            RequestId = requestId;
            ReplyTo = replyTo;
            ProtocolVersion = protocolVersion;
            Flow = flow;
        }

        public string? From { get; }

        public string? RequestId { get; }

        public string? ReplyTo { get; }

        public string? ProtocolVersion { get; }

        public string? Flow { get; }

        public bool HasConversation => !string.IsNullOrWhiteSpace(From)
            || !string.IsNullOrWhiteSpace(RequestId);

        public bool HasCompleteConversation => !string.IsNullOrWhiteSpace(From)
            && !string.IsNullOrWhiteSpace(RequestId);

        public static KnotLinkCommandMetadata Empty { get; } = new(null, null, null, null, null);

        public static KnotLinkCommandMetadata FromRequest(KnotLinkCommandRequest request)
        {
            System.ArgumentNullException.ThrowIfNull(request);

            return new KnotLinkCommandMetadata(
                request.GetString("from"),
                request.GetString("request_id"),
                request.GetString("reply_to"),
                request.GetString("protocol_version"),
                request.GetString("flow"));
        }

        public IReadOnlyDictionary<string, string?> ToConversationFields()
        {
            var fields = new Dictionary<string, string?>();
            if (!string.IsNullOrWhiteSpace(From))
            {
                fields["from"] = From;
            }

            if (!string.IsNullOrWhiteSpace(RequestId))
            {
                fields["request_id"] = RequestId;
            }

            return fields;
        }
    }
}

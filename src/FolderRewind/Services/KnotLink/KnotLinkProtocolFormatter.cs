using System;
using System.Collections.Generic;

namespace FolderRewind.Services.KnotLink
{
    public static class KnotLinkProtocolFormatter
    {
        public static string FormatOk(
            KnotLinkCommandContext context,
            IReadOnlyDictionary<string, string?>? fields = null)
        {
            ArgumentNullException.ThrowIfNull(context);
            return FormatFields(context, "ok", null, fields);
        }

        public static string FormatMessageOk(KnotLinkCommandContext context, string? message) =>
            FormatOk(context, new Dictionary<string, string?> { ["message"] = message });

        public static string FormatError(KnotLinkCommandContext context, string? message) =>
            FormatFields(context, "error", null, new Dictionary<string, string?> { ["message"] = message });

        public static string FormatEvent(
            KnotLinkCommandContext? context,
            string eventName,
            IReadOnlyDictionary<string, string?>? fields = null) =>
            FormatFields(context, null, eventName, fields);

        public static string FormatHandlerResponse(
            KnotLinkCommandContext context,
            string? handlerResponse,
            bool treatOkPayloadAsData = false)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (string.IsNullOrWhiteSpace(handlerResponse)) return FormatOk(context);

            if (handlerResponse.StartsWith("status=", StringComparison.OrdinalIgnoreCase))
            {
                // Validate and canonicalize plugin-provided v2 responses.
                var parsed = KnotLinkKeyValueCodec.Parse(handlerResponse);
                return KnotLinkKeyValueCodec.Serialize(ToNullableFields(parsed.Values));
            }

            if (handlerResponse.StartsWith("OK:", StringComparison.OrdinalIgnoreCase))
            {
                var payload = handlerResponse[3..];
                if (payload.Length == 0) return FormatOk(context);
                return treatOkPayloadAsData
                    ? FormatOk(context, new Dictionary<string, string?> { ["data"] = payload })
                    : FormatMessageOk(context, payload);
            }

            if (handlerResponse.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
            {
                return FormatError(context, handlerResponse[6..]);
            }

            return FormatMessageOk(context, handlerResponse);
        }

        public static string EncodeValue(string? value) => KnotLinkKeyValueCodec.EncodeValue(value);

        private static string FormatFields(
            KnotLinkCommandContext? context,
            string? status,
            string? eventName,
            IReadOnlyDictionary<string, string?>? fields)
        {
            var parts = new List<KeyValuePair<string, string?>>();
            if (status != null) parts.Add(new("status", status));
            if (eventName != null) parts.Add(new("event", eventName));

            if (!string.IsNullOrWhiteSpace(context?.Metadata.From)) parts.Add(new("from", context.Metadata.From));
            if (!string.IsNullOrWhiteSpace(context?.Metadata.RequestId)) parts.Add(new("request_id", context.Metadata.RequestId));

            if (fields != null)
            {
                foreach (var field in fields)
                {
                    if (IsReserved(field.Key, status != null, eventName != null, context)) continue;
                    parts.Add(new(field.Key, field.Value));
                }
            }

            return KnotLinkKeyValueCodec.Serialize(parts);
        }

        private static bool IsReserved(string key, bool hasStatus, bool hasEvent, KnotLinkCommandContext? context) =>
            (hasStatus && key.Equals("status", StringComparison.OrdinalIgnoreCase)) ||
            (hasEvent && key.Equals("event", StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(context?.Metadata.From) && key.Equals("from", StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(context?.Metadata.RequestId) && key.Equals("request_id", StringComparison.OrdinalIgnoreCase));

        private static IEnumerable<KeyValuePair<string, string?>> ToNullableFields(IReadOnlyDictionary<string, string> values)
        {
            foreach (var value in values) yield return new(value.Key, value.Value);
        }
    }
}

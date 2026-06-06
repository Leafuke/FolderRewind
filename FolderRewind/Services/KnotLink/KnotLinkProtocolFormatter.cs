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

            return "OK:" + FormatFields(context, null, fields);
        }

        public static string FormatMessageOk(KnotLinkCommandContext context, string? message)
        {
            return FormatOk(context, new Dictionary<string, string?>
            {
                ["message"] = message
            });
        }

        public static string FormatError(KnotLinkCommandContext context, string? message)
        {
            ArgumentNullException.ThrowIfNull(context);

            return "ERROR:" + FormatFields(context, null, new Dictionary<string, string?>
            {
                ["message"] = message
            });
        }

        public static string FormatEvent(
            KnotLinkCommandContext context,
            string eventName,
            IReadOnlyDictionary<string, string?>? fields = null)
        {
            ArgumentNullException.ThrowIfNull(context);

            return FormatFields(context, eventName, fields);
        }

        public static string FormatHandlerResponse(
            KnotLinkCommandContext context,
            string? handlerResponse,
            bool treatOkPayloadAsData = false)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (string.IsNullOrWhiteSpace(handlerResponse))
            {
                return context.Metadata.HasConversation ? FormatOk(context) : "OK:";
            }

            if (!context.Metadata.HasConversation)
            {
                return handlerResponse;
            }

            if (handlerResponse.StartsWith("OK:", StringComparison.OrdinalIgnoreCase))
            {
                var payload = handlerResponse[3..];
                if (string.IsNullOrEmpty(payload))
                {
                    return FormatOk(context);
                }

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

        public static string EncodeValue(string? value)
        {
            return Uri.EscapeDataString(value ?? string.Empty);
        }

        private static string FormatFields(
            KnotLinkCommandContext context,
            string? eventName,
            IReadOnlyDictionary<string, string?>? fields)
        {
            var parts = new List<string>();

            if (eventName != null)
            {
                AddField(parts, "event", eventName);
            }

            var from = FirstNonWhiteSpace(context.Metadata.From, GetField(fields, "from"));
            if (!string.IsNullOrWhiteSpace(from))
            {
                AddField(parts, "from", from);
            }

            var requestId = FirstNonWhiteSpace(context.Metadata.RequestId, GetField(fields, "request_id"));
            if (!string.IsNullOrWhiteSpace(requestId))
            {
                AddField(parts, "request_id", requestId);
            }

            var command = GetField(fields, "command");
            if (command != null)
            {
                AddField(parts, "command", command);
            }

            if (fields != null)
            {
                foreach (var field in fields)
                {
                    if (IsReservedFieldKey(field.Key))
                    {
                        continue;
                    }

                    AddField(parts, field.Key, field.Value);
                }
            }

            return string.Join(';', parts);
        }

        private static void AddField(List<string> parts, string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            parts.Add($"{key}={EncodeValue(value)}");
        }

        private static string? GetField(IReadOnlyDictionary<string, string?>? fields, string key)
        {
            if (fields == null)
            {
                return null;
            }

            foreach (var field in fields)
            {
                if (string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return field.Value;
                }
            }

            return null;
        }

        private static string? FirstNonWhiteSpace(string? first, string? second)
        {
            return !string.IsNullOrWhiteSpace(first) ? first : second;
        }

        private static bool IsReservedFieldKey(string key)
        {
            return string.Equals(key, "event", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "from", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "request_id", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "command", StringComparison.OrdinalIgnoreCase);
        }
    }
}

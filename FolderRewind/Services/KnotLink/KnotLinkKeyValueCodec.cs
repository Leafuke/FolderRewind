using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FolderRewind.Services.KnotLink
{
    /// <summary>
    /// KnotLink v2 payload codec. The wire representation is a strict
    /// key=value;key2=value2 map whose values use RFC 3986 percent encoding.
    /// </summary>
    public static partial class KnotLinkKeyValueCodec
    {
        [GeneratedRegex("^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant)]
        private static partial Regex KeyRegex();

        public static bool HasCommandField(string? payload)
        {
            if (string.IsNullOrEmpty(payload)) return false;

            foreach (var segment in payload.Split(';', StringSplitOptions.None))
            {
                var separator = segment.IndexOf('=');
                if (separator <= 0) continue;
                if (string.Equals(segment[..separator], "cmd", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public static KnotLinkKeyValuePayload Parse(string payload)
        {
            if (string.IsNullOrEmpty(payload))
            {
                throw new KnotLinkCommandParseException("The v2 payload is empty.");
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var encodedValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var segment in payload.Split(';', StringSplitOptions.None))
            {
                if (segment.Length == 0)
                {
                    throw new KnotLinkCommandParseException("Empty key-value segment is not allowed.");
                }

                var separator = segment.IndexOf('=');
                if (separator <= 0 || separator != segment.LastIndexOf('='))
                {
                    throw new KnotLinkCommandParseException($"Invalid key-value segment: {segment}");
                }

                var key = NormalizeKey(segment[..separator]);
                if (!KeyRegex().IsMatch(key))
                {
                    throw new KnotLinkCommandParseException($"Invalid key: {segment[..separator]}");
                }

                if (values.ContainsKey(key))
                {
                    throw new KnotLinkCommandParseException($"Duplicate key: {key}");
                }

                var encodedValue = segment[(separator + 1)..];
                ValidateEncodedValue(encodedValue);
                encodedValues[key] = encodedValue;
                values[key] = DecodeValue(encodedValue);
            }

            return new KnotLinkKeyValuePayload(values, encodedValues);
        }

        public static string Serialize(IEnumerable<KeyValuePair<string, string?>> fields)
        {
            ArgumentNullException.ThrowIfNull(fields);
            var parts = new List<string>();
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var field in fields)
            {
                var key = NormalizeKey(field.Key);
                if (!KeyRegex().IsMatch(key))
                {
                    throw new ArgumentException($"Invalid KnotLink key: {field.Key}", nameof(fields));
                }

                if (!keys.Add(key))
                {
                    throw new ArgumentException($"Duplicate KnotLink key: {key}", nameof(fields));
                }

                parts.Add($"{key}={EncodeValue(field.Value)}");
            }

            return string.Join(';', parts);
        }

        public static string EncodeValue(string? value) => Uri.EscapeDataString(value ?? string.Empty);

        public static string DecodeValue(string encodedValue)
        {
            ValidateEncodedValue(encodedValue ?? string.Empty);
            return Uri.UnescapeDataString(encodedValue ?? string.Empty);
        }

        public static IReadOnlyList<string> DecodeList(string encodedValue)
        {
            if (string.IsNullOrEmpty(encodedValue)) return Array.Empty<string>();

            var result = new List<string>();
            foreach (var item in encodedValue.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                ValidateEncodedValue(item);
                var decoded = Uri.UnescapeDataString(item);
                if (!string.IsNullOrWhiteSpace(decoded)) result.Add(decoded);
            }

            return result;
        }

        public static string EncodeList(IEnumerable<string> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            var encoded = new List<string>();
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value)) encoded.Add(EncodeValue(value));
            }

            return string.Join(',', encoded);
        }

        public static string NormalizeKey(string key) => (key ?? string.Empty).ToLowerInvariant();

        private static void ValidateEncodedValue(string value)
        {
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (IsUnreserved(c) || c == ',') continue;
                if (c == '%' && i + 2 < value.Length && IsHex(value[i + 1]) && IsHex(value[i + 2]))
                {
                    i += 2;
                    continue;
                }

                throw new KnotLinkCommandParseException(
                    $"Value contains a character that must be percent-encoded: U+{(int)c:X4}");
            }
        }

        private static bool IsUnreserved(char c) =>
            (c >= 'A' && c <= 'Z') ||
            (c >= 'a' && c <= 'z') ||
            (c >= '0' && c <= '9') ||
            c is '-' or '.' or '_' or '~';

        private static bool IsHex(char c) =>
            (c >= '0' && c <= '9') ||
            (c >= 'A' && c <= 'F') ||
            (c >= 'a' && c <= 'f');
    }

    public sealed class KnotLinkKeyValuePayload
    {
        internal KnotLinkKeyValuePayload(
            IReadOnlyDictionary<string, string> values,
            IReadOnlyDictionary<string, string> encodedValues)
        {
            Values = values;
            EncodedValues = encodedValues;
        }

        public IReadOnlyDictionary<string, string> Values { get; }

        internal IReadOnlyDictionary<string, string> EncodedValues { get; }
    }
}

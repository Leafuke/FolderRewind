using System;
using System.Collections.Generic;

namespace FolderRewind.Services.KnotLink
{
    /// <summary>KnotLink strict key-value v2 request.</summary>
    public sealed class KnotLinkCommandRequest
    {
        private readonly IReadOnlyDictionary<string, string> _encodedOptions;

        internal KnotLinkCommandRequest(
            string command,
            string rawPayload,
            IReadOnlyDictionary<string, string> options,
            IReadOnlyDictionary<string, string> encodedOptions)
        {
            Command = (command ?? string.Empty).ToUpperInvariant();
            RawPayload = rawPayload ?? string.Empty;
            Options = options;
            _encodedOptions = encodedOptions;
        }

        public string Command { get; }

        public string RawPayload { get; }

        public IReadOnlyDictionary<string, string> Options { get; }

        public bool HasOption(string key) =>
            !string.IsNullOrWhiteSpace(key) && Options.ContainsKey(NormalizeKey(key));

        public string? GetString(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            return Options.TryGetValue(NormalizeKey(key), out var value) ? value : null;
        }

        public string GetStringOrDefault(string key, string defaultValue = "") => GetString(key) ?? defaultValue;

        public bool? GetBool(string key)
        {
            var value = GetString(key);
            if (string.IsNullOrWhiteSpace(value)) return null;

            return value.Trim().ToLowerInvariant() switch
            {
                "true" or "1" or "yes" or "y" or "on" => true,
                "false" or "0" or "no" or "n" or "off" => false,
                _ => null
            };
        }

        public bool GetBoolOrDefault(string key, bool defaultValue = false) => GetBool(key) ?? defaultValue;

        public IReadOnlyList<string> GetList(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return Array.Empty<string>();
            return _encodedOptions.TryGetValue(NormalizeKey(key), out var value)
                ? KnotLinkKeyValueCodec.DecodeList(value)
                : Array.Empty<string>();
        }

        public static string NormalizeKey(string key) => KnotLinkKeyValueCodec.NormalizeKey(key);
    }
}

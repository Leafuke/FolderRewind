using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services.KnotLink
{
    /// <summary>
    /// KnotLink 远程指令的统一请求模型。
    /// 旧版位置参数仍放在 LegacyArgs；新版 -key=value 参数统一进入 Options。
    /// </summary>
    public sealed class KnotLinkCommandRequest
    {
        public KnotLinkCommandRequest(
            string command,
            string legacyArgs,
            string rawCommand,
            IReadOnlyDictionary<string, string>? options = null)
        {
            Command = (command ?? string.Empty).Trim().ToUpperInvariant();
            LegacyArgs = legacyArgs ?? string.Empty;
            RawCommand = rawCommand ?? string.Empty;
            Options = options ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public string Command { get; }

        public string LegacyArgs { get; }

        public string RawCommand { get; }

        public IReadOnlyDictionary<string, string> Options { get; }

        public bool IsParameterized => Options.Count > 0;

        public bool HasOption(string key)
        {
            return !string.IsNullOrWhiteSpace(key) && Options.ContainsKey(NormalizeKey(key));
        }

        public string? GetString(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            return Options.TryGetValue(NormalizeKey(key), out var value) ? value : null;
        }

        public string GetStringOrDefault(string key, string defaultValue = "")
        {
            return GetString(key) ?? defaultValue;
        }

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

        public bool GetBoolOrDefault(string key, bool defaultValue = false)
        {
            return GetBool(key) ?? defaultValue;
        }

        public IReadOnlyList<string> GetList(string key)
        {
            var value = GetString(key);
            if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();

            return value
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToList();
        }

        public static string NormalizeKey(string key)
        {
            return (key ?? string.Empty)
                .Trim()
                .TrimStart('-')
                .Replace('-', '_')
                .ToLowerInvariant();
        }
    }
}

using System;
using System.Collections.Generic;

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
        /// <summary>
        /// 解析 KnotLink 指令。
        /// 这里故意不模拟完整 shell：远程协议只接受 COMMAND -key=value，降低长期维护和安全审计成本。
        /// </summary>
        public static KnotLinkCommandRequest Parse(string rawCommand)
        {
            if (string.IsNullOrWhiteSpace(rawCommand))
            {
                throw new KnotLinkCommandParseException(I18n.GetString("KnotLink_Parse_EmptyCommand"));
            }

            var trimmed = rawCommand.Trim();
            var firstSpace = FindFirstWhitespace(trimmed);
            var command = firstSpace < 0 ? trimmed : trimmed[..firstSpace];
            var args = firstSpace < 0 ? string.Empty : trimmed[(firstSpace + 1)..].Trim();

            if (string.IsNullOrWhiteSpace(command))
            {
                throw new KnotLinkCommandParseException(I18n.GetString("KnotLink_Parse_EmptyCommand"));
            }

            if (string.IsNullOrWhiteSpace(args) || !args.StartsWith("-", StringComparison.Ordinal))
            {
                return new KnotLinkCommandRequest(command, args, rawCommand);
            }

            var options = ParseOptions(args);
            return new KnotLinkCommandRequest(command, args, rawCommand, options);
        }

        private static Dictionary<string, string> ParseOptions(string args)
        {
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var i = 0;

            while (i < args.Length)
            {
                SkipWhitespace(args, ref i);
                if (i >= args.Length) break;

                if (args[i] != '-')
                {
                    throw new KnotLinkCommandParseException(I18n.Format("KnotLink_Parse_ExpectedOption", args[i]));
                }

                var keyStart = i;
                while (i < args.Length && args[i] != '=' && !char.IsWhiteSpace(args[i]))
                {
                    i++;
                }

                if (i >= args.Length || args[i] != '=')
                {
                    var token = args[keyStart..Math.Min(i, args.Length)];
                    throw new KnotLinkCommandParseException(I18n.Format("KnotLink_Parse_InvalidOption", token));
                }

                var rawKey = args[keyStart..i];
                var key = KnotLinkCommandRequest.NormalizeKey(rawKey);
                if (string.IsNullOrWhiteSpace(key))
                {
                    throw new KnotLinkCommandParseException(I18n.GetString("KnotLink_Parse_EmptyOptionName"));
                }

                i++; // skip '='
                var value = ParseValue(args, ref i);

                if (options.ContainsKey(key))
                {
                    throw new KnotLinkCommandParseException(I18n.Format("KnotLink_Parse_DuplicateOption", key));
                }

                options[key] = value;
            }

            return options;
        }

        private static string ParseValue(string args, ref int i)
        {
            if (i >= args.Length) return string.Empty;

            if (args[i] == '"')
            {
                i++;
                var value = new System.Text.StringBuilder();
                var closed = false;

                while (i < args.Length)
                {
                    var c = args[i++];
                    if (c == '"')
                    {
                        closed = true;
                        break;
                    }

                    if (c == '\\' && i < args.Length)
                    {
                        var escaped = args[i++];
                        value.Append(escaped switch
                        {
                            '"' => '"',
                            '\\' => '\\',
                            'n' => '\n',
                            'r' => '\r',
                            't' => '\t',
                            _ => escaped
                        });
                        continue;
                    }

                    value.Append(c);
                }

                if (!closed)
                {
                    throw new KnotLinkCommandParseException(I18n.GetString("KnotLink_Parse_UnclosedQuote"));
                }

                SkipWhitespace(args, ref i);
                return value.ToString();
            }

            var start = i;
            while (i < args.Length && !char.IsWhiteSpace(args[i]))
            {
                i++;
            }

            return args[start..i];
        }

        private static int FindFirstWhitespace(string value)
        {
            for (var i = 0; i < value.Length; i++)
            {
                if (char.IsWhiteSpace(value[i])) return i;
            }

            return -1;
        }

        private static void SkipWhitespace(string value, ref int index)
        {
            while (index < value.Length && char.IsWhiteSpace(value[index]))
            {
                index++;
            }
        }
    }
}

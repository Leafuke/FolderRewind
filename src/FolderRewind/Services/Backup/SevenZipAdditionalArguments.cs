using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FolderRewind.Services
{
    /// <summary>
    /// Validates protected user-supplied 7-Zip switches before they are appended to backup commands.
    /// </summary>
    public static class SevenZipAdditionalArguments
    {
        public static ValidationResult Validate(string? input)
        {
            string arguments = input?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(arguments))
            {
                return ValidationResult.Valid(string.Empty);
            }

            var tokenizeResult = Tokenize(arguments);
            if (!tokenizeResult.IsValid)
            {
                return ValidationResult.Invalid(tokenizeResult.ErrorMessage);
            }

            var unsupportedTokens = tokenizeResult.Tokens
                .Where(IsUnsupportedToken)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (unsupportedTokens.Count > 0)
            {
                return ValidationResult.Invalid(I18n.Format(
                    "ConfigSettingsDialog_Additional7zArgsUnsupported",
                    string.Join(", ", unsupportedTokens)));
            }

            return ValidationResult.Valid(arguments);
        }

        public static bool AppendValidated(StringBuilder builder, string? input, out string? errorMessage)
        {
            ArgumentNullException.ThrowIfNull(builder);

            var result = Validate(input);
            if (!result.IsValid)
            {
                errorMessage = result.ErrorMessage;
                return false;
            }

            if (!string.IsNullOrWhiteSpace(result.Arguments))
            {
                if (builder.Length > 0 && !char.IsWhiteSpace(builder[builder.Length - 1]))
                {
                    builder.Append(' ');
                }

                builder.Append(result.Arguments);
            }

            errorMessage = null;
            return true;
        }

        private static TokenizeResult Tokenize(string input)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            bool inQuote = false;

            foreach (char ch in input)
            {
                if (ch == '"')
                {
                    inQuote = !inQuote;
                    current.Append(ch);
                    continue;
                }

                if (char.IsWhiteSpace(ch) && !inQuote)
                {
                    AddTokenIfPresent(tokens, current);
                    continue;
                }

                current.Append(ch);
            }

            if (inQuote)
            {
                return TokenizeResult.Invalid(I18n.GetString("ConfigSettingsDialog_Additional7zArgsUnclosedQuote"));
            }

            AddTokenIfPresent(tokens, current);
            return TokenizeResult.Valid(tokens);
        }

        private static void AddTokenIfPresent(List<string> tokens, StringBuilder current)
        {
            if (current.Length == 0)
            {
                return;
            }

            tokens.Add(current.ToString());
            current.Clear();
        }

        private static bool IsUnsupportedToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            string trimmed = token.Trim();
            if (trimmed.StartsWith('@'))
            {
                return true;
            }

            if (string.Equals(trimmed, "a", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "u", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!trimmed.StartsWith('-') && !trimmed.StartsWith('/'))
            {
                return true;
            }

            string option = trimmed[1..].ToLowerInvariant();
            return option.StartsWith("t", StringComparison.Ordinal)
                || option.StartsWith("mx", StringComparison.Ordinal)
                || option.StartsWith("m0", StringComparison.Ordinal)
                || option.StartsWith("mmt", StringComparison.Ordinal)
                || option.StartsWith("p", StringComparison.Ordinal)
                || option.StartsWith("mhe", StringComparison.Ordinal)
                || option.StartsWith("o", StringComparison.Ordinal)
                || string.Equals(option, "y", StringComparison.Ordinal)
                || option.StartsWith("bsp", StringComparison.Ordinal)
                || option.StartsWith("x", StringComparison.Ordinal)
                || option.StartsWith("i", StringComparison.Ordinal);
        }

        private sealed record TokenizeResult(bool IsValid, IReadOnlyList<string> Tokens, string? ErrorMessage)
        {
            public static TokenizeResult Valid(IReadOnlyList<string> tokens) => new(true, tokens, null);

            public static TokenizeResult Invalid(string? errorMessage) => new(false, Array.Empty<string>(), errorMessage);
        }

        public sealed record ValidationResult(bool IsValid, string Arguments, string? ErrorMessage)
        {
            public static ValidationResult Valid(string arguments) => new(true, arguments, null);

            public static ValidationResult Invalid(string? errorMessage) => new(false, string.Empty, errorMessage);
        }
    }
}

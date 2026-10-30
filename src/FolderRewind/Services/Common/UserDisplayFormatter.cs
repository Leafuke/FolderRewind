using System;
using System.Globalization;

namespace FolderRewind.Services;

/// <summary>
/// Formats values that are shown to the user. Persistence, file names, logs and protocol values
/// must continue to use their own invariant formats instead of this helper.
/// </summary>
public static class UserDisplayFormatter
{
    public static string Date(DateTime value) => value.ToString("d", CultureInfo.CurrentCulture);

    public static string Date(DateTimeOffset value) => value.ToString("d", CultureInfo.CurrentCulture);

    public static string ShortTime(DateTime value) => value.ToString("t", CultureInfo.CurrentCulture);

    public static string ShortTime(DateTimeOffset value) => value.ToString("t", CultureInfo.CurrentCulture);

    public static string LongTime(DateTime value) => value.ToString("T", CultureInfo.CurrentCulture);

    public static string LongTime(DateTimeOffset value) => value.ToString("T", CultureInfo.CurrentCulture);

    public static string ShortDateTime(DateTime value) => value.ToString("g", CultureInfo.CurrentCulture);

    public static string ShortDateTime(DateTimeOffset value) => value.ToString("g", CultureInfo.CurrentCulture);

    public static string LongDateTime(DateTime value) => value.ToString("G", CultureInfo.CurrentCulture);

    public static string LongDateTime(DateTimeOffset value) => value.ToString("G", CultureInfo.CurrentCulture);

    public static string Number(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    public static string Number(double value, int decimalPlaces) =>
        value.ToString($"N{decimalPlaces}", CultureInfo.CurrentCulture);

    public static string PersistedLocalDateTime(string? value, string invariantFormat)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return DateTime.TryParseExact(
            value,
            invariantFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? ShortDateTime(parsed)
            : value;
    }
}

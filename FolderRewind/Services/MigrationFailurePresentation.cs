using FolderRewind.History.Storage;
using System;
using System.IO;
using System.Text.Json;

namespace FolderRewind.Services;

internal static class MigrationFailurePresentation
{
    internal static string Kind(Exception error) => error switch
    {
        HistoryPackCompatibilityException => "Format",
        InvalidDataException data when data.Message.StartsWith("config_schema_newer_than_host:", StringComparison.Ordinal) => "Format",
        FileNotFoundException or DirectoryNotFoundException => "Missing",
        UnauthorizedAccessException => "Permission",
        InvalidDataException or JsonException or HistoryRepositoryException => "Data",
        IOException io when (io.HResult & 0xFFFF) is 112 or 39 => "Space",
        IOException => "Io",
        _ => "Unexpected"
    };
    internal static string Message(Exception error) => I18n.GetString("MigrationFailure_" + Kind(error));
}

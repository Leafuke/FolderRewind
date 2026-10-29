using System;
using System.IO;

namespace FolderRewind.Services;

internal enum ClassicStartupState { Disabled, Enabled, DisabledByUser, Unknown }

internal static class ClassicStartupPolicy
{
    internal static bool IsOwnedCommand(string? command, string? executable)
    {
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(executable)) return false;
        var trimmed = command.Trim();
        if (!trimmed.StartsWith('"')) return false;
        var end = trimmed.IndexOf('"', 1);
        if (end <= 1 || !string.Equals(trimmed[(end + 1)..].Trim(), "--startup", StringComparison.OrdinalIgnoreCase)) return false;
        try { return string.Equals(Path.GetFullPath(trimmed[1..end]), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    internal static ClassicStartupState Evaluate(string? command, string? executable, byte[]? approved)
    {
        if (!IsOwnedCommand(command, executable)) return ClassicStartupState.Disabled;
        if (approved == null) return ClassicStartupState.Enabled;
        if (approved.Length != 12) return ClassicStartupState.Unknown;
        var value = BitConverter.ToUInt32(approved, 0);
        return value switch { 2 or 6 => ClassicStartupState.Enabled, 3 or 7 => ClassicStartupState.DisabledByUser, _ => ClassicStartupState.Unknown };
    }
}

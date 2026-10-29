using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FolderRewind.Services;

/// <summary>Backup passwords are protected by the current Windows user's DPAPI identity.</summary>
public static class EncryptionService
{
    private static readonly byte[] AdditionalEntropy = Encoding.UTF8.GetBytes("FolderRewind.EncryptionService.v1");
    private static ProtectedPasswordStore Store => new(Path.Combine(ConfigService.ConfigDirectory, "encrypted_passwords.dat"),
        bytes => ProtectedData.Protect(bytes, AdditionalEntropy, DataProtectionScope.CurrentUser),
        bytes => ProtectedData.Unprotect(bytes, AdditionalEntropy, DataProtectionScope.CurrentUser));

    public static void StorePassword(string configId, string password) => Store.Write(configId, password);
    public static string? RetrievePassword(string configId)
    {
        if (string.IsNullOrEmpty(configId)) return null;
        try { return Store.Read(configId); }
        catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException
            or FormatException or System.Text.Json.JsonException)
        {
            LogService.Log($"[EncryptionService] Password is unavailable for config {configId}: {error.GetType().Name}");
            return null;
        }
    }
    public static bool HasStoredPassword(string configId) => !string.IsNullOrEmpty(RetrievePassword(configId));
    public static string SanitizeForLog(string text, string configId)
        => string.IsNullOrEmpty(text) || RetrievePassword(configId) is not { Length: > 0 } password
            ? text : text.Replace(password, "***", StringComparison.Ordinal);
    public static void RemovePassword(string configId) { if (!string.IsNullOrEmpty(configId)) Store.Remove(configId); }
    public static bool VerifyPassword(string configId, string inputPassword)
    {
        if (string.IsNullOrEmpty(inputPassword) || RetrievePassword(configId) is not { } stored) return false;
        var first = Encoding.UTF8.GetBytes(stored); var second = Encoding.UTF8.GetBytes(inputPassword);
        try { return CryptographicOperations.FixedTimeEquals(first, second); }
        finally { CryptographicOperations.ZeroMemory(first); CryptographicOperations.ZeroMemory(second); }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FolderRewind.Models;

namespace FolderRewind.Services
{
    internal static class GitHubSecretStoreService
    {
        private const string StoreFileName = "github_secure_store.dat";
        private const string ClientSecretKey = "oauth_client_secret";
        private const string AccessTokenKey = "oauth_access_token";
        private const string UserLoginKey = "oauth_user_login";
        private static readonly string StorePath = Path.Combine(ConfigService.ConfigDirectory, StoreFileName);
        private static readonly byte[] AdditionalEntropy = Encoding.UTF8.GetBytes("FolderRewind.GitHubSecretStore.v1");

        public static string GetClientSecret()
        {
            return GetValue(ClientSecretKey);
        }

        public static void SetClientSecret(string? secret)
        {
            SetValue(ClientSecretKey, secret);
        }

        public static string GetAccessToken()
        {
            return GetValue(AccessTokenKey);
        }

        public static void SetAccessToken(string? token)
        {
            SetValue(AccessTokenKey, token);
        }

        public static string GetUserLogin()
        {
            return GetValue(UserLoginKey);
        }

        public static void SetUserLogin(string? login)
        {
            SetValue(UserLoginKey, login);
        }

        public static void ClearAuthorization()
        {
            SetValue(AccessTokenKey, null);
            SetValue(UserLoginKey, null);
        }

        private static string GetValue(string key)
        {
            var store = LoadStore();
            return store.TryGetValue(key, out var value) ? value ?? string.Empty : string.Empty;
        }

        private static void SetValue(string key, string? value)
        {
            var store = LoadStore();
            if (string.IsNullOrWhiteSpace(value))
            {
                store.Remove(key);
            }
            else
            {
                store[key] = value.Trim();
            }

            SaveStore(store);
        }

        private static Dictionary<string, string> LoadStore()
        {
            try
            {
                if (!File.Exists(StorePath))
                {
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                var encryptedBytes = File.ReadAllBytes(StorePath);
                if (encryptedBytes.Length == 0)
                {
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                var plainBytes = ProtectedData.Unprotect(encryptedBytes, AdditionalEntropy, DataProtectionScope.CurrentUser);
                try
                {
                    var json = Encoding.UTF8.GetString(plainBytes);
                    var result = JsonSerializer.Deserialize(json, AppJsonContext.Default.DictionaryStringString)
                        ?? new Dictionary<string, string>();
                    return new Dictionary<string, string>(result, StringComparer.OrdinalIgnoreCase);
                }
                finally
                {
                    Array.Clear(plainBytes, 0, plainBytes.Length);
                }
            }
            catch (Exception ex)
            {
                LogService.Log($"[GitHubSecretStoreService] Failed to load secure store: {ex.Message}");
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static void SaveStore(Dictionary<string, string> store)
        {
            try
            {
                var dir = Path.GetDirectoryName(StorePath)!;
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var json = JsonSerializer.Serialize(store, AppJsonContext.Default.DictionaryStringString);
                var plainBytes = Encoding.UTF8.GetBytes(json);
                try
                {
                    var encryptedBytes = ProtectedData.Protect(plainBytes, AdditionalEntropy, DataProtectionScope.CurrentUser);
                    var tempPath = StorePath + ".tmp";
                    File.WriteAllBytes(tempPath, encryptedBytes);
                    File.Move(tempPath, StorePath, true);
                }
                finally
                {
                    Array.Clear(plainBytes, 0, plainBytes.Length);
                }
            }
            catch (Exception ex)
            {
                LogService.Log($"[GitHubSecretStoreService] Failed to save secure store: {ex.Message}");
            }
        }
    }
}

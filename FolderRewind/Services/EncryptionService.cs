using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FolderRewind.Models;

namespace FolderRewind.Services
{
    /// <summary>
    /// 加密服务：使用 DPAPI (Windows Data Protection API) 安全存储和检索配置密码。
    /// 密码以加密形式存储在本地文件中，仅当前 Windows 用户可解密。
    /// </summary>
    public static class EncryptionService
    {
        private const string PasswordStoreFileName = "encrypted_passwords.dat";
        private static readonly string PasswordStorePath = Path.Combine(ConfigService.ConfigDirectory, PasswordStoreFileName);

        // 附加熵，增强 DPAPI 保护强度
        private static readonly byte[] AdditionalEntropy = Encoding.UTF8.GetBytes("FolderRewind.EncryptionService.v1");

        /// <summary>
        /// 加密密码并存储到本地文件，关联到指定配置 ID。
        /// </summary>
        public static void StorePassword(string configId, string password)
        {
            if (string.IsNullOrEmpty(configId)) throw new ArgumentNullException(nameof(configId));
            if (string.IsNullOrEmpty(password)) throw new ArgumentNullException(nameof(password));

            var store = LoadPasswordStore();
            byte[] plainBytes = Encoding.UTF8.GetBytes(password);
            byte[] encryptedBytes = ProtectedData.Protect(plainBytes, AdditionalEntropy, DataProtectionScope.CurrentUser);
            store[configId] = Convert.ToBase64String(encryptedBytes);
            SavePasswordStore(store);

            // 清除明文字节
            Array.Clear(plainBytes, 0, plainBytes.Length);
        }

        /// <summary>
        /// 从本地加密存储中检索指定配置的密码。
        /// </summary>
        /// <returns>解密后的密码，如果不存在或解密失败则返回 null。</returns>
        public static string RetrievePassword(string configId)
        {
            if (string.IsNullOrEmpty(configId)) return null;

            var store = LoadPasswordStore();
            if (!store.TryGetValue(configId, out var encryptedBase64) || string.IsNullOrEmpty(encryptedBase64))
                return null;

            try
            {
                byte[] encryptedBytes = Convert.FromBase64String(encryptedBase64);
                byte[] plainBytes = ProtectedData.Unprotect(encryptedBytes, AdditionalEntropy, DataProtectionScope.CurrentUser);
                string password = Encoding.UTF8.GetString(plainBytes);

                // 清除明文字节
                Array.Clear(plainBytes, 0, plainBytes.Length);
                return password;
            }
            catch (Exception ex)
            {
                LogService.Log($"[EncryptionService] Failed to decrypt password for config {configId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 检查指定配置是否已存储加密密码。
        /// </summary>
        public static bool HasStoredPassword(string configId)
        {
            if (string.IsNullOrEmpty(configId)) return false;
            var store = LoadPasswordStore();
            return store.ContainsKey(configId) && !string.IsNullOrEmpty(store[configId]);
        }

        /// <summary>
        /// 删除指定配置的已存储密码。
        /// </summary>
        public static void RemovePassword(string configId)
        {
            if (string.IsNullOrEmpty(configId)) return;
            var store = LoadPasswordStore();
            if (store.Remove(configId))
            {
                SavePasswordStore(store);
            }
        }

        /// <summary>
        /// 验证用户输入的密码是否与存储的密码匹配。
        /// </summary>
        public static bool VerifyPassword(string configId, string inputPassword)
        {
            if (string.IsNullOrEmpty(configId) || string.IsNullOrEmpty(inputPassword))
                return false;

            var storedPassword = RetrievePassword(configId);
            if (storedPassword == null) return false;

            // 使用恒定时间比较防止时序攻击
            bool result = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(storedPassword),
                Encoding.UTF8.GetBytes(inputPassword));

            return result;
        }

        /// <summary>
        /// 对日志文本进行密码脱敏处理。
        /// 将所有已知密码替换为 "***"。
        /// </summary>
        public static string SanitizeForLog(string text, string configId)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(configId))
                return text;

            var password = RetrievePassword(configId);
            if (string.IsNullOrEmpty(password))
                return text;

            return text.Replace(password, "***");
        }

        #region Private helpers

        private static System.Collections.Generic.Dictionary<string, string> LoadPasswordStore()
        {
            try
            {
                if (File.Exists(PasswordStorePath))
                {
                    string json = File.ReadAllText(PasswordStorePath);
                    return JsonSerializer.Deserialize(json, AppJsonContext.Default.DictionaryStringString)
                           ?? new System.Collections.Generic.Dictionary<string, string>();
                }
            }
            catch (Exception ex)
            {
                LogService.Log($"[EncryptionService] Failed to load password store: {ex.Message}");
            }
            return new System.Collections.Generic.Dictionary<string, string>();
        }

        private static void SavePasswordStore(System.Collections.Generic.Dictionary<string, string> store)
        {
            try
            {
                string dir = Path.GetDirectoryName(PasswordStorePath)!;
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string json = JsonSerializer.Serialize(store, AppJsonContext.Default.DictionaryStringString);

                // 原子写入
                string tempPath = PasswordStorePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, PasswordStorePath, overwrite: true);
            }
            catch (Exception ex)
            {
                LogService.Log($"[EncryptionService] Failed to save password store: {ex.Message}");
            }
        }

        #endregion
    }
}

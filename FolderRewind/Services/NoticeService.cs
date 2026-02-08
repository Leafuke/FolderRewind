using FolderRewind.Models;
using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    /// <summary>
    /// 公告服务：从 GitHub 远程拉取公告内容，支持多语言、变更检测、已读记忆。
    /// 参考 MineBackup 的 CheckForNoticesThread / ExtractLocalizedContent 实现，
    /// 使用 WinUI3 ContentDialog 替代 ImGui 弹窗。
    /// </summary>
    public static class NoticeService
    {
        // 公告文件 URL（按语言区分）
        private const string NoticeBaseUrl = "https://raw.githubusercontent.com/Leafuke/FolderRewind/dev/";
        private const string NoticeFileZh = "notice_zh";
        private const string NoticeFileEn = "notice_en";
        private const string NoticeFileFallback = "notice"; // 回退：无语言后缀

        // 检查结果
        private static bool _checkDone;
        private static bool _newNoticeAvailable;
        private static string _noticeContent = "";
        private static string _noticeVersion = ""; // Last-Modified 或内容 hash

        // 会话内暂缓标记（参考 MineBackup 的 notice_snoozed_this_session）
        private static bool _snoozedThisSession;

        public static bool CheckDone => _checkDone;
        public static bool NewNoticeAvailable => _newNoticeAvailable && !_snoozedThisSession;
        public static string NoticeContent => _noticeContent;

        /// <summary>
        /// 后台检查公告（启动时调用）
        /// </summary>
        public static async Task CheckForNoticesAsync()
        {
            _checkDone = false;
            _newNoticeAvailable = false;

            var settings = ConfigService.CurrentConfig?.GlobalSettings;
            if (settings == null || !settings.EnableNotices)
            {
                _checkDone = true;
                return;
            }

            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                client.DefaultRequestHeaders.Add("User-Agent", "FolderRewind-NoticeCheck");

                // 1. 根据当前语言选择 URL（参考 MineBackup 的多语言策略）
                string lang = settings.Language?.Replace("_", "-") ?? "zh-CN";
                bool isChinese = lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
                string primaryFile = isChinese ? NoticeFileZh : NoticeFileEn;

                string content = null;
                string version = null;

                // 尝试获取带语言后缀的文件
                (content, version) = await FetchNoticeAsync(client, NoticeBaseUrl + primaryFile);

                // 如果失败，回退到无后缀文件（参考 MineBackup 的 fallback 逻辑）
                if (content == null)
                {
                    (content, version) = await FetchNoticeAsync(client, NoticeBaseUrl + NoticeFileFallback);
                }

                if (string.IsNullOrWhiteSpace(content))
                {
                    _checkDone = true;
                    return;
                }

                // 2. 如果公告文件使用 --- 分隔中英文（参考 MineBackup 的 ExtractLocalizedContent），
                //    则根据语言提取对应段落
                content = ExtractLocalizedContent(content, isChinese);

                _noticeContent = content.Trim();
                _noticeVersion = version ?? ComputeHash(content);

                // 3. 比较是否有新公告
                string lastSeen = settings.NoticeLastSeenVersion ?? "";
                _newNoticeAvailable = !string.Equals(_noticeVersion, lastSeen, StringComparison.Ordinal);
            }
            catch (Exception ex)
            {
                LogService.Log(I18n.Format("Notice_FetchFailed", ex.Message), LogLevel.Warning);
            }
            finally
            {
                _checkDone = true;
            }
        }

        /// <summary>
        /// 从指定 URL 获取公告内容和版本标识
        /// </summary>
        private static async Task<(string Content, string Version)> FetchNoticeAsync(HttpClient client, string url)
        {
            try
            {
                var response = await client.GetAsync(url);
                if (!response.IsSuccessStatusCode) return (null, null);

                string content = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(content)) return (null, null);

                // 优先使用 Last-Modified 作为版本标识（参考 MineBackup）
                string version = null;
                if (response.Content.Headers.LastModified.HasValue)
                {
                    version = response.Content.Headers.LastModified.Value.ToString("O");
                }
                version ??= ComputeHash(content);

                return (content, version);
            }
            catch
            {
                return (null, null);
            }
        }

        /// <summary>
        /// 解析多语言内容。如果文本中包含 --- 分隔符，中文在前、英文在后。
        /// 参考 MineBackup 的 ExtractLocalizedContent() 实现。
        /// </summary>
        private static string ExtractLocalizedContent(string raw, bool isChinese)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;

            // 查找 --- 分隔符
            int separatorIndex = raw.IndexOf("\n---\n", StringComparison.Ordinal);
            if (separatorIndex < 0)
            {
                separatorIndex = raw.IndexOf("\r\n---\r\n", StringComparison.Ordinal);
            }

            if (separatorIndex < 0)
            {
                // 没有分隔符，返回全部内容
                return raw;
            }

            // 中文在前，英文在后
            if (isChinese)
            {
                return raw.Substring(0, separatorIndex);
            }

            int contentStart = raw.IndexOf('\n', separatorIndex + 1);
            if (contentStart < 0) return raw;
            return raw.Substring(contentStart + 1);
        }

        /// <summary>
        /// 计算内容 hash 作为版本标识
        /// </summary>
        private static string ComputeHash(string content)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
            return Convert.ToHexString(bytes).Substring(0, 16);
        }

        /// <summary>
        /// 标记当前公告为已读（"确认并不再提示"）
        /// </summary>
        public static void MarkAsRead()
        {
            var settings = ConfigService.CurrentConfig?.GlobalSettings;
            if (settings != null)
            {
                settings.NoticeLastSeenVersion = _noticeVersion;
                ConfigService.Save();
            }
            _newNoticeAvailable = false;
        }

        /// <summary>
        /// 本次会话暂缓提醒（"稍后提醒"）
        /// </summary>
        public static void SnoozeThisSession()
        {
            _snoozedThisSession = true;
        }
    }
}

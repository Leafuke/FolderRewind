using System.Collections.Generic;
using System.Threading.Tasks;

namespace FolderRewind.Services.Plugins
{
    public sealed class PluginKnotLinkCommandDefinition
    {
        /// <summary>
        /// 指令名（不区分大小写），例如："BACKUP_CURRENT"。
        /// </summary>
        public string Command { get; set; } = string.Empty;

        /// <summary>
        /// 可选：说明文本（用于文档或未来 UI 展示）。
        /// </summary>
        public string? Description { get; set; }
    }

    /// <summary>
    /// 可选接口：允许插件扩展 FolderRewind 的 KnotLink 指令集。
    /// 
    /// 触发场景：当 FolderRewind 作为 OpenSocket 响应器收到指令时，
    /// 若内置指令未命中，则会依次询问已启用插件是否愿意处理。
    /// </summary>
    public interface IFolderRewindKnotLinkCommandHandler
    {
        /// <summary>
        /// 返回该插件声明的 KnotLink 指令列表（可选）。
        /// </summary>
        IReadOnlyList<PluginKnotLinkCommandDefinition> GetKnotLinkCommandDefinitions();

        /// <summary>
        /// 尝试处理一条 KnotLink 指令。
        /// 返回 null 表示“不处理”；返回字符串表示“已处理并作为响应返回”。
        /// 
        /// 注意：这里运行在 Host 进程内，应避免长时间阻塞。
        /// 若指令会触发耗时操作（如备份），建议启动后台任务并尽快返回 OK。
        /// </summary>
        Task<string?> TryHandleKnotLinkCommandAsync(
            string command,
            string args,
            string rawCommand,
            IReadOnlyDictionary<string, string> settingsValues,
            PluginHostContext hostContext);
    }
}

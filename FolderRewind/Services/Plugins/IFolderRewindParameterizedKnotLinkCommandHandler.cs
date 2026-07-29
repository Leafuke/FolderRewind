using System.Collections.Generic;
using System.Threading.Tasks;
using FolderRewind.Services.KnotLink;

namespace FolderRewind.Services.Plugins
{
    public sealed class PluginParameterizedKnotLinkCommandResult
    {
        /// <summary>
        /// 插件是否已经处理该指令。
        /// </summary>
        public bool Handled { get; set; }

        /// <summary>
        /// 返回给 KnotLink 调用方的响应文本。Host 会将 OK:/ERROR: 插件结果
        /// 转换为严格 v2 status=ok/status=error 响应；为空时补成功响应。
        /// </summary>
        public string? Response { get; set; }

        public static PluginParameterizedKnotLinkCommandResult NotHandled { get; } = new()
        {
            Handled = false,
            Response = null
        };
    }

    /// <summary>
    /// 可选接口：允许插件参与严格键值对 KnotLink v2 指令。
    ///
    /// </summary>
    public interface IFolderRewindParameterizedKnotLinkCommandHandler
    {
        Task<PluginParameterizedKnotLinkCommandResult?> TryHandleParameterizedKnotLinkCommandAsync(
            KnotLinkCommandRequest request,
            IReadOnlyDictionary<string, string> settingsValues,
            PluginHostContext hostContext);
    }
}

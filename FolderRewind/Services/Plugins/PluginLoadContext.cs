using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace FolderRewind.Services.Plugins
{
    /// <summary>
    /// 插件加载上下文：允许插件从自己的目录加载依赖。
    /// 
    /// 说明：
    /// - WinUI3 桌面端通常无需强隔离（卸载/热替换很难完全做到），
    ///   但单独 ALC 可以避免依赖冲突，并让插件依赖优先从插件目录解析。
    /// </summary>
    internal sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly string _pluginDir;

        /// <summary>
        /// 创建一个可卸载的插件加载上下文
        /// isCollectible: true 允许通过 Unload() 方法卸载程序集
        /// </summary>
        public PluginLoadContext(string pluginDir) : base(isCollectible: true)
        {
            _pluginDir = pluginDir;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            try
            {
                // 优先从插件目录解析依赖
                var candidate = Path.Combine(_pluginDir, assemblyName.Name + ".dll");
                if (File.Exists(candidate))
                {
                    return LoadFromAssemblyPath(candidate);
                }
            }
            catch
            {
                // 交给默认加载流程
            }

            return null;
        }
    }
}

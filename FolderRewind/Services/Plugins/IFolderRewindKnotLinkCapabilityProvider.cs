using FolderRewind.Services.KnotLink;
using System.Collections.Generic;

namespace FolderRewind.Services.Plugins
{
    /// <summary>Optional plugin API for contributing discoverable KnotLink v2 capabilities.</summary>
    public interface IFolderRewindKnotLinkCapabilityProvider
    {
        PluginKnotLinkCapabilityContribution GetKnotLinkCapabilities();
    }

    public sealed class PluginKnotLinkCapabilityContribution
    {
        public IReadOnlyList<PluginKnotLinkOpenSocketCapability> OpenSocket { get; set; } =
            new List<PluginKnotLinkOpenSocketCapability>();

        public IReadOnlyList<PluginKnotLinkSignalCapability> Signal { get; set; } =
            new List<PluginKnotLinkSignalCapability>();
    }

    public sealed class PluginKnotLinkOpenSocketCapability
    {
        public string Name { get; set; } = string.Empty;

        public string? Description { get; set; }

        public IReadOnlyDictionary<string, KnotLinkFuncArgument> Args { get; set; } =
            new Dictionary<string, KnotLinkFuncArgument>();

        public IReadOnlyList<string[]> Returns { get; set; } = new List<string[]>();
    }

    public sealed class PluginKnotLinkSignalCapability
    {
        public string Name { get; set; } = string.Empty;

        public string? Description { get; set; }

        public IReadOnlyDictionary<string, KnotLinkSignalField> Returns { get; set; } =
            new Dictionary<string, KnotLinkSignalField>();
    }
}

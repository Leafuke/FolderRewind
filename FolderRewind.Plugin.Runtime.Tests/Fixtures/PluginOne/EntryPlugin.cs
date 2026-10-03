using Fixture.PrivateDependency;
using Fixture.PluginBase;

namespace Fixture.PluginOne;

public sealed class EntryPlugin : EntryPluginBase
{
    public EntryPlugin()
    {
        var marker = Environment.GetEnvironmentVariable("FOLDERREWIND_PLUGIN_TEST_MARKER");
        if (!string.IsNullOrWhiteSpace(marker)) File.WriteAllText(marker, "constructed");
    }

    public string DependencyVersion => DependencyMarker.Value;

}

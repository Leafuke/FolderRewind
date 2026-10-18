namespace FolderRewind.Plugin.Runtime.Artifacts;

/// <summary>Short, private scratch storage; live operation locks survive concurrent sweeps.</summary>
public sealed class OwnedArtifactTemporaryWorkspace : IAsyncDisposable
{
    private OwnedPluginTemporaryStorage? _owner;
    private OwnedArtifactTemporaryWorkspace(OwnedPluginTemporaryStorage owner, string root) { _owner = owner; Root = root; }
    public string Root { get; }
    public static async Task<OwnedArtifactTemporaryWorkspace> CreateAsync(string logicalRoot, CancellationToken token)
    {
        var owner = await OwnedPluginTemporaryStorage.CreateAsync(OwnedPluginTemporaryStorage.ForLogicalRoot(logicalRoot), token);
        try { return new(owner, await owner.CreateDirectoryAsync(token)); }
        catch { await owner.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        if (owner is null) return;
        try { HostArtifactWorkspace.DeleteOwnedDirectory(Root); }
        finally { await owner.DisposeAsync(); }
    }
}

using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Tests;

public sealed partial class PluginRuntimeManagerTests
{
    [TestMethod]
    public async Task RunCoordinationRequiresOwnedDeclaredKindAndApi318()
    {
        var kind = new ConfigKindRef(new(PluginOneId.Value), "world");
        var label = new LocalizedText("World", new Dictionary<string, string>());
        var manifest = new PluginManifestContract(PluginOneId, "1.0.0", new(3, 18), "fixture.dll", "Fixture", label, label,
            [new(kind, label, label, "", BackupFallbackPolicy.RawWithWarnings, RestoreCoordinationPolicy.Required)],
            "settings.json", [], [PluginCapabilityKind.BackupRunCoordination], [], [], [], false);
        foreach (var (api, registeredKind, success) in new[]
        {
            (new PluginApiVersion(3, 18), kind, true),
            (new PluginApiVersion(3, 17), kind, false),
            (new PluginApiVersion(3, 18), new ConfigKindRef(new(PluginTwoId.Value), "world"), false),
            (new PluginApiVersion(3, 18), new ConfigKindRef(new(PluginOneId.Value), "undeclared"), false)
        })
        {
            var manager = new FolderRewind.Plugin.Runtime.Activation.PluginRuntimeManager();
            var plugin = new FakePlugin(context =>
            {
                context.RegisterCapability<IBackupRunCoordinationCapability>(new RunProvider(registeredKind));
                return PluginActivationResult.Empty;
            });
            var result = await manager.ActivateAsync(Candidate(PluginOneId, plugin, new FakeStore()) with
                { Manifest = manifest with { RequiredApi = api } });
            Assert.AreEqual(success, result.Success);
            using var lease = manager.TryAcquire<IBackupRunCoordinationCapability>(PluginOneId);
            Assert.AreEqual(success, lease is not null);
            if (success) { lease!.Dispose(); await manager.DeactivateAsync(PluginOneId); }
        }
    }

    private sealed class RunProvider(ConfigKindRef kind) : IBackupRunCoordinationCapability
    {
        public ConfigKindRef Kind => kind;
        public ValueTask<IBackupRunCoordinationSession> CreateSessionAsync(BackupRunCoordinationRequest request, PluginInvocationContext context) => throw new NotSupportedException();
        public ValueTask<BackupCoordinationStatus> GetStatusAsync(PluginInvocationContext context) => throw new NotSupportedException();
        public ValueTask<PluginCommandResult> ResetAsync(PluginInvocationContext context) => throw new NotSupportedException();
    }
}

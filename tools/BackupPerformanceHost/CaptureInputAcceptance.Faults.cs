using System.Reflection;
using System.Text.Json;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;
using FolderRewind.Plugin.Runtime.Artifacts;
using FolderRewind.Services;
using FolderRewind.Services.Plugins.V3;
using static FolderRewind.PerformanceHost.Program;

namespace FolderRewind.PerformanceHost;

internal static partial class CaptureInputAcceptance
{
    private static async Task VerifyNativeProofAsync(BackupConfig config, HistoryRuntime history, VersionId version)
    {
        var factory = typeof(BackupService).Assembly.GetType("FolderRewind.Services.HistoryRepresentationRuntimeFactory", true)!;
        var create = factory.GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Single(m => m.Name == "Create" && m.GetParameters().Length == 3);
        var engine = (RepresentationRuntime)create.Invoke(null, [config, history, null])!;
        var graph = await history.Query.GetAllRepresentationsAsync();
        var environment = new RepresentationEnvironment((await history.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries, [], []);
        using var diagnostics = HistoryOperationDiagnostics.Begin("protected-native-acceptance");
        using (var context = new ArchiveRestoreVerificationContext(RestoreArchivePreflightMode.Balanced))
        {
            for (int i = 0; i < 2; i++)
                Require((await engine.AssessVersionAsync(version, graph, environment, AssessmentDepth.Deep, MaterializationFidelity.Exact,
                    default, context)).Readiness == HistoryReadiness.Ready, "Protected Native assessment failed.");
        }
        diagnostics.Dispose();
        Require(diagnostics.Summary!.Counters.GetValueOrDefault("native-protected-verifications") == 1
            && diagnostics.Summary.Counters.GetValueOrDefault("native-verification-reuses") == 1, "Native input was deeply verified twice.");
        Results.Add(new { phase = "actual-native-verification-reuse", correct = true, diagnostic = diagnostics.Summary });
    }

    private static async Task VerifyFaultsAsync()
    {
        var plugin = new FaultPlugin();
        var id = FaultPlugin.Id;
        var format = new ArtifactFormatRef(new(id.Value), "fault-fixture");
        var strategy = new RestoreStrategyId(id, "restore");
        var kind = new ConfigKindRef(new("folderrewind.core"), "default");
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { representation = new { type = "string", @enum = new[] { "incremental-v1" } } } });
        var manifest = new PluginManifestContract(id, "1.0.0", PluginApiVersion.HostVersion, "fixture.dll", "Fixture",
            new("fault fixture", new Dictionary<string, string>()), new("fault fixture", new Dictionary<string, string>()), [], "unused.json",
            [HostServiceKind.ArtifactRead, HostServiceKind.ArtifactTransformStaging, HostServiceKind.RestoreMaterializationWorkspace],
            [PluginCapabilityKind.BackupArtifactTransformer, PluginCapabilityKind.RestoreMaterializer],
            [new(format, 1, 1, new("fixture", new Dictionary<string, string>()))],
            [new(new(id, "capture"), [kind], [CoreCaptureMode.Full, CoreCaptureMode.Smart], [ArtifactCompleteness.Complete, ArtifactCompleteness.Partial],
                schema, [ArtifactTransformFailureBehavior.KeepPrimaryWithWarnings, ArtifactTransformFailureBehavior.RequireTransform]) { SupportsIncrementalInput = true }],
            [new(strategy, [new(format, 1, 1)], [ArtifactCompleteness.Complete], [FolderRewind.Plugin.Abstractions.RestoreMode.Clean])], false);
        var activation = await PluginV3RuntimeService.ActivateAsync(new(id, () => plugin, new(id, new Dictionary<string, JsonElement>()), [],
            new UnusedHost(), new ActivationStore(), manifest));
        Require(activation.Success, "Fault provider activation failed: " + JsonSerializer.Serialize(activation.Diagnostics));
        try
        {
            foreach (var copy in new[] { false, true })
            {
                var source = Path.Combine(Root, "fault-" + copy, "source"); Directory.CreateDirectory(source);
                File.WriteAllText(Path.Combine(source, "changed"), "before");
                File.WriteAllText(Path.Combine(source, "unchanged"), "must survive sparse fallback");
                var config = new BackupConfig { Name = "fault fixture", DestinationPath = Path.Combine(Root, "fault-" + copy, "backups") };
                var folder = new ManagedFolder { Path = source, DisplayName = "fault fixture" }; config.SourceFolders.Add(folder);
                config.Archive.Mode = BackupMode.Smart; config.Archive.CopySourceBeforeArchive = copy;
                config.ArtifactTransformPolicy = new() { ExperimentalEnabled = true, Transformer = new() { PluginId = id.Value, TransformerId = "capture" },
                    FailureBehavior = PersistedArtifactTransformFailureBehavior.KeepPrimaryWithWarnings };
                config.ArtifactTransformPolicy.Parameters["representation"] = JsonSerializer.SerializeToElement("incremental-v1");
                ConfigService.CurrentConfig.BackupConfigs.Add(config); Require((await ConfigService.SaveAsync()).Success, "Config save failed.");
                plugin.Error = null; plugin.Outcome = null;
                var initial = await BackupService.BackupFolderAsync(config, folder, BackupInvocationOptions.ForInternal());
                await LogService.FlushAsync();
                Require(initial.CreatedNewArchive, "Fault fixture baseline failed: " + JsonSerializer.Serialize(initial)
                    + "; " + string.Join("; ", BackupService.ActiveTasks.Select(t => t.ErrorMessage)));
                var history = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
                File.AppendAllText(Path.Combine(source, "changed"), " after");
                plugin.Error = new InvalidDataException("Synthetic domain conversion failure.");
                var result = await BackupService.BackupFolderAsync(config, folder, BackupInvocationOptions.ForInternal());
                Require(result.IsSuccessful && result.CreatedNewArchive && result.HasWarnings, "Independent Full fallback failed.");
                Require(plugin.LastInput.SequenceEqual(new[] { "changed" }), "Failure was not injected after a sparse Native input.");
                var diagnostic = HistoryOperationDiagnostics.GetRecent().Last(d => d.Operation == "backup");
                Require(diagnostic.Counters.GetValueOrDefault("native-independent-full-fallbacks") == 1, "Fallback was not exactly once.");
                var version = (await history.WorkspaceStore.LoadAsync()).Value!.GetSourceState(new(Guid.Parse(folder.Id))).BaseVersionId!.Value;
                Require((await history.Query.GetRepresentationsAsync(version)).Single().Kind == RepresentationKind.CoreFull, "Fallback was not self-contained Full.");
                var method = typeof(BackupService).Assembly.GetType("FolderRewind.Services.NativeHistoryApplicationService", true)!
                    .GetMethod("RestoreVersionAsync", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
                File.Delete(Path.Combine(source, "unchanged"));
                var restored = await (Task<HistoryRestoreResult>)method.Invoke(null,
                    [config, folder, version, BackupService.RestoreMode.Clean, CancellationToken.None, null, null])!;
                Require(restored.Succeeded && File.ReadAllText(Path.Combine(source, "unchanged")) == "must survive sparse fallback",
                    "Independent Full omitted unchanged input.");
                await (Task)typeof(HistoryRuntime).GetMethod("DrainRestoreCleanupAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(history, [CancellationToken.None])!;
                Results.Add(new { phase = "sparse-native-independent-full", copy, correct = true, diagnostic });
                var storeFactory = typeof(BackupService).Assembly.GetType("FolderRewind.Services.Plugins.V3.NativeArtifactHostService", true)!
                    .GetMethod("Store", BindingFlags.NonPublic | BindingFlags.Static)!;
                var store = (NativeArtifactStore)storeFactory.Invoke(null, [config, null])!;
                var oldNodes = (await store.Ledger.LoadAsync(default)).Artifacts.Select(a => a.ArtifactId).ToHashSet();
                plugin.Error = null; plugin.Corrupt = true;
                File.AppendAllText(Path.Combine(source, "changed"), "candidate");
                var candidateFailure = await BackupService.BackupFolderAsync(config, folder, BackupInvocationOptions.ForInternal());
                plugin.Corrupt = false;
                Require(candidateFailure.CreatedNewArchive && candidateFailure.HasWarnings
                    && HistoryOperationDiagnostics.GetRecent().Last(d => d.Operation == "backup").Counters.GetValueOrDefault("native-independent-full-fallbacks") == 1,
                    "Candidate validation failure did not fall back independently.");
                Require(oldNodes.SetEquals((await store.Ledger.LoadAsync(default)).Artifacts.Select(a => a.ArtifactId))
                    && store.Pending().Count == 0, "Rejected Native candidate was left in the ledger.");
                Require(Directory.EnumerateDirectories(Path.Combine(store.Root, "artifacts")).Count() == oldNodes.Count,
                    "Rejected Native candidate payload was left behind.");
                Results.Add(new { phase = "candidate-validation-cleanup", copy, correct = true });
                foreach (var error in new Exception[] { new IOException("Synthetic space failure."), new OperationCanceledException("Synthetic cancellation."),
                    new InvalidDataException("Wrapped resource failure.", new IOException("Disk full.")) })
                {
                    var before = (await history.WorkspaceStore.LoadAsync()).Value!.StateRevision;
                    plugin.Error = error; File.AppendAllText(Path.Combine(source, "changed"), "!");
                    var blocked = await BackupService.BackupFolderAsync(config, folder, BackupInvocationOptions.ForInternal());
                    var stats = HistoryOperationDiagnostics.GetRecent().Last(d => d.Operation == "backup");
                    Require(!blocked.IsSuccessful && stats.Counters.GetValueOrDefault("native-independent-full-fallbacks") == 0, "Resource failure fell back.");
                    Require((await history.WorkspaceStore.LoadAsync()).Value!.StateRevision == before, "Failed capture changed Workspace.");
                    Results.Add(new { phase = "no-fallback-" + error.GetType().Name, copy, correct = true });
                }
                foreach (var outcome in new[] { OperationOutcome.Canceled, OperationOutcome.Blocked })
                {
                    var before = (await history.WorkspaceStore.LoadAsync()).Value!.StateRevision;
                    plugin.Error = null; plugin.Outcome = outcome; File.AppendAllText(Path.Combine(source, "changed"), "!");
                    var blocked = await BackupService.BackupFolderAsync(config, folder, BackupInvocationOptions.ForInternal());
                    var stats = HistoryOperationDiagnostics.GetRecent().Last(d => d.Operation == "backup");
                    Require(!blocked.IsSuccessful && stats.Counters.GetValueOrDefault("native-independent-full-fallbacks") == 0
                        && (await history.WorkspaceStore.LoadAsync()).Value!.StateRevision == before, "Non-success outcome incorrectly fell back.");
                    Results.Add(new { phase = "no-fallback-outcome-" + outcome, copy, correct = true });
                }
            }
        }
        finally { await PluginV3RuntimeService.DeactivateAsync(id); }
    }

    private sealed class FaultPlugin : IFolderRewindPlugin, IBackupArtifactTransformerCapability, IRestoreMaterializerCapability, IArtifactFileViewProvider
    {
        internal static readonly PluginId Id = new("test.capture-input-fault");
        internal Exception? Error;
        internal OperationOutcome? Outcome;
        internal bool Corrupt;
        internal string[] LastInput = [];
        public ArtifactTransformerId TransformerId => new(Id, "capture");
        public RestoreStrategyId RestoreStrategyId => new(Id, "restore");
        public ValueTask<PluginActivationResult> ActivateAsync(IPluginActivationContext context, CancellationToken token)
        { context.RegisterCapability<IBackupArtifactTransformerCapability>(this); return ValueTask.FromResult(PluginActivationResult.Empty); }
        public ValueTask DeactivateAsync(CancellationToken token) => ValueTask.CompletedTask;
        public async ValueTask<ArtifactTransformResult> TransformAsync(ArtifactTransformRequest request, PluginInvocationContext context)
        {
            var files = await request.ArtifactRead.ListFilesAsync(request.Primary.Content, context.OperationCancellation);
            LastInput = files.Select(f => f.RelativePath).Order(StringComparer.Ordinal).ToArray();
            if (Error is { } error) throw error;
            if (Outcome is { } outcome) return new(outcome, null, []);
            var root = await request.Staging.CreateArtifactAsync(context.OperationCancellation);
            foreach (var file in files)
            {
                await using var source = await request.ArtifactRead.OpenReadAsync(request.Primary.Content, file.RelativePath, context.OperationCancellation);
                await using var destination = await request.Staging.OpenWriteAsync(root.Staging, file.RelativePath, context.OperationCancellation);
                if (Corrupt) await destination.WriteAsync("corrupt"u8.ToArray(), context.OperationCancellation);
                else await source.CopyToAsync(destination, context.OperationCancellation);
            }
            return new(OperationOutcome.Success, new(request.ExpectedGraphRevision,
                [new(root.ArtifactId, root.Staging, new(new(Id.Value), "fault-fixture"), 1, RestoreStrategyId,
                    ArtifactCompleteness.Complete, request.Primary.CoreCaptureMode, [])], root.ArtifactId), []);
        }
        public ValueTask<RestoreMaterializationResult> MaterializeAsync(RestoreMaterializationRequest request, PluginInvocationContext context)
            => throw new NotSupportedException("Fixture uses its file-view provider.");
        public async ValueTask<IArtifactFileView> OpenFileViewAsync(ArtifactFileViewRequest request, PluginInvocationContext context)
        {
            var root = request.ArtifactsTopologicallySorted.Single(a => a.ArtifactId == request.RootArtifactId);
            var files = (await request.ArtifactRead.ListFilesAsync(root.Content, context.OperationCancellation)).Where(f => !f.RelativePath.StartsWith("_host/", StringComparison.Ordinal))
                .Select(f => new ArtifactMaterializedFile(f.RelativePath, f.Length, f.Sha256, new(root.ArtifactId, f.RelativePath, 0, f.Length, f.Sha256, "fault-fixture", 1))).ToArray();
            return new View(files, request.ArtifactRead, root.Content);
        }
        private sealed class View(IReadOnlyList<ArtifactMaterializedFile> files, IArtifactReadService read, ArtifactContentHandle content) : IArtifactFileView
        {
            public IReadOnlyList<ArtifactMaterializedFile> Files => files;
            public ValueTask<Stream> OpenReadAsync(string path, CancellationToken token) => read.OpenReadAsync(content, path, token);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private sealed class ActivationStore : IPluginActivationStore
    { public ValueTask CommitAsync(PluginActivationCommit commit, CancellationToken token) => ValueTask.CompletedTask; }
    private sealed class UnusedHost : IPluginHostServices
    {
        public IReadOnlyConfigQueryService Configs => null!; public IBackupRequestService Backups => null!; public IRestoreRequestService Restores => null!;
        public IHistoryQueryService History => null!; public IPluginNotificationService Notifications => null!; public IKnotLinkHostService KnotLink => null!;
        public IPluginDataStore DataStore => null!; public IPluginTemporaryStorage TemporaryStorage => null!; public IPluginLogger Logger => null!;
    }
}

using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;
using FolderRewind.Plugin.Runtime.Loading;
using FolderRewind.Plugin.Runtime.Packaging;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SemanticMergeIntegrationTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task ExactBundledMineRewindPackageMergesCoreHistoryWithoutArtifactTransformEnablement(bool smart, bool blocked)
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !Directory.Exists(Path.Combine(repo.FullName, "FolderRewind", "Assets", "Plugins"))) repo = repo.Parent;
        Assert.IsNotNull(repo);
        using var preset = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo.FullName, "FolderRewind", "Assets", "PluginPresets", "minecraft-enhanced-experience.v1.json")));
        var relative = preset.RootElement.GetProperty("actions").EnumerateArray().Single(a => a.GetProperty("id").GetString() == "install-minerewind").GetProperty("packagePath").GetString()!;
        var package = Path.Combine(repo.FullName, "FolderRewind", relative);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package)));
        Assert.AreEqual(hash, File.ReadAllText(package + ".sha256").Trim().Split(' ')[0].ToUpperInvariant());
        var extraction = Path.Combine(AppContext.BaseDirectory, "merge-package-fixtures", Guid.NewGuid().ToString("N"));
        ZipFile.ExtractToDirectory(package, extraction);
        Assert.IsFalse(File.Exists(Path.Combine(extraction, "FolderRewind.Plugin.Abstractions.dll")));
        var manifest = PluginPackageManifestReader.Parse(File.ReadAllBytes(Path.Combine(extraction, "manifest.json"))).Contract;
        using var loaded = PluginAssemblyLoader.Load(new(manifest.PluginId, extraction, manifest.EntryAssembly, manifest.EntryType, manifest.RequiredApi));
        var resolver = await Resolver.Create(loaded.Instance, manifest, hash, smart ? "terrain-first" : null);
        await using var fixture = new HistoryMergeArchiveFixture { UseSmartDeltas = smart,
            Boundary = new(EffectiveBoundaryScopeMode.All, [], EffectiveBoundaryFilterMode.Blacklist,
                ["**/session.lock", "voxy/**", "**/distanthorizons.sqlite", "**/distanthorizons.sqlite-shm", "**/distanthorizons.sqlite-wal", "*.log", .. blocked ? new[] { "session.lock" } : Array.Empty<string>()], false) };
        SemanticMergeEngine Engine(HistoryRuntime history) => new(history.MergeSessions, resolver.Binding, resolver);
        await fixture.SeedAsync((root, side) =>
        {
            Directory.CreateDirectory(Path.Combine(root, "region"));
            File.WriteAllBytes(Path.Combine(root, "level.dat"), [1]);
            var bytes = Region(side == 1 ? 2 : 1, side == 2 ? 3 : 1, smart);
            if (side != 2) bytes = bytes[..(12288 + 4 + BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(12288)))];
            File.WriteAllBytes(Path.Combine(root, "region", "r.0.0.mca"), bytes);
            if (smart)
            {
                Directory.CreateDirectory(Path.Combine(root, "entities"));
                File.WriteAllBytes(Path.Combine(root, "entities", "r.0.0.mca"), Region(10 * (side + 1), 10 * (side + 1)));
                Directory.CreateDirectory(Path.Combine(root, "poi"));
                File.WriteAllBytes(Path.Combine(root, "poi", "r.0.0.mca"), Region(100 * (side + 1), side == 2 ? 300 : 100));
            }
        }, Engine, blocked ? MergeSessionState.Resolving : MergeSessionState.Ready);
        Assert.AreEqual(smart ? RepresentationKind.CoreSmartDelta : RepresentationKind.CoreFull, fixture.Ours.NewRepresentations.Single().Kind);
        await fixture.ReopenAsync();
        var restore = fixture.Restore(); var engine = Engine(fixture.History);
        var builder = new HistoryMergeCommitBuilder(fixture.History, restore, fixture.Archives, fixture.Archives, semantic: engine);
        if (blocked)
        {
            var row = fixture.History.MergeSessions.QuerySemanticChanges(fixture.Session, "", "Unresolved", 0, 100).Rows.Single(r => r.Conflict.SemanticSubject?.IsBlocked == true);
            StringAssert.Contains(row.Conflict.SemanticSubject!.Description.Default, "session.lock");
            Assert.ThrowsExactly<InvalidOperationException>(() => fixture.History.MergeSessions.Resolve(fixture.Session,
                new(fixture.Session.Plan.Revision, row.Conflict.Id, row.Conflict.InputSignature, MergeResolutionChoice.Ours)));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => builder.BuildAsync(fixture.Session));
            Assert.AreEqual(fixture.OursDigest, (await MergeTreeManifest.ReadAsync(fixture.Target, _ => true, default)).Digest);
            Assert.IsTrue((await resolver.Runtime.DeactivateAsync(manifest.PluginId)).Success);
            return;
        }
        Assert.AreEqual(2, fixture.History.MergeSessions.QuerySemanticChanges(fixture.Session, "", "Automatic", 0, 100).Rows.Count(r => r.Conflict.SemanticSubject is not null));
        var prepared = await builder.BuildAsync(fixture.Session);
        Assert.IsEmpty(prepared.Sources.Single().RequiredAbsentPaths, "Core file inventories do not establish complete directory absence.");
        File.WriteAllText(Path.Combine(fixture.Target, "debug.log"), "keep excluded live content");
        CollectionAssert.AreEqual(Region(2, 3, smart), File.ReadAllBytes(Path.Combine(prepared.Sources.Single().StagingDirectory, "region", "r.0.0.mca")));
        if (smart)
        {
            CollectionAssert.AreEqual(Region(20, 20), File.ReadAllBytes(Path.Combine(prepared.Sources.Single().StagingDirectory, "entities", "r.0.0.mca")));
            CollectionAssert.AreEqual(Region(200, 300), File.ReadAllBytes(Path.Combine(prepared.Sources.Single().StagingDirectory, "poi", "r.0.0.mca")));
            var warning = prepared.Sources.Single().SemanticDiagnostics.Single();
            Assert.AreEqual("1", warning.Arguments["count"]);
            StringAssert.Contains(warning.Arguments["message.zh-CN"], "固定为目标分支");
        }
        var result = await new HistoryMergeApplyService(fixture.History, restore, builder, provider: engine.Descriptor).ApplyAsync(fixture.Session, ready: prepared);
        Assert.IsTrue(result.Succeeded, result.Diagnostic);
        Assert.AreEqual("keep excluded live content", File.ReadAllText(Path.Combine(fixture.Target, "debug.log")));
        CollectionAssert.AreEqual(Region(2, 3, smart), File.ReadAllBytes(Path.Combine(fixture.Target, "region", "r.0.0.mca")));
        if (smart) CollectionAssert.AreEqual(Region(20, 20), File.ReadAllBytes(Path.Combine(fixture.Target, "entities", "r.0.0.mca")));
        Assert.IsTrue((await resolver.Runtime.DeactivateAsync(manifest.PluginId)).Success);
        // Extracted assemblies live under this test build's bin tree, separate from user plugin state.
    }

    private static byte[] Region(int left, int right, bool embeddedFirst = false)
    {
        var bytes = new byte[16384];
        for (var slot = 0; slot < 2; slot++)
        {
            using var nbt = new MemoryStream(); nbt.Write([10, 0, 0]);
            foreach (var (name, value) in new[] { ("DataVersion", 3700), ("xPos", slot), ("zPos", 0), ("value", slot == 0 ? left : right) })
            {
                nbt.WriteByte(3); nbt.WriteByte(0); nbt.WriteByte((byte)name.Length); nbt.Write(Encoding.UTF8.GetBytes(name));
                var number = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(number, value); nbt.Write(number);
            }
            // Modern generation-stage terrain may retain an empty root entity list.
            nbt.Write([9, 0, 8]); nbt.Write(Encoding.UTF8.GetBytes("entities"));
            if (embeddedFirst && slot == 0) nbt.Write([10, 0, 0, 0, 1, 0]); else nbt.Write([0, 0, 0, 0, 0]);
            nbt.WriteByte(0); var payload = nbt.ToArray(); var offset = (slot + 2) * 4096;
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(slot * 4), (slot + 2) << 8 | 1);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4096 + slot * 4), 100 + slot);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(offset), payload.Length + 1); bytes[offset + 4] = 3; payload.CopyTo(bytes, offset + 5);
        }
        return bytes;
    }
    [TestMethod]
    public async Task IndependentTextCapabilityPersistsDecisionsReopensBuildsCoreFullAndCommitsThroughHost()
    {
        await using var fixture = new HistoryMergeArchiveFixture(); await fixture.SeedAsync();
        var provider = new TextPlugin(); var resolver = await Resolver.Create(provider);
        SemanticMergeEngine Engine() => new(fixture.History.MergeSessions, resolver.Binding, resolver);
        HistoryMergeService Service() => new(fixture.History, fixture.Restore(), semantic: Engine());
        var session = (await Service().StartAsync(fixture.Theirs.NewBranchUpdates.Single().BranchId, "fixture-v1", [new(fixture.Source, fixture.Target)]))!;
        Assert.AreEqual(MergeSessionState.Resolving, session.State);
        Assert.ThrowsExactly<ObjectDisposedException>(() => provider.LastRead!.ReadByte());
        Assert.HasCount(2, fixture.History.MergeSessions.QuerySemanticChanges(session, "Text", "Unresolved", 0, 100).Rows);
        var rows = Service().AllConflicts(session).ToArray();
        var a = rows.Single(r => r.Conflict.SemanticSubject!.SubjectId == "a.txt").Conflict;
        var b = rows.Single(r => r.Conflict.SemanticSubject!.SubjectId == "b.txt").Conflict;
        // Exercise all non-manual choices without rebuilding archives for each decision.
        session = fixture.History.MergeSessions.ResolveBatch(session,
            [new(session.Plan.Revision, a.Id, a.InputSignature, MergeResolutionChoice.Base), new(session.Plan.Revision, b.Id, b.InputSignature, MergeResolutionChoice.Delete)]);
        session = await Service().RecomputeAsync(session, "fixture-v1", [new(fixture.Source, fixture.Target)]);
        Assert.AreEqual(MergeSessionState.Ready, session.State);
        Assert.IsTrue(Service().AllConflicts(session).Any(r => r.Resolution?.Choice == MergeResolutionChoice.Base));
        var manual = Path.Combine(fixture.Root, "manual.txt");
        var manualBytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("manual-text\r\n")).ToArray();
        await File.WriteAllBytesAsync(manual, manualBytes);
        a = Service().AllConflicts(session).Single(r => r.Conflict.SemanticSubject!.SubjectId == "a.txt").Conflict;
        b = Service().AllConflicts(session).Single(r => r.Conflict.SemanticSubject!.SubjectId == "b.txt").Conflict;
        session = await Service().ImportManualAsync(session, a, manual);
        session = fixture.History.MergeSessions.Resolve(session, new(session.Plan.Revision, b.Id, b.InputSignature, MergeResolutionChoice.Theirs));
        await fixture.ReopenAsync(); session = fixture.History.MergeSessions.Load(session.Id);
        var engine = Engine(); var restore = fixture.Restore();
        var builder = new HistoryMergeCommitBuilder(fixture.History, restore, fixture.Archives, fixture.Archives, semantic: engine);
        var prepared = await builder.BuildAsync(session);
        CollectionAssert.AreEqual(manualBytes.Concat(Encoding.UTF8.GetBytes("|theirs-b")).ToArray(), File.ReadAllBytes(Path.Combine(prepared.Sources.Single().StagingDirectory, "document.txt")));
        Assert.AreEqual("text.fixture_note", prepared.Sources.Single().SemanticDiagnostics.Single().Code);
        Assert.AreEqual(RepresentationKind.CoreFull, prepared.Facts.OfType<VersionRepresentation>().Single().Kind);
        Assert.HasCount(2, prepared.Sources.Single().Version.ParentVersionIds);
        resolver.Available = false;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => builder.BuildAsync(session));
        resolver.Available = true;
        var applied = await new HistoryMergeApplyService(fixture.History, restore, builder, provider: engine.Descriptor).ApplyAsync(session, ready: prepared);
        Assert.IsTrue(applied.Succeeded, applied.Diagnostic);
        CollectionAssert.AreEqual(manualBytes.Concat(Encoding.UTF8.GetBytes("|theirs-b")).ToArray(), File.ReadAllBytes(Path.Combine(fixture.Target, "document.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Target, "a.txt")));
        Assert.AreEqual("untouched", File.ReadAllText(Path.Combine(fixture.OtherTarget, "keep.txt")));
        Assert.HasCount(1, (await fixture.History.Query.GetAllVersionsAsync()).Where(v => v.CreationKind == SourceVersionCreationKind.Merge));
        Assert.AreEqual(0, resolver.Runtime.GetSnapshot(resolver.Binding.ProviderId.PluginId).ActiveLeases);
        Assert.IsTrue((await resolver.Runtime.DeactivateAsync(resolver.Binding.ProviderId.PluginId)).Success);
    }

    [TestMethod]
    public async Task OutputReadbackEnforcesSealingOwnershipIntegrityAndInvocationLifetime()
    {
        var root = Path.Combine(Path.GetTempPath(), "SemanticMergeOutputTests", Guid.NewGuid().ToString("N"));
        try
        {
            using var cancellation = new CancellationTokenSource();
            using var output = new SemanticMergeOutputWorkspace(root, new Dictionary<MergeInputSide, MergeTreeManifest>(), ["file"], ["file"], cancellation.Token);
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => output.OpenWriteAsync("escape", default).AsTask());
            await using (var stream = await output.OpenWriteAsync("file", default))
            {
                await stream.WriteAsync(new byte[] { 1, 2, 3 });
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => output.OpenReadAsync("file", default).AsTask());
            }
            var read = await output.OpenReadAsync("file", default); Assert.AreEqual(1, read.ReadByte());
            read.Dispose(); await File.WriteAllBytesAsync(Path.Combine(root, "file"), [3, 2, 1]);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => output.OpenReadAsync("file", default).AsTask());
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => output.CompleteAsync());
            await File.WriteAllBytesAsync(Path.Combine(root, "file"), [1, 2, 3]);
            read = await output.OpenReadAsync("file", default); cancellation.Cancel();
            Assert.ThrowsExactly<OperationCanceledException>(() => read.ReadByte()); output.Dispose();
            Assert.ThrowsExactly<ObjectDisposedException>(() => read.ReadByte());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    // Only Merge is registered. No Artifact, discovery, preview or Config Kind ownership exists.
    private sealed class TextPlugin : IFolderRewindPlugin, IMergeProviderCapability
    {
        public MergeProviderId ProviderId => new(new("com.test.text"), "lines");
        public int ContractVersion => 1;
        internal Stream? LastRead;
        public ValueTask<PluginActivationResult> ActivateAsync(IPluginActivationContext context, CancellationToken token)
        { context.RegisterCapability<IMergeProviderCapability>(this); return ValueTask.FromResult(PluginActivationResult.Empty); }
        public ValueTask DeactivateAsync(CancellationToken token) => ValueTask.CompletedTask;
        private static LocalizedText Label(string s) => new(s, new Dictionary<string, string>());
        public async ValueTask<MergeProviderResult> AnalyzeAsync(MergeAnalysisRequest request, IMergePlanWriter plan, PluginInvocationContext context)
        {
            LastRead = await request.Ours.Files.OpenReadAsync("a.txt", context.OperationCancellation);
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => request.Ours.Files.OpenReadAsync("missing.txt", default).AsTask());
            await plan.AddGroupAsync(new("document", Label("Text document"), JsonSerializer.SerializeToElement(1)), default);
            await plan.ClaimPathsAsync("document", ["a.txt", "b.txt"], ["document.txt"], default);
            await plan.AddSubjectsAsync(new[] { "a.txt", "b.txt" }.Select(path => new MergeSubject(path, "document", "fixture-1", Label("Text " + path), Label("Choose a segment"),
                [MergeDecisionKind.Ours, MergeDecisionKind.Theirs, MergeDecisionKind.Base, MergeDecisionKind.Delete, MergeDecisionKind.Manual], false, JsonSerializer.SerializeToElement(path))).ToArray(), default);
            return new(OperationOutcome.SuccessWithWarnings, [new("text.fixture_note", DiagnosticSeverity.Warning, "Merge", "com.test.text", new Dictionary<string, string>())]);
        }
        public async ValueTask<MergeProviderResult> BuildAsync(MergeBuildRequest request, IMergeOutputWorkspace output, PluginInvocationContext context)
        {
            var segments = new SortedDictionary<string, byte[]>(); string? cursor = null;
            do
            {
                var page = await request.Decisions.ReadPageAsync(cursor, 1, default);
                foreach (var decision in page.Decisions)
                {
                    var path = decision.Subject.State.GetString()!;
                    if (decision.Choice == MergeDecisionKind.Delete) continue;
                    await using var input = decision.Choice == MergeDecisionKind.Manual
                        ? await request.Decisions.OpenManualReadAsync(decision.ManualContentId!, default)
                        : await (decision.Choice == MergeDecisionKind.Base ? request.Analysis.Base : decision.Choice == MergeDecisionKind.Ours ? request.Analysis.Ours : request.Analysis.Theirs).Files.OpenReadAsync(path, default);
                    using var bytes = new MemoryStream(); await input.CopyToAsync(bytes); segments.Add(path, bytes.ToArray());
                }
                cursor = page.NextCursor;
            } while (cursor is not null);
            await output.DeleteAsync("a.txt", default); await output.DeleteAsync("b.txt", default);
            await using (var stream = await output.OpenWriteAsync("document.txt", default))
            {
                var first = true;
                foreach (var bytes in segments.Values)
                {
                    if (!first) await stream.WriteAsync(new byte[] { (byte)'|' });
                    await stream.WriteAsync(bytes); first = false;
                }
            }
            await using var verification = await output.OpenReadAsync("document.txt", default);
            Assert.IsFalse(verification.CanWrite);
            return new(OperationOutcome.Success, []);
        }
    }
    private sealed class Resolver : ISemanticMergeResolver
    {
        internal PluginRuntimeManager Runtime { get; } = new();
        internal SemanticMergeBinding Binding { get; private set; } = null!;
        internal bool Available = true;
        internal static async Task<Resolver> Create(TextPlugin provider)
        {
            var label = new LocalizedText("Text", new Dictionary<string, string>());
            var kind = new ConfigKindRef(new("folderrewind.core"), "default");
            var manifest = new PluginManifestContract(provider.ProviderId.PluginId, "1.0.0", new(3, 12), "text.dll", "Text", label, label, [], "settings.json",
                [], [PluginCapabilityKind.MergeProvider], [], [], [], false)
            { MergeProviders = [new(provider.ProviderId, 1, label, [kind], [new("text", "1", label, label)])] };
            return await Create(provider, manifest, new string('A', 64));
        }
        internal static async Task<Resolver> Create(IFolderRewindPlugin plugin, PluginManifestContract manifest, string sha, string? policyId = null)
        {
            var resolver = new Resolver(); var declaration = manifest.MergeProviders.Single();
            var policy = policyId is null ? declaration.Policies[0] : declaration.Policies.Single(p => p.PolicyId == policyId);
            resolver.Binding = new(declaration.ProviderId, declaration.ContractVersion, manifest.Version, sha, policy.PolicyId, policy.Version, declaration.CompatibleConfigKinds[0]);
            var activated = await resolver.Runtime.ActivateAsync(new(manifest.PluginId, () => plugin,
                new(manifest.PluginId, new Dictionary<string, JsonElement>()), [], new DeclaredPluginHostServices(new NoServices(), []), new Store(), manifest));
            Assert.IsTrue(activated.Success, JsonSerializer.Serialize(activated.Diagnostics)); return resolver;
        }
        public ValueTask<SemanticMergeInvocation> AcquireAsync(SemanticMergeBinding binding, CancellationToken token)
        {
            if (!Available || binding != Binding) throw new InvalidOperationException("Pinned provider unavailable.");
            var lease = Runtime.TryAcquire<IMergeProviderCapability>(binding.ProviderId.PluginId, p => p.ProviderId == binding.ProviderId, token)!;
            return ValueTask.FromResult(new SemanticMergeInvocation(lease.Capability, lease.Context, lease));
        }
    }
    private sealed class Store : IPluginActivationStore
    { public ValueTask CommitAsync(PluginActivationCommit commit, CancellationToken token) => ValueTask.CompletedTask; }
    private sealed class NoServices : IPluginHostServices
    {
        public IReadOnlyConfigQueryService Configs => throw new NotSupportedException();
        public IBackupRequestService Backups => throw new NotSupportedException();
        public IRestoreRequestService Restores => throw new NotSupportedException();
        public IHistoryQueryService History => throw new NotSupportedException();
        public IPluginNotificationService Notifications => throw new NotSupportedException();
        public IKnotLinkHostService KnotLink => throw new NotSupportedException();
        public IPluginDataStore DataStore => throw new NotSupportedException();
        public IPluginTemporaryStorage TemporaryStorage => throw new NotSupportedException();
        public IPluginLogger Logger => throw new NotSupportedException();
    }
}

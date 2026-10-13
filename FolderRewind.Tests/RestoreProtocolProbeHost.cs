using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;

namespace FolderRewind.Tests;

// 专用控制台测试入口引用测试程序集；故障回调不编译进 Host 应用。
public static class RestoreProtocolProbeHost
{
    public static async Task RunAsync(string[] args)
    {
        var root = Path.GetFullPath(args[1]);
        ConfigureFault(root, args[2]);
        if (args[0] == "recover-fixture")
        {
            var fixtureParent = Path.Combine(Path.GetTempPath(), "FolderRewindMergeArchive") + Path.DirectorySeparatorChar;
            if (!root.StartsWith(fixtureParent, StringComparison.OrdinalIgnoreCase) || !Guid.TryParse(Path.GetFileName(root), out _))
                throw new InvalidOperationException("Only an isolated benchmark fixture can use this test recovery entry.");
            var repository = Path.Combine(root, "repository");
            var file = Directory.GetFiles(Path.Combine(repository, "transactions"), "restore-journal.json", SearchOption.AllDirectories).First();
            var fixtureJournal = System.Text.Json.JsonSerializer.Deserialize<HistoryRestoreTransactionJournal>(File.ReadAllBytes(file), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
            await using var recovered = new HistoryRuntime(new FileHistoryRepository(fixtureJournal.ExpectedWorkspace.ConfigId, new(repository)));
            await recovered.InitializeAsync();
            await new HistoryRestoreTransactionJournalStore(recovered, new FileSystemHistoryRestoreMutationBackend()).RecoverIncompleteAsync(default);
            return;
        }
        var config = new HistoryConfigId("protocol-probe");
        await using var history = new HistoryRuntime(new FileHistoryRepository(config, new(Path.Combine(root, "history"))));
        await history.InitializeAsync();
        var journals = new HistoryRestoreTransactionJournalStore(history, new FileSystemHistoryRestoreMutationBackend());
        if (args[0] == "recover")
        {
            ConfigureFault(root, args[2]);
            await journals.RecoverIncompleteAsync(default);
            return;
        }
        var source = SourceId.New(); var transaction = HistoryTransactionId.New();
        var target = Path.Combine(root, "world"); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "file.txt"), "original");
        File.WriteAllText(Path.Combine(target, "untouched.txt"), "keep");
        var expected = new HistoryWorkspace(config, 0, [new WorkspaceSourceBaseline(source, null, WorkspaceBaselineRelation.Unknown)]);
        var desired = expected.WithSourceStates([new(source, VersionId.New(), WorkspaceBaselineRelation.Exact)]);
        await history.WorkspaceStore.SaveAsync(expected, -1);
        var location = HistoryRestoreWorkspacePlanner.Plan(transaction, [new(source, target)], [Path.Combine(root, "history")]).Single();
        var journal = new HistoryRestoreTransactionJournal(transaction, HistoryRestoreTransactionPhase.Preparing, expected, desired,
            [], [], [], [], ProtocolVersion: 2, Workspaces: [new(location, Guid.NewGuid().ToString("N"), null, [])]);
        journals.Save(journal);
        ConfigureFault(root, args[2]);
        HistoryRestoreFileSystemPlanStore.Reach("before-container");
        var owned = WindowsRestoreFileSystem.CreateDirectoryNew(location.Container, location.ParentIdentity);
        using (owned.Handle)
        {
            HistoryRestoreFileSystemPlanStore.Reach("after-container-before-register");
            journal = journal with { Workspaces = [journal.Workspaces[0] with {
                ContainerIdentity = owned.Identity, KnownObjects = [new(location.Container, owned.Identity, FileAttributes.Directory)] }] };
            journals.Save(journal);
        }
        HistoryRestoreFileSystemPlanStore.Reach("after-container-register");
        var staging = Path.Combine(location.Container, "staging"); Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "file.txt"), "restored");
        if (args[3] == "batch")
            for (int i = 0; i < 70; i++)
            {
                File.WriteAllText(Path.Combine(target, "batch-" + i), "original-" + i);
                File.WriteAllText(Path.Combine(staging, "batch-" + i), "restored-" + i);
            }
        var members = HistoryRestoreFileSystemPlanStore.CaptureTree(location.Container).Concat(HistoryRestoreFileSystemPlanStore.CaptureTree(target))
            .Append(new RestorePlanObject(root, WindowsRestoreFileSystem.Identity(root), File.GetAttributes(root))).ToArray();
        var moves = new List<RestoreMoveAction>();
        var parent = location.ParentIdentity; var container = owned.Identity;
        if (args[3] is "swap" or "merge")
        {
            moves.Add(new(0, source, target, Path.Combine(location.Container, "rollback"), WindowsRestoreFileSystem.Identity(target), parent, container));
            moves.Add(new(1, source, staging, target, WindowsRestoreFileSystem.Identity(staging), container, parent));
        }
        else
        {
            var targetId = WindowsRestoreFileSystem.Identity(target); var stageId = WindowsRestoreFileSystem.Identity(staging);
            var old = Path.Combine(target, "file.txt"); var fresh = Path.Combine(staging, "file.txt");
            moves.Add(new(0, source, old, Path.Combine(location.Container, "old.txt"), WindowsRestoreFileSystem.Identity(old), targetId, container));
            moves.Add(new(1, source, fresh, old, WindowsRestoreFileSystem.Identity(fresh), stageId, targetId));
            if (args[3] == "batch")
                for (int i = 0; i < 70; i++)
                {
                    var oldFile = Path.Combine(target, "batch-" + i); var newFile = Path.Combine(staging, "batch-" + i);
                    moves.Add(new(moves.Count, source, oldFile, Path.Combine(location.Container, "old-" + i), WindowsRestoreFileSystem.Identity(oldFile), targetId, container));
                    moves.Add(new(moves.Count, source, newFile, oldFile, WindowsRestoreFileSystem.Identity(newFile), stageId, targetId));
                }
        }
        var plan = new HistoryRestoreFileSystemPlan(transaction, [location.Container], members, moves);
        var directory = journals.TransactionDirectory(transaction);
        journal = journal with { Workspaces = [journal.Workspaces[0] with {
            KnownObjects = [.. HistoryRestoreFileSystemPlanStore.CaptureTree(location.Container)] }] };
        journals.Save(journal);
        HistoryRestoreFileSystemPlanStore.Reach("before-plan");
        var proof = HistoryRestoreFileSystemPlanStore.Save(Path.Combine(directory, "restore-plan.json"), plan);
        HistoryRestoreFileSystemPlanStore.Reach("after-plan-before-prepared");
        HistoryCommitPack? pack = null;
        if (args[3] == "merge")
        {
            var version = new SourceVersion(desired.GetSourceState(source).BaseVersionId!.Value, config, source, [], DateTimeOffset.UtcNow,
                null, CaptureScope.FullSource, CaptureOutcome.Captured, [], new("probe", target), null, HistoryProvenance.Native("protocol-probe"));
            pack = new(PackId.New(), transaction, DateTimeOffset.UtcNow, [new HistoryPackCodec().CreateObject(version)]);
        }
        journal = journal with { Phase = HistoryRestoreTransactionPhase.Prepared, FileSystemPlan = proof,
            IntendedPackBytes = pack is null ? null : new HistoryPackCodec().Encode(pack),
            Workspaces = [journal.Workspaces[0] with { KnownObjects = [] }] };
        journals.Save(journal); HistoryRestoreFileSystemPlanStore.Reach("after-prepared");
        using (HistoryRestoreFileSystemPlanStore.Execute(plan, Path.Combine(directory, "restore-progress.bin"), default))
        {
            HistoryRestoreFileSystemPlanStore.ValidatePublished(plan);
            if (pack is not null)
            {
                HistoryRestoreFileSystemPlanStore.Reach("before-pack");
                await history.Repository.CommitAsync(pack);
                HistoryRestoreFileSystemPlanStore.Reach("after-pack");
            }
            journal = journal with { Phase = HistoryRestoreTransactionPhase.WorkspaceApplying }; journals.Save(journal);
            HistoryRestoreFileSystemPlanStore.Reach("before-workspace");
            await journals.CompleteLocalStateAsync(journal, default);
            HistoryRestoreFileSystemPlanStore.Reach("after-workspace");
        }
        journal = journal with { Phase = HistoryRestoreTransactionPhase.Cleaning, CleanupCommitted = true }; journals.Save(journal);
        HistoryRestoreFileSystemPlanStore.Reach("before-cleanup");
        HistoryRestoreFileSystemPlanStore.Cleanup(plan, true, true);
        journals.Save(journal with { Phase = HistoryRestoreTransactionPhase.Complete });
        HistoryRestoreFileSystemPlanStore.Reach("after-complete");
    }
    private static void ConfigureFault(string root, string point)
    {
        HistoryRestoreFileSystemPlanStore.TestFault = reached =>
        {
            if (reached != point) return;
            File.WriteAllText(Path.Combine(root, "ready"), reached);
            using (var signal = new FileStream(Path.Combine(root, "ready"), FileMode.Open, FileAccess.Write, FileShare.Read)) signal.Flush(true);
            Thread.Sleep(Timeout.Infinite); // 测试进程由已记录的 PID 精确终止。
        };
    }
}

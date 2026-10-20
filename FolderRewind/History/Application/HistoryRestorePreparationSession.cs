using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace FolderRewind.History.Application;

internal sealed class HistoryRestorePreparationSession : IDisposable
{
    private static readonly AsyncLocal<HistoryRestorePreparationSession?> Active = new();
    private readonly HistoryRestorePreparationSession? _previous;
    private readonly HistoryRestoreTransactionJournalStore _store;
    private readonly string _repository;
    private readonly HistoryConfigId _config;
    private bool _finished;
    private readonly List<IDisposable> _bindings = [];
    internal HistoryRestoreTransactionJournal Journal { get; private set; }
    internal string? Diagnostic { get; private set; }
    internal HistoryRestorePreparationSession(HistoryRuntime history, HistoryRestoreTransactionJournalStore store,
        HistoryWorkspace expected, IReadOnlyList<HistoryRestoreSourceBinding> bindings, IEnumerable<string> protectedRoots, string? configurationSignature = null)
    {
        _store = store; _repository = history.Repository.Paths.RepositoryRoot; _config = history.ConfigId;
        var id = HistoryTransactionId.New();
        var locations = HistoryRestoreWorkspacePlanner.Plan(id, bindings, protectedRoots.Append(_repository));
        try
        {
            foreach (var location in locations)
            {
                var logical = location.LogicalTarget!;
                _bindings.Add(new WindowsPhysicalDirectoryLease(Directory.Exists(logical) ? logical : Path.GetDirectoryName(logical)!, !Directory.Exists(logical)));
            }
            foreach (var root in protectedRoots.Append(_repository).Where(Directory.Exists))
                _bindings.Add(new WindowsPhysicalDirectoryLease(root));
            foreach (var location in locations)
                if (!StringComparer.OrdinalIgnoreCase.Equals(HistoryRestoreWorkspacePlanner.ResolvePath(location.LogicalTarget!), location.Target))
                    throw new IOException("Restore logical binding changed during preparation.");
        }
        catch { foreach (var binding in _bindings) binding.Dispose(); throw; }
        Journal = new(id, HistoryRestoreTransactionPhase.Preparing, expected, expected, [], [], [], [],
            ProtocolVersion: 2, Workspaces: [.. locations.Select(location => new RestoreWorkspaceRegistration(location, Guid.NewGuid().ToString("N"), null, []))],
            ConfigurationSignature: configurationSignature ?? history.ConfigId.ToString());
        try { _store.SavePreparation(PreparationRecord); }
        catch { foreach (var binding in _bindings) binding.Dispose(); throw; }
        _previous = Active.Value; Active.Value = this;
    }
    internal HistoryRestorePreparationRecord PreparationRecord => new(1, _config, Journal.TransactionId, Journal.Workspaces, Journal.ConfigurationSignature);
    internal static bool OwnsPreparation(HistoryTransactionId transaction, string root) => Active.Value is { } active
        && active.Journal.TransactionId == transaction && !active._finished
        && StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(root), Path.Combine(active._repository, "transactions"));
    internal HistoryRestoreSourceBinding BindSource(HistoryRestoreSourceBinding source)
    {
        var location = Journal.Workspaces.Single(w => w.Location.SourceId == source.SourceId).Location;
        return source with { TargetDirectory = location.Target, LogicalDirectory = source.ComparisonRoot };
    }
    internal void ValidateBindings()
    {
        foreach (var workspace in Journal.Workspaces)
            HistoryRestoreWorkspacePlanner.ValidateBinding(workspace.Location);
    }
    internal static bool OwnsPreparing(HistoryRestoreTransactionJournal journal, string root) => Active.Value is { } active
        && active.Journal.TransactionId == journal.TransactionId && journal.ProtocolVersion == 2
        && journal.Phase == HistoryRestoreTransactionPhase.Preparing
        && StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(root), Path.Combine(active._repository, "transactions"));
    internal static bool IsActiveStaging(string path) => Active.Value?.Journal.Workspaces.Any(workspace =>
        Path.GetFullPath(path).StartsWith(workspace.Location.Container + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) == true;
    internal string CreateStaging(SourceId source)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Restore workspaces require Windows.");
        var index = Journal.Workspaces.IndexOf(Journal.Workspaces.Single(workspace => workspace.Location.SourceId == source));
        var registration = Journal.Workspaces[index];
        if (registration.ContainerIdentity is not null) return Path.Combine(registration.Location.Container, "staging");
        HistoryRestoreFileSystemPlanStore.Reach("before-container");
        var created = WindowsRestoreFileSystem.CreateDirectoryNew(registration.Location.Container, registration.Location.ParentIdentity);
        using (created.Handle)
        {
            HistoryRestoreFileSystemPlanStore.Reach("after-container-before-register");
            registration = registration with { ContainerIdentity = created.Identity,
                KnownObjects = [] };
            Set(index, registration);
            // 容器仅供本次进程用户和 SYSTEM 使用，发布时再恢复目标的权限继承。
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("Cannot identify restore workspace owner.");
            foreach (var identity in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(registration.Location.Container).SetAccessControl(security);
            var marker = Path.Combine(registration.Location.Container, "owner.json");
            using (var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(registration.OwnerNonce); stream.Write(bytes); stream.Flush(true);
            }
            var staging = Path.Combine(registration.Location.Container, "staging");
            var stage = WindowsRestoreFileSystem.CreateDirectoryNew(staging, created.Identity); stage.Handle.Dispose();
            HistoryRestoreFileSystemPlanStore.Reach("after-container-register");
            return staging;
        }
    }
    internal void RecordPrepared(SourceId source)
    {
        int index = Journal.Workspaces.IndexOf(Journal.Workspaces.Single(workspace => workspace.Location.SourceId == source));
        var registration = Journal.Workspaces[index];
        if (WindowsRestoreFileSystem.Identity(registration.Location.Container) != registration.ContainerIdentity
            || File.ReadAllText(Path.Combine(registration.Location.Container, "owner.json")) != registration.OwnerNonce)
            throw new IOException("Restore preparation ownership proof changed.");
    }
    internal void RebindExpected(HistoryWorkspace expected)
    {
        if (Journal.Phase != HistoryRestoreTransactionPhase.Preparing) throw new InvalidOperationException("Restore preparation is already frozen.");
        Journal = Journal with { ExpectedWorkspace = expected, DesiredWorkspace = expected };
    }
    internal HistoryRestoreTransactionJournal Freeze(HistoryRestoreFileSystemPlan plan, HistoryWorkspace expected, HistoryWorkspace desired)
    {
        var path = Path.Combine(_store.TransactionDirectory(Journal.TransactionId), "restore-plan.json");
        var proof = HistoryRestoreFileSystemPlanStore.Save(path, plan);
        HistoryRestoreFileSystemPlanStore.Reach("after-plan-before-prepared");
        var frozen = Journal with { Phase = HistoryRestoreTransactionPhase.Prepared, ExpectedWorkspace = expected, DesiredWorkspace = desired,
            FileSystemPlan = proof, Workspaces = [.. Journal.Workspaces.Select(workspace => workspace with { KnownObjects = [] })] };
        _store.Save(frozen); Journal = frozen;
        HistoryRestoreFileSystemPlanStore.Reach("after-prepared"); return Journal;
    }
    internal void SetJournal(HistoryRestoreTransactionJournal journal) { Journal = journal; _store.Save(Journal); }
    internal void Finish() => _finished = true;
    private void Set(int index, RestoreWorkspaceRegistration registration)
    { Journal = Journal with { Workspaces = Journal.Workspaces.SetItem(index, registration) }; _store.SavePreparation(PreparationRecord); }
    internal void Abort()
    {
        if (_finished || Journal.Phase != HistoryRestoreTransactionPhase.Preparing) return;
        try
        {
            _store.AbandonPreparationRecord(PreparationRecord);
            Journal = Journal with { Phase = HistoryRestoreTransactionPhase.Complete };
            _finished = true;
        }
        catch (Exception error)
        {
            Diagnostic = "Restore preparation requires recovery: " + HistoryRestoreRecoveryDiagnostics.Describe(error);
            HistoryRestoreRecoveryDiagnostics.Report(_config, Journal.TransactionId, "Preparing", false, null, error, "RecoveryBlocked");
        }
    }
    public void Dispose()
    {
        try { Abort(); }
        finally
        {
            if (ReferenceEquals(Active.Value, this)) Active.Value = _previous;
            for (int i = _bindings.Count - 1; i >= 0; i--) _bindings[i].Dispose();
            _bindings.Clear();
        }
    }
}

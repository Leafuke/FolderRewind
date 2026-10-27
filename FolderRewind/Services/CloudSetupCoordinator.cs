using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal enum CloudConnectionEditIntent { Create, Reuse, Edit }
internal enum CloudSetupSaveStage { None, Connection, Binding }
internal sealed class CloudWizardFlow
{
    public int Step { get; set; }
    public int Method { get; set; }
    public bool ReadOnly { get; set; }
    public void Begin(int method, bool toolReady)
    { Method = method; Step = toolReady && method is 1 or 2 ? 2 : 1; }
    public void TargetChanged() { if (Step > 3) Step = 3; }
    public void Back() => Step = Step == 3 && Method == 3 ? 1 : Math.Max(0, Step - 1);
    public bool AcceptLocation(bool readable, bool writable)
    {
        if (!(ReadOnly ? readable : writable)) return false;
        Step = 4; return true;
    }
}
internal sealed record CloudSetupSaveResult(bool ConnectionSaved, bool ProjectBound, bool PersistenceCertain,
    CloudSetupSaveStage FailedStage, Exception? Error = null)
{
    public bool Success => PersistenceCertain && FailedStage == CloudSetupSaveStage.None;
}

// This state belongs to the wizard session, not to an individual page or list snapshot.
internal sealed class CloudSetupSaveSession
{
    public string ProfileId { get; private set; } = Guid.NewGuid().ToString("N");
    public CloudConnectionEditIntent Intent { get; private set; } = CloudConnectionEditIntent.Create;
    public CloudConnectionProfile? Expected { get; private set; }
    public bool PersistenceUncertain { get; internal set; }
    public void Select(CloudConnectionProfile profile, bool edit)
    {
        if (PersistenceUncertain) throw new InvalidOperationException("[cloud.save-uncertain]");
        ProfileId = profile.Id; Expected = CloudConnectionEditPolicy.Copy(profile);
        Intent = edit ? CloudConnectionEditIntent.Edit : CloudConnectionEditIntent.Reuse;
    }
    public void CreateNew()
    {
        if (PersistenceUncertain) throw new InvalidOperationException("[cloud.save-uncertain]");
        ProfileId = Guid.NewGuid().ToString("N"); Expected = null; Intent = CloudConnectionEditIntent.Create;
    }
    internal void Saved(CloudConnectionProfile profile)
    {
        Expected = CloudConnectionEditPolicy.Copy(profile);
        if (Intent == CloudConnectionEditIntent.Create) Intent = CloudConnectionEditIntent.Edit;
    }
}

internal sealed record CloudSetupSaveRequest(CloudConnectionProfile Profile, string? ProjectId,
    string RemoteRoot, string ReadableEvidence, string WritableEvidence, string? ExpectedProjectRevision = null,
    bool CredentialTargetChanged = false);

internal interface ICloudSetupPersistence
{
    CloudConnectionProfile? FindProfile(string id);
    Task ConfirmPersistenceAsync(CancellationToken token);
    Task SaveProfileAsync(CloudConnectionProfile profile, CloudConnectionProfile? expected, CancellationToken token);
    Task BindProjectAsync(CloudSetupSaveRequest request, CancellationToken token);
}

internal sealed class CloudSetupCoordinator(ICloudSetupPersistence persistence)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<CloudSetupSaveResult> SaveAsync(CloudSetupSaveSession session, CloudSetupSaveRequest request, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        var connected = false;
        var stage = CloudSetupSaveStage.Connection;
        try
        {
            if (session.PersistenceUncertain)
            {
                // Flush the compensated, authoritative catalog before retrying either stage.
                await persistence.ConfirmPersistenceAsync(token);
                var reconciled = persistence.FindProfile(session.ProfileId);
                if (reconciled is not null && !CloudConnectionEditPolicy.SameTarget(reconciled, request.Profile))
                    throw new InvalidOperationException("[cloud.connection-conflict]");
                session.PersistenceUncertain = false;
                if (reconciled is not null) session.Saved(reconciled);
            }
            var current = persistence.FindProfile(session.ProfileId);
            if (session.Expected is not null && (current is null || !CloudConnectionEditPolicy.SameSnapshot(current, session.Expected)))
                throw new InvalidOperationException("[cloud.connection-conflict]");
            var profile = CloudConnectionEditPolicy.Copy(request.Profile);
            profile.Id = session.ProfileId;
            if (session.Intent == CloudConnectionEditIntent.Reuse)
            {
                if (current is null || request.CredentialTargetChanged || !CloudConnectionEditPolicy.SameTarget(current, profile))
                    throw new InvalidOperationException("[cloud.new-connection-required]");
                profile = CloudConnectionEditPolicy.Copy(current);
            }
            else
            {
                profile.ProfileRevision = current is not null && !request.CredentialTargetChanged && CloudConnectionEditPolicy.SameTarget(current, profile)
                    ? current.ProfileRevision : current is null ? profile.ProfileRevision : Guid.NewGuid().ToString("N");
                if (current is null || !CloudConnectionEditPolicy.SameSnapshot(current, profile))
                    await persistence.SaveProfileAsync(profile, current, token);
            }
            session.Saved(profile); connected = true;
            if (request.ProjectId is not null)
            {
                stage = CloudSetupSaveStage.Binding;
                await persistence.BindProjectAsync(request with { Profile = profile }, token);
            }
            return new(true, request.ProjectId is not null, true, CloudSetupSaveStage.None);
        }
        catch (Exception ex)
        {
            session.PersistenceUncertain |= ex is ConfigEditRollbackException;
            return new(connected, false, !session.PersistenceUncertain, stage, ex);
        }
        finally { _gate.Release(); }
    }
}

internal static class CloudConnectionEditPolicy
{
    public static CloudConnectionProfile Copy(CloudConnectionProfile p) => new()
    {
        Id = p.Id, DisplayName = p.DisplayName, Scope = p.Scope, OwnerProjectId = p.OwnerProjectId,
        CredentialOwnership = p.CredentialOwnership, CredentialReference = p.CredentialReference,
        ImportedFromReference = p.ImportedFromReference, RemoteName = p.RemoteName, ProfileRevision = p.ProfileRevision,
        DependsOnLocalOpenList = p.DependsOnLocalOpenList, Access = p.Access, VerificationEvidence = p.VerificationEvidence,
        ExecutablePath = p.ExecutablePath, WorkingDirectory = p.WorkingDirectory
    };
    public static bool SameTarget(CloudConnectionProfile a, CloudConnectionProfile b)
        => a.CredentialReference == b.CredentialReference && a.RemoteName == b.RemoteName
            && a.ExecutablePath == b.ExecutablePath && a.WorkingDirectory == b.WorkingDirectory
            && a.DependsOnLocalOpenList == b.DependsOnLocalOpenList
            && !(a.VerificationEvidence.Length > 64 && b.VerificationEvidence.Length > 64
                && a.VerificationEvidence[..64] != b.VerificationEvidence[..64]);
    public static bool SameSnapshot(CloudConnectionProfile a, CloudConnectionProfile b)
        => SameTarget(a, b) && a.Id == b.Id && a.ProfileRevision == b.ProfileRevision
            && a.DisplayName == b.DisplayName && a.Scope == b.Scope && a.OwnerProjectId == b.OwnerProjectId
            && a.CredentialOwnership == b.CredentialOwnership && a.ImportedFromReference == b.ImportedFromReference
            && a.Access == b.Access && a.VerificationEvidence == b.VerificationEvidence;
    public static void ValidateScope(CloudConnectionProfile profile, IEnumerable<string> referencingProjects)
    {
        if (profile.Scope != CloudConnectionScope.Project) return;
        if (string.IsNullOrWhiteSpace(profile.OwnerProjectId) || referencingProjects.Any(id => !SameIdentity(id, profile.OwnerProjectId)))
            throw new InvalidOperationException("[cloud.connection-scope-in-use]");
    }
    private static bool SameIdentity(string a, string? b)
        => Guid.TryParse(a, out var first) && Guid.TryParse(b, out var second) ? first == second : a == b;
}

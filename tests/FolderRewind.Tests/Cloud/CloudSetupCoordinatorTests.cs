using FolderRewind.Models;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class CloudSetupCoordinatorTests
{
    [TestMethod]
    public void WizardSkipsAvailableToolsAndInvalidatesCompletedLocation()
    {
        var flow = new CloudWizardFlow();
        flow.Begin(1, true); Assert.AreEqual(2, flow.Step);
        flow.Begin(2, false); Assert.AreEqual(1, flow.Step);
        flow.Begin(3, true); Assert.AreEqual(1, flow.Step);
        flow.Step = 3; flow.Back(); Assert.AreEqual(1, flow.Step);
        flow.Step = 3;
        Assert.IsFalse(flow.AcceptLocation(true, false)); Assert.AreEqual(3, flow.Step);
        flow.ReadOnly = true;
        Assert.IsTrue(flow.AcceptLocation(true, false)); Assert.AreEqual(4, flow.Step);
        flow.TargetChanged(); Assert.AreEqual(3, flow.Step);
    }
    [TestMethod]
    [DataRow("remote:", "remote:")]
    [DataRow("remote:/", "remote:/")]
    [DataRow("remote:path/child", "remote:path")]
    [DataRow("remote:/path", "remote:/")]
    [DataRow("remote:bucket/prefix", "remote:bucket")]
    public void ParentNavigationPreservesRootSemantics(string path, string expected)
        => Assert.AreEqual(expected, CloudRemotePath.Parent(path));
    private sealed class Store : ICloudSetupPersistence
    {
        public readonly Dictionary<string, CloudConnectionProfile> Profiles = [];
        public int Saves, Bindings, Confirmations;
        public int FailBindings;
        public bool Uncertain, FailConfirmation;
        public string? BoundId;
        public CloudConnectionProfile? FindProfile(string id) => Profiles.GetValueOrDefault(id);
        public Task ConfirmPersistenceAsync(CancellationToken token)
        { Confirmations++; if (FailConfirmation) throw new IOException("unavailable"); return Task.CompletedTask; }
        public Task SaveProfileAsync(CloudConnectionProfile profile, CloudConnectionProfile? expected, CancellationToken token)
        {
            Saves++; Profiles[profile.Id] = CloudConnectionEditPolicy.Copy(profile);
            if (Uncertain) { Uncertain = false; throw new ConfigEditRollbackException("uncertain", new IOException(), new IOException()); }
            return Task.CompletedTask;
        }
        public Task BindProjectAsync(CloudSetupSaveRequest request, CancellationToken token)
        {
            Bindings++; if (FailBindings-- > 0) throw new IOException("binding failure");
            BoundId = request.Profile.Id; return Task.CompletedTask;
        }
    }
    private static CloudConnectionProfile Profile() => new()
    {
        DisplayName = "cloud", RemoteName = "remote", CredentialReference = Path.GetFullPath("first.conf"),
        ExecutablePath = Path.GetFullPath("rclone.exe"), Scope = CloudConnectionScope.Shared,
        Access = CloudConnectionAccess.VerifiedWritable, VerificationEvidence = new string('A', 64) + "|remote:/backup"
    };
    private static CloudSetupSaveRequest Request(CloudConnectionProfile? p = null) => new(p ?? Profile(), "project", "remote:/backup", "read", "write");

    [TestMethod]
    public async Task RepeatedSaveAndBindingRetryKeepOneIdentity()
    {
        var store = new Store { FailBindings = 1 }; var coordinator = new CloudSetupCoordinator(store);
        var session = new CloudSetupSaveSession(); var request = Request(); var id = session.ProfileId;
        var first = await coordinator.SaveAsync(session, request, default);
        Assert.IsTrue(first.ConnectionSaved); Assert.IsFalse(first.ProjectBound);
        Assert.AreEqual(CloudSetupSaveStage.Binding, first.FailedStage);
        Assert.IsTrue((await coordinator.SaveAsync(session, request, default)).Success);
        Assert.IsTrue((await coordinator.SaveAsync(session, request, default)).Success);
        Assert.HasCount(1, store.Profiles); Assert.AreEqual(1, store.Saves); Assert.AreEqual(id, store.BoundId);
    }
    [TestMethod]
    public async Task UncertainPersistenceMustBeConfirmedBeforeBinding()
    {
        var store = new Store { Uncertain = true }; var session = new CloudSetupSaveSession();
        var coordinator = new CloudSetupCoordinator(store); var request = Request();
        Assert.IsFalse((await coordinator.SaveAsync(session, request, default)).PersistenceCertain);
        Assert.AreEqual(0, store.Bindings);
        store.FailConfirmation = true;
        Assert.IsFalse((await coordinator.SaveAsync(session, request, default)).PersistenceCertain);
        Assert.AreEqual(0, store.Bindings);
        store.FailConfirmation = false;
        Assert.IsTrue((await coordinator.SaveAsync(session, request, default)).Success);
        Assert.AreEqual(1, store.Saves); Assert.AreEqual(2, store.Confirmations);
    }
    [TestMethod]
    public async Task NameEditPreservesRevisionAndReuseDoesNotEditSharedMetadata()
    {
        var store = new Store(); var original = Profile(); store.Profiles.Add(original.Id, original);
        var session = new CloudSetupSaveSession(); session.Select(original, false);
        var coordinator = new CloudSetupCoordinator(store);
        var renamed = CloudConnectionEditPolicy.Copy(original); renamed.DisplayName = "renamed";
        Assert.IsTrue((await coordinator.SaveAsync(session, Request(renamed), default)).Success);
        Assert.AreEqual("cloud", store.Profiles[original.Id].DisplayName);
        session.Select(original, true);
        Assert.IsTrue((await coordinator.SaveAsync(session, Request(renamed), default)).Success);
        Assert.AreEqual("renamed", store.Profiles[original.Id].DisplayName);
        Assert.AreEqual(original.ProfileRevision, store.Profiles[original.Id].ProfileRevision);
        Assert.AreEqual(original.VerificationEvidence, store.Profiles[original.Id].VerificationEvidence);
    }
    [TestMethod]
    public async Task ChangedSharedTargetCannotBeImplicitlyReused()
    {
        var store = new Store(); var original = Profile(); store.Profiles.Add(original.Id, original);
        var session = new CloudSetupSaveSession(); session.Select(original, false);
        var replacement = CloudConnectionEditPolicy.Copy(original); replacement.RemoteName = "other";
        var result = await new CloudSetupCoordinator(store).SaveAsync(session, Request(replacement), default);
        Assert.IsFalse(result.Success); Assert.AreEqual(0, store.Saves); Assert.AreEqual(0, store.Bindings);
    }
    [TestMethod]
    public async Task ChangedCredentialTargetRequiresExplicitEditAndNewRevision()
    {
        var store = new Store(); var original = Profile(); store.Profiles.Add(original.Id, original);
        var session = new CloudSetupSaveSession(); session.Select(original, false);
        var coordinator = new CloudSetupCoordinator(store);
        var request = Request(CloudConnectionEditPolicy.Copy(original)) with { CredentialTargetChanged = true };
        Assert.IsFalse((await coordinator.SaveAsync(session, request, default)).Success);
        session.Select(original, true);
        Assert.IsTrue((await coordinator.SaveAsync(session, request, default)).Success);
        Assert.AreNotEqual(original.ProfileRevision, store.Profiles[original.Id].ProfileRevision);
    }
    [TestMethod]
    public async Task NewImportKeepsOldProfileAndUsesIndependentIdentity()
    {
        var store = new Store(); var original = Profile(); store.Profiles.Add(original.Id, original);
        var session = new CloudSetupSaveSession(); session.Select(original, false); session.CreateNew();
        var imported = CloudConnectionEditPolicy.Copy(original); imported.CredentialReference = Path.GetFullPath("new.conf");
        Assert.IsTrue((await new CloudSetupCoordinator(store).SaveAsync(session, Request(imported), default)).Success);
        Assert.HasCount(2, store.Profiles); Assert.AreNotEqual(original.Id, store.BoundId);
        Assert.AreEqual(original.CredentialReference, store.Profiles[original.Id].CredentialReference);
    }
    [TestMethod]
    public void ScopeReductionRejectsOtherProjectReferences()
    {
        var profile = Profile(); profile.Scope = CloudConnectionScope.Project; profile.OwnerProjectId = "owner";
        CloudConnectionEditPolicy.ValidateScope(profile, ["owner"]);
        Assert.ThrowsExactly<InvalidOperationException>(() => CloudConnectionEditPolicy.ValidateScope(profile, ["owner", "other"]));
    }
}

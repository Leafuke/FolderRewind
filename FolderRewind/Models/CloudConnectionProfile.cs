using System;

namespace FolderRewind.Models;

public enum CloudConnectionScope { Project, Shared }
public enum CloudCredentialOwnership { ManagedCopy, ExternalReference }
public enum CloudConnectionAccess { NeedsAuthentication, ConnectedUnverified, VerifiedReadable, VerifiedWritable }
public enum CloudSetupIntent { Connect, ProjectStatus, Recover }
public sealed record CloudSetupNavigationParameter(string ConfigId = "", bool ManageConnection = false, string? ProfileId = null,
    CloudSetupIntent Intent = CloudSetupIntent.Connect, string? SessionId = null);

// 连接可见性与实际凭据存储互相独立；Token 刷新不能修改目标语义修订。
public sealed class CloudConnectionProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "";
    public CloudConnectionScope Scope { get; set; }
    public string? OwnerProjectId { get; set; }
    public CloudCredentialOwnership CredentialOwnership { get; set; }
    public string CredentialReference { get; set; } = "";
    public string ImportedFromReference { get; set; } = "";
    public string RemoteName { get; set; } = "";
    public string ProfileRevision { get; set; } = Guid.NewGuid().ToString("N");
    public bool DependsOnLocalOpenList { get; set; }
    public CloudConnectionAccess Access { get; set; } = CloudConnectionAccess.NeedsAuthentication;
    public string VerificationEvidence { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
}

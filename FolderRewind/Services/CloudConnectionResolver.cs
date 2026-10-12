using FolderRewind.Models;
using System;
using System.IO;

namespace FolderRewind.Services;

public sealed record ResolvedCloudConnection(string ProfileId, string ProfileRevision, string BindingRevision,
    string RemoteRoot, string CredentialReference, string ExecutablePath, string WorkingDirectory);

public static class CloudConnectionResolver
{
    public static ResolvedCloudConnection Resolve(CloudConnectionProfile profile, string projectId, string suffix, string bindingRevision)
    {
        if (profile.Scope == CloudConnectionScope.Project && profile.OwnerProjectId != projectId)
            throw new InvalidOperationException("[connection.owner-mismatch] Project connection belongs to a different project.");
        if (string.IsNullOrEmpty(profile.Id) || string.IsNullOrEmpty(profile.ProfileRevision) || string.IsNullOrEmpty(bindingRevision)
            || !Path.IsPathFullyQualified(profile.CredentialReference))
            throw new InvalidOperationException("[connection.credentials-missing] Connection requires authentication.");
        var root = profile.RemoteName + ":" + suffix;
        CloudRemotePath.Split(root);
        return new(profile.Id, profile.ProfileRevision, bindingRevision, root, profile.CredentialReference,
            profile.ExecutablePath, profile.WorkingDirectory);
    }
}

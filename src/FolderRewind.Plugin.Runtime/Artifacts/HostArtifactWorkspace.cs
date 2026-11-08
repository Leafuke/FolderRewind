using System.Security.AccessControl;
using System.Security.Principal;
using FolderRewind.Plugin.Runtime.IO;

namespace FolderRewind.Plugin.Runtime.Artifacts;

/// <summary>Host-created plaintext directories have a protected owner ACL and reject filesystem aliases.</summary>
public static class HostArtifactWorkspace
{
    public static void CreatePrivateDirectory(string path, string? trustedRoot = null)
    {
        path = Path.GetFullPath(path);
        trustedRoot ??= Path.GetDirectoryName(path)!;
        using var parent = DirectoryPathLease.BindExistingAncestor(Path.GetDirectoryName(path)!);
        DirectoryPathBoundary.RequireUnlinkedPath(path, trustedRoot);
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var directory = new DirectoryInfo(path);
            if (directory.Exists) RequireOwner(directory, identity);
            var security = new DirectorySecurity();
            security.SetOwner(identity.User!);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            if (directory.Exists) directory.SetAccessControl(security);
            else FileSystemAclExtensions.Create(directory, security);
        }
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public static void DeleteOwnedDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        using var parent = DirectoryPathLease.BindExistingAncestor(Path.GetDirectoryName(Path.GetFullPath(path))!);
        ValidateOwnedDirectory(path);
        // Archive attributes can be read-only. Normalize only this validated private tree before deletion.
        var entries = new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions
            { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false });
        foreach (var entry in entries)
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Artifact workspace contains a filesystem alias.");
            if ((entry.Attributes & FileAttributes.ReadOnly) != 0) entry.Attributes &= ~FileAttributes.ReadOnly;
        }
        var root = new DirectoryInfo(path);
        if ((root.Attributes & FileAttributes.ReadOnly) != 0) root.Attributes &= ~FileAttributes.ReadOnly;
        Directory.Delete(path, true);
    }

    public static void ValidateOwnedDirectory(string path, CancellationToken token = default)
    {
        using var parent = DirectoryPathLease.BindExistingAncestor(Path.GetDirectoryName(Path.GetFullPath(path))!);
        DirectoryPathBoundary.RequireUnlinkedPath(path, Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            RequireOwner(new DirectoryInfo(path), identity);
        }
        var pending = new Stack<string>(); pending.Push(path);
        while (pending.TryPop(out var directory))
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Artifact workspace contains a filesystem alias.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RequireOwner(DirectoryInfo directory, WindowsIdentity identity)
    {
        var owner = directory.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
        if (!Equals(owner, identity.User) && !Equals(owner, identity.Owner))
            throw new UnauthorizedAccessException("Artifact workspace is not owned by the Host user.");
    }
}

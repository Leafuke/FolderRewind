using FolderRewind.Plugin.Runtime.IO;
using System;

namespace FolderRewind.History.Storage;

/// <summary>Preserve Core's identity while sharing the directory binding implementation.</summary>
internal sealed class WindowsPhysicalDirectoryLease : IDisposable
{
    private readonly DirectoryPathLease _lease;
    internal string PhysicalPath => _lease.PhysicalPath;
    internal RestoreObjectIdentity Identity { get; }
    internal WindowsPhysicalDirectoryLease(string directory, bool protectFinal = true)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Physical directory binding requires Windows.");
        _lease = new(directory, protectFinal, allowNetwork: false);
        var id = _lease.Identity;
        Identity = new(id.Volume, id.Low, id.High, id.Directory);
    }
    public void Dispose() => _lease.Dispose();
}

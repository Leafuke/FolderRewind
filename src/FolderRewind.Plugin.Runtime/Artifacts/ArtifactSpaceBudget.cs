using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace FolderRewind.Plugin.Runtime.Artifacts;

public sealed record ArtifactSpaceRequest(string Path, long Bytes);
public sealed record ArtifactVolumeCapacity(string VolumeId, long AvailableBytes);

/// <summary>Accounts concurrent outstanding writes by actual volume, rather than reserving the maximum quota for each tiny input.</summary>
public sealed class ArtifactSpaceBudget(Func<string, ArtifactVolumeCapacity>? describeVolume = null)
{
    public static ArtifactSpaceBudget Shared { get; } = new();
    private readonly object _sync = new();
    private readonly Dictionary<string, long> _reserved = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, ArtifactVolumeCapacity> _describe = describeVolume ?? DescribeVolume;

    public IDisposable Reserve(params ArtifactSpaceRequest[] requests)
    {
        var volumes = requests.Select(request =>
        {
            if (request.Bytes < 0) throw new ArgumentOutOfRangeException(nameof(requests));
            return (Capacity: _describe(request.Path), request.Bytes);
        }).GroupBy(r => r.Capacity.VolumeId, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Id: g.Key, Bytes: g.Sum(r => r.Bytes), Available: g.Min(r => r.Capacity.AvailableBytes))).ToArray();
        lock (_sync)
        {
            foreach (var volume in volumes)
                if (volume.Bytes > volume.Available - _reserved.GetValueOrDefault(volume.Id))
                    throw new IOException("Insufficient temporary space for the cumulative Artifact volume budget.");
            foreach (var volume in volumes) _reserved[volume.Id] = checked(_reserved.GetValueOrDefault(volume.Id) + volume.Bytes);
        }
        return new NativeArtifactStore.ActionLease(() =>
        {
            lock (_sync)
                foreach (var volume in volumes)
                {
                    var remaining = _reserved[volume.Id] - volume.Bytes;
                    if (remaining == 0) _reserved.Remove(volume.Id); else _reserved[volume.Id] = remaining;
                }
        });
    }

    private static ArtifactVolumeCapacity DescribeVolume(string path)
    {
        path = Path.GetFullPath(path);
        while (!Directory.Exists(path) && Directory.GetParent(path) is { } parent) path = parent.FullName;
        if (OperatingSystem.IsWindows())
        {
            var mount = new StringBuilder(1024); var name = new StringBuilder(1024);
            if (!GetVolumePathName(path, mount, mount.Capacity)
                || !GetDiskFreeSpaceEx(mount.ToString(), out var available, out _, out _))
                throw new IOException("Cannot inspect the Artifact temporary volume.", new Win32Exception(Marshal.GetLastWin32Error()));
            var id = GetVolumeNameForVolumeMountPoint(mount.ToString(), name, name.Capacity) ? name.ToString() : mount.ToString();
            return new(id, (long)Math.Min(available, (ulong)long.MaxValue));
        }
        var drive = DriveInfo.GetDrives().Where(d => path.StartsWith(d.Name, StringComparison.Ordinal))
            .OrderByDescending(d => d.Name.Length).First();
        return new(drive.Name, drive.AvailableFreeSpace);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumePathNameW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumePathName(string path, StringBuilder volume, int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeNameForVolumeMountPointW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumeNameForVolumeMountPoint(string mount, StringBuilder volume, int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetDiskFreeSpaceExW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetDiskFreeSpaceEx(string path, out ulong available, out ulong total, out ulong free);
}

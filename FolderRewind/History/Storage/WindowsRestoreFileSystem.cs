using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FolderRewind.History.Storage;

internal sealed record RestoreObjectIdentity(ulong Volume, ulong Low, ulong High, bool Directory);

/// <summary>NTFS 身份与无覆盖移动。句柄固定源和父目录；不能退回路径 stat 后 Move 的竞争窗口。</summary>
internal static class WindowsRestoreFileSystem
{
    internal static (RestoreObjectIdentity Identity, SafeFileHandle Handle) CreateDirectoryNew(string path,
        RestoreObjectIdentity expectedParent)
    {
        using var ancestors = new WindowsDirectoryReadLease(path);
        using var parent = Open(ancestors.PhysicalDirectory, 0x81, 3);
        RequireOrdinary(parent);
        if (Identity(parent) != expectedParent) throw new IOException("Restore preparation parent changed.");
        var name = Path.GetFileName(path);
        var text = Marshal.StringToHGlobalUni(name);
        var unicode = new UnicodeString { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2 + 2)), Buffer = text };
        var nameBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            Marshal.StructureToPtr(unicode, nameBuffer, false);
            var attributes = new ObjectAttributes { Length = (uint)Marshal.SizeOf<ObjectAttributes>(), Root = parent.DangerousGetHandle(), Name = nameBuffer, Attributes = 0x40 };
            // FILE_CREATE 原子排他创建并返回同一目录句柄，消除 CreateDirectory 后重新按路径打开的归属窗口。
            var status = NtCreateFile(out var created, 0xC0100180, ref attributes, out _, IntPtr.Zero,
                0x10, 3, 2, 0x00200021, IntPtr.Zero, 0);
            if (status < 0) { created?.Dispose(); throw new IOException("Cannot exclusively create restore directory.", new Win32Exception((int)RtlNtStatusToDosError(status))); }
            try { return (Identity(created), created); }
            catch { created.Dispose(); throw; }
        }
        finally { Marshal.FreeHGlobal(nameBuffer); Marshal.FreeHGlobal(text); }
    }
    internal static RestoreObjectIdentity Identity(string path)
    {
        using var handle = Open(path, 0x80, 7);
        return Identity(handle);
    }
    internal static RestoreObjectIdentity? TryIdentity(string path)
    {
        try { return Identity(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    internal static RestoreObjectIdentity Identity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, 18, out var id, (uint)Marshal.SizeOf<FileIdInfo>())
            || !GetFileInformationByHandle(handle, out var info)) throw Error("Cannot identify restore object.");
        return new(id.Volume, id.Low, id.High, (info.Attributes & (uint)FileAttributes.Directory) != 0);
    }
    internal static FileAttributes Attributes(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw Error("Cannot inspect restore object.");
        return (FileAttributes)info.Attributes;
    }
    internal static void RequireOrdinary(SafeFileHandle handle)
    {
        if ((Attributes(handle) & FileAttributes.ReparsePoint) != 0) throw new IOException("Affected restore object is a reparse point.");
    }
    internal static void MoveOwnedNoReplace(string source, string destination, RestoreObjectIdentity expected,
        RestoreObjectIdentity? expectedSourceParent = null, RestoreObjectIdentity? expectedDestinationParent = null, bool ancestorsProtected = false,
        RestoreParentHandles? protectedParents = null, string? expectedContentHash = null)
    {
        using var timing = Services.HistoryOperationDiagnostics.Current?.Measure("identity-move");
        using var sourceParents = ancestorsProtected ? null : new WindowsDirectoryReadLease(source);
        using var targetParents = ancestorsProtected ? null : new WindowsDirectoryReadLease(destination);
        using var handle = Open(source, 0x80010180, 1); // READ + DELETE + WRITE/READ_ATTRIBUTES，禁止外部写入与删除。
        RequireOrdinary(handle);
        if (Identity(handle) != expected) throw new IOException("Restore source identity changed.");
        using var content = expectedContentHash is not null ? new FileStream(handle, FileAccess.Read) : null;
        if (content is not null && !StringComparer.Ordinal.Equals(expectedContentHash,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content))))
            throw new IOException("Local state content changed before protected retirement.");
        var cachedSource = protectedParents?.Find(expectedSourceParent);
        using var sourceParent = cachedSource is null ? Open(sourceParents?.PhysicalDirectory ?? Path.GetDirectoryName(Path.GetFullPath(source))!, 0x80, 3) : null;
        if (sourceParent is not null) RequireOrdinary(sourceParent);
        if (cachedSource is null && expectedSourceParent is not null && Identity(sourceParent!) != expectedSourceParent)
            throw new IOException("Restore source parent identity changed.");
        var parent = targetParents?.PhysicalDirectory ?? Path.GetDirectoryName(Path.GetFullPath(destination))!;
        var cachedTarget = protectedParents?.Find(expectedDestinationParent);
        using var targetParent = cachedTarget is null ? Open(parent, 0x80, 3) : null;
        if (targetParent is not null) RequireOrdinary(targetParent);
        var targetParentIdentity = cachedTarget is not null ? expectedDestinationParent! : Identity(targetParent!);
        if (expectedDestinationParent is not null && targetParentIdentity != expectedDestinationParent)
            throw new IOException("Restore destination parent identity changed.");
        if (targetParentIdentity.Volume != expected.Volume) throw new IOException("Restore move crosses volumes.");
        var name = Encoding.Unicode.GetBytes(Path.GetFileName(Path.GetFullPath(destination)));
        int rootOffset = IntPtr.Size == 8 ? 8 : 4, lengthOffset = rootOffset + IntPtr.Size, nameOffset = lengthOffset + 4;
        var buffer = Marshal.AllocHGlobal(nameOffset + name.Length + 2);
        try
        {
            Marshal.Copy(new byte[nameOffset + name.Length + 2], 0, buffer, nameOffset + name.Length + 2);
            Marshal.WriteIntPtr(buffer, rootOffset, (cachedTarget ?? targetParent!).DangerousGetHandle());
            Marshal.WriteInt32(buffer, lengthOffset, name.Length);
            Marshal.Copy(name, 0, buffer + nameOffset, name.Length);
            // ReplaceIfExists=FALSE。NtSetInformationFile 支持 RootDirectory，直接绑定
            // 已固定父目录与单个文件名；Win32 包装不支持非空 RootDirectory。
            var status = NtSetInformationFile(handle, out _, buffer, (uint)(nameOffset + name.Length + 2), 10);
            if (status < 0)
                throw new IOException("Owned restore move failed without replacing the destination.", new Win32Exception((int)RtlNtStatusToDosError(status)));
            if (Identity(handle) != expected || TryIdentity(destination) != expected)
                throw new IOException("Restore move result identity is uncertain.");
            Services.HistoryOperationDiagnostics.Current?.Count("file-moves");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    internal sealed class RestoreParentHandles : IDisposable
    {
        private readonly Dictionary<RestoreObjectIdentity, SafeFileHandle> _handles = [];
        private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
        internal RestoreParentHandles(IEnumerable<(string Path, RestoreObjectIdentity Identity)> parents, bool allowChildRenames = false)
        {
            try
            {
                foreach (var parent in parents)
                {
                    // 目录自身禁止改名/删除。NTFS 子项改名还需要父目录写入共享；
                    // 清理仅删除文件，不需要此共享，拒绝外部写入父目录对象。
                    // READ_ATTRIBUTES 单独打开不参与 Windows 的读写共享检查；
                    // LIST_DIRECTORY 使禁止写入/删除的共享约束真正生效。
                    var handle = Open(parent.Path, 0x81, allowChildRenames ? 3u : 1u);
                    try
                    {
                        RequireOrdinary(handle);
                        if (Identity(handle) != parent.Identity) throw new IOException("Restore parent binding changed.");
                        _handles.Add(parent.Identity, handle);
                        _paths.Add(Path.GetFullPath(parent.Path));
                    }
                    catch { handle.Dispose(); throw; }
                }
            }
            catch { Dispose(); throw; }
        }
        internal SafeFileHandle? Find(RestoreObjectIdentity? identity)
        {
            var handle = identity is not null ? _handles.GetValueOrDefault(identity) : null;
            if (handle is not null && (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0))
                throw new IOException("Restore protected parent became a reparse point.");
            return handle;
        }
        internal bool Protects(string path) => _paths.Contains(Path.GetFullPath(path));
        public void Dispose() { foreach (var handle in _handles.Values) handle.Dispose(); _handles.Clear(); _paths.Clear(); }
    }
    internal static void DeleteOwned(string path, RestoreObjectIdentity expected, RestoreParentHandles? protectedParents = null)
    {
        using var parents = protectedParents is null ? new WindowsDirectoryReadLease(path) : null;
        if (protectedParents is not null && !protectedParents.Protects(Path.GetDirectoryName(Path.GetFullPath(path))!))
            throw new IOException("Restore cleanup parent is not protected.");
        using var handle = Open(path, 0x80010080, 1);
        RequireOrdinary(handle);
        if (Identity(handle) != expected) throw new IOException("Restore cleanup ownership changed.");
        if (expected.Directory && Directory.EnumerateFileSystemEntries(path).GetEnumerator() is var entries)
        {
            using (entries) if (entries.MoveNext()) throw new IOException("Restore cleanup directory has new members.");
        }
        var disposition = Marshal.AllocHGlobal(4);
        try
        {
            // 只删除目录项，不改变范围外硬链接共享的 ReadOnly 或 ACL。
            // 立即解除已确认归属的目录项，避免其他允许删除的读取句柄留下 pending 名称阻塞父目录清理。
            Marshal.WriteInt32(disposition, 0x13); // DELETE | POSIX_SEMANTICS | IGNORE_READONLY_ATTRIBUTE
            if (!SetFileInformationByHandle(handle, 21, disposition, 4))
            {
                if ((Attributes(handle) & FileAttributes.ReadOnly) != 0) throw Error("Owned readonly cleanup requires maintenance.");
                Marshal.WriteInt32(disposition, 1);
                if (!SetFileInformationByHandle(handle, 4, disposition, 4)) throw Error("Owned restore cleanup failed.");
            }
        }
        finally { Marshal.FreeHGlobal(disposition); }
    }
    internal static SafeFileHandle Open(string path, uint access, uint share)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Restore moves require Windows NTFS.");
        var absolute = Path.GetFullPath(path);
        var handle = CreateFileW(ExtendedPath(absolute), access, share, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error(); handle.Dispose();
        if (error == 2) throw new FileNotFoundException("Restore object is absent.", path);
        if (error == 3) throw new DirectoryNotFoundException("Restore object parent is absent.");
        throw new IOException("Cannot protect restore object.", new Win32Exception(error));
    }
    internal static string ExtendedPath(string path)
    {
        if (path.StartsWith("\\\\?\\", StringComparison.Ordinal)) return path;
        if (path.StartsWith("\\\\", StringComparison.Ordinal)) throw new IOException("Network restore paths are unsupported.");
        return "\\\\?\\" + path;
    }
    internal static (RestoreObjectIdentity Identity, ulong FreeBytes) RequireLocalNtfs(string parent)
    {
        if (GetDriveTypeW(Path.GetPathRoot(Path.GetFullPath(parent))!) is not (2 or 3))
            throw new IOException("Ordinary restore requires a local NTFS drive; network and special drives are unsupported.");
        using var ancestors = new WindowsDirectoryReadLease(Path.Combine(parent, "unused-child"));
        using var handle = Open(parent, 0x80, 3);
        var fs = new StringBuilder(32);
        if (!GetVolumeInformationByHandleW(handle, null, 0, out _, out _, out _, fs, (uint)fs.Capacity)
            || !fs.ToString().Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Ordinary restore requires a local NTFS volume with object identity support.");
        if (!GetDiskFreeSpaceExW(parent, out var available, out _, out _)) throw Error("Cannot inspect restore volume free space.");
        return (Identity(handle), available);
    }
    private static IOException Error(string message) => new(message, new Win32Exception(Marshal.GetLastWin32Error()));
    [StructLayout(LayoutKind.Sequential)] private struct FileIdInfo { internal ulong Volume, Low, High; }
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { internal ushort Length, MaximumLength; internal IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes { internal uint Length; internal IntPtr Root, Name; internal uint Attributes; internal IntPtr Security, SecurityQuality; }
    [StructLayout(LayoutKind.Sequential)] private struct IoStatusBlock { internal IntPtr Status; internal nuint Information; }
    [StructLayout(LayoutKind.Sequential)] private struct FileBasicInfo { internal long Creation, Access, Write, Change; internal uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        internal uint Attributes; internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out FileIdInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetRawInformation(SafeFileHandle handle, int kind, IntPtr info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, IntPtr info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetBasicInformation(SafeFileHandle handle, int kind, ref FileBasicInfo info, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandleW(SafeFileHandle handle, StringBuilder? name, uint nameSize,
        out uint serial, out uint maxComponentLength, out uint flags, StringBuilder fileSystem, uint fileSystemSize);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string path, out ulong available, out ulong total, out ulong free);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetDriveTypeW(string root);
    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes,
        out IoStatusBlock ioStatus, IntPtr allocationSize, uint fileAttributes, uint share, uint disposition,
        uint options, IntPtr ea, uint eaLength);
    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(SafeFileHandle handle, out IoStatusBlock status, IntPtr information, uint length, int informationClass);
    [DllImport("ntdll.dll")] private static extern uint RtlNtStatusToDosError(int status);
}

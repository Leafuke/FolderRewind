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
        using var parent = Open(Path.GetDirectoryName(Path.GetFullPath(path))!, 0x84, 3);
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
        if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0) throw new IOException("Restore object is a reparse point.");
        if ((info.Attributes & (uint)FileAttributes.Directory) == 0 && info.Links != 1)
            throw new IOException("Restore mutation does not support affected hard links.");
        RequireNoAlternateStreams(handle, (info.Attributes & (uint)FileAttributes.Directory) != 0);
        return new(id.Volume, id.Low, id.High, (info.Attributes & (uint)FileAttributes.Directory) != 0);
    }
    private static void RequireNoAlternateStreams(SafeFileHandle handle, bool directory)
    {
        var buffer = Marshal.AllocHGlobal(65536);
        try
        {
            if (!GetRawInformation(handle, 7, buffer, 65536))
            {
                var error = Marshal.GetLastWin32Error();
                if (directory && error == 38) return;
                throw new IOException("Cannot prove restore stream safety.", new Win32Exception(error));
            }
            int offset = 0;
            while (true)
            {
                if (offset > 65536 - 24) throw new IOException("Restore stream metadata is ambiguous.");
                var entry = buffer + offset; int next = Marshal.ReadInt32(entry), length = Marshal.ReadInt32(entry, 4);
                if (length < 0 || length > 65536 - offset - 24 || length % 2 != 0) throw new IOException("Restore stream metadata is invalid.");
                var name = Marshal.PtrToStringUni(entry + 24, length / 2);
                if (name != "::$DATA") throw new IOException("Restore mutation does not support alternate data streams.");
                if (next == 0) break;
                if (next < 24 + length) throw new IOException("Restore stream metadata overlaps.");
                offset = checked(offset + next);
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    internal static void MoveOwnedNoReplace(string source, string destination, RestoreObjectIdentity expected,
        RestoreObjectIdentity? expectedSourceParent = null, RestoreObjectIdentity? expectedDestinationParent = null, bool ancestorsProtected = false)
    {
        using var timing = Services.HistoryOperationDiagnostics.Current?.Measure("identity-move");
        using var sourceParents = ancestorsProtected ? null : new WindowsDirectoryReadLease(source);
        using var targetParents = ancestorsProtected ? null : new WindowsDirectoryReadLease(destination);
        using var handle = Open(source, 0x80010180, 1); // READ + DELETE + WRITE/READ_ATTRIBUTES，禁止外部写入与删除。
        if (Identity(handle) != expected) throw new IOException("Restore source identity changed.");
        using var sourceParent = Open(Path.GetDirectoryName(Path.GetFullPath(source))!, 0x80, 3);
        if (expectedSourceParent is not null && Identity(sourceParent) != expectedSourceParent)
            throw new IOException("Restore source parent identity changed.");
        var parent = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        using var targetParent = Open(parent, 0x80, 3);
        var targetParentIdentity = Identity(targetParent);
        if (expectedDestinationParent is not null && targetParentIdentity != expectedDestinationParent)
            throw new IOException("Restore destination parent identity changed.");
        if (targetParentIdentity.Volume != expected.Volume) throw new IOException("Restore move crosses volumes.");
        var name = Encoding.Unicode.GetBytes("\\\\?\\" + Path.GetFullPath(destination));
        int rootOffset = IntPtr.Size == 8 ? 8 : 4, lengthOffset = rootOffset + IntPtr.Size, nameOffset = lengthOffset + 4;
        var buffer = Marshal.AllocHGlobal(nameOffset + name.Length + 2);
        try
        {
            Marshal.Copy(new byte[nameOffset + name.Length + 2], 0, buffer, nameOffset + name.Length + 2);
            Marshal.WriteIntPtr(buffer, rootOffset, IntPtr.Zero);
            Marshal.WriteInt32(buffer, lengthOffset, name.Length);
            Marshal.Copy(name, 0, buffer + nameOffset, name.Length);
            // ReplaceIfExists=FALSE。目标所有祖先在整个调用期间持有禁止删除/改名的句柄。
            if (!SetFileInformationByHandle(handle, 3, buffer, (uint)(nameOffset + name.Length + 2)))
                throw Error("Owned restore move failed without replacing the destination.");
            if (Identity(handle) != expected || TryIdentity(destination) != expected)
                throw new IOException("Restore move result identity is uncertain.");
            Services.HistoryOperationDiagnostics.Current?.Count("file-moves");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    internal static void DeleteOwned(string path, RestoreObjectIdentity expected)
    {
        using var parents = new WindowsDirectoryReadLease(path);
        using var handle = Open(path, 0x80010180, 1);
        if (Identity(handle) != expected) throw new IOException("Restore cleanup ownership changed.");
        if (expected.Directory && Directory.EnumerateFileSystemEntries(path).GetEnumerator() is var entries)
        {
            using (entries) if (entries.MoveNext()) throw new IOException("Restore cleanup directory has new members.");
        }
        // 文件属性不作为所有权证明。只调整已固定对象的 ReadOnly 位，避免清理更改陌生对象。
        if (!GetFileInformationByHandle(handle, out var info)) throw Error("Cannot inspect cleanup object.");
        if ((info.Attributes & (uint)FileAttributes.ReadOnly) != 0)
        {
            var basic = new FileBasicInfo { Attributes = info.Attributes & ~(uint)FileAttributes.ReadOnly };
            if (basic.Attributes == 0) basic.Attributes = (uint)FileAttributes.Normal;
            if (!SetBasicInformation(handle, 0, ref basic, (uint)Marshal.SizeOf<FileBasicInfo>())) throw Error("Cannot clear owned readonly attribute.");
        }
        var disposition = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(disposition, 1);
            if (!SetFileInformationByHandle(handle, 4, disposition, 4)) throw Error("Owned restore cleanup failed.");
        }
        finally { Marshal.FreeHGlobal(disposition); }
    }
    internal static SafeFileHandle Open(string path, uint access, uint share)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Restore moves require Windows NTFS.");
        var absolute = Path.GetFullPath(path);
        if (absolute.StartsWith("\\\\", StringComparison.Ordinal)) throw new IOException("Network restore paths are unsupported.");
        var handle = CreateFileW("\\\\?\\" + absolute, access, share, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error(); handle.Dispose();
        if (error == 2) throw new FileNotFoundException("Restore object is absent.", path);
        if (error == 3) throw new DirectoryNotFoundException("Restore object parent is absent.");
        throw new IOException("Cannot protect restore object.", new Win32Exception(error));
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
    [DllImport("ntdll.dll")] private static extern uint RtlNtStatusToDosError(int status);
}

using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace FolderRewind.History.Storage;

/// <summary>固定载荷路径祖先，防止读取租约期间通过替换父目录改变路径绑定。</summary>
internal sealed class WindowsDirectoryReadLease : IDisposable
{
    internal static FileStream OpenPayload(string path, bool allowRename = false)
    {
        var handle = CreateFileW(ExtendedPath(Path.GetFullPath(path)), 0x80000000, allowRename ? 5u : 1u, IntPtr.Zero, 3, 0x40200000, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid) throw new IOException("Cannot protect archive payload.", new Win32Exception(Marshal.GetLastWin32Error()));
            if (!GetFileInformationByHandle(handle, out var info)
                || (info.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException("Archive payload is not a regular file.");
            return new FileStream(handle, FileAccess.Read, 65536, true);
        }
        catch { handle.Dispose(); throw; }
    }
    private readonly WindowsPhysicalDirectoryLease _directory;
    internal string PhysicalDirectory => _directory.PhysicalPath;
    internal WindowsDirectoryReadLease(string filePath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Protected archive reads require Windows.");
        _directory = new WindowsPhysicalDirectoryLease(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
    }
    public void Dispose() => _directory.Dispose();
    private static string ExtendedPath(string path) => path.StartsWith("\\\\?\\", StringComparison.Ordinal) ? path : path.StartsWith("\\\\", StringComparison.Ordinal)
        ? "\\\\?\\UNC\\" + path[2..] : "\\\\?\\" + path;
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, AccessTime, WriteTime;
        internal uint VolumeSerialNumber, SizeHigh, SizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}

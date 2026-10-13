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
    internal static FileStream OpenPayload(string path)
    {
        var handle = CreateFileW(ExtendedPath(Path.GetFullPath(path)), 0x80000000, 1, IntPtr.Zero, 3, 0x40200000, IntPtr.Zero);
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
    private readonly List<SafeFileHandle> _handles = [];
    internal WindowsDirectoryReadLease(string filePath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Protected archive reads require Windows.");
        var parent = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        var ancestors = new Stack<string>();
        while (parent is not null) { ancestors.Push(parent); parent = Path.GetDirectoryName(parent); }
        try
        {
            while (ancestors.TryPop(out var directory))
            {
                var handle = CreateFileW(ExtendedPath(directory), 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Cannot protect archive parent.", new Win32Exception(Marshal.GetLastWin32Error())); }
                _handles.Add(handle);
                if (!GetFileInformationByHandle(handle, out var info)) throw new IOException("Cannot inspect protected archive parent.");
                if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0) throw new IOException("Archive path traverses a reparse point.");
            }
        }
        catch { Dispose(); throw; }
    }
    public void Dispose() { for (int i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose(); _handles.Clear(); }
    private static string ExtendedPath(string path) => path.StartsWith("\\\\", StringComparison.Ordinal)
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

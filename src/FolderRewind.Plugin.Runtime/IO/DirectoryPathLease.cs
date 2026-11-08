using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FolderRewind.Plugin.Runtime.IO;

public sealed record DirectoryObjectIdentity(ulong Volume, ulong Low, ulong High, bool Directory);

/// <summary>固定普通目录和已支持的链接入口；逻辑路径仍由调用方用于领域语义。</summary>
public sealed class DirectoryPathLease : IDisposable
{
    private readonly bool _allowNetwork;
    private readonly List<SafeFileHandle> _handles = [];
    private readonly HashSet<DirectoryObjectIdentity> _resolving = [];
    public string PhysicalPath { get; }
    public DirectoryObjectIdentity Identity { get; }
    public DirectoryPathLease(string directory, bool protectFinal = true, bool allowNetwork = true)
    {
        _allowNetwork = allowNetwork;
        if (!OperatingSystem.IsWindows())
        {
            directory = Path.GetFullPath(directory);
            DirectoryPathBoundary.RequireUnlinkedPath(directory, Path.GetPathRoot(directory)!);
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
            PhysicalPath = directory;
            Identity = new(0, 0, 0, true);
            return;
        }
        try { (PhysicalPath, Identity) = Resolve(Path.GetFullPath(directory), protectFinal, 0); }
        catch { Dispose(); throw; }
    }
    private (string Path, DirectoryObjectIdentity Identity) Resolve(string path, bool protectFinal, int depth)
    {
        if (depth > 32) throw new IOException("Directory redirection is cyclic or too deep: " + path);
        path = Path.GetFullPath(path);
        var root = Path.GetPathRoot(path) ?? throw new IOException("Directory binding has no volume: " + path);
        if (!_allowNetwork && (root.StartsWith("\\\\", StringComparison.Ordinal) && !root.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || root.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Network directory bindings are unsupported: " + path);
        var parts = path[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (int i = -1; i < parts.Length; i++)
        {
            if (i >= 0) current = Path.Combine(current, parts[i]);
            bool last = i == parts.Length - 1;
            var handle = Open(current, 0x81, 3);
            try
            {
                var attributes = Attributes(handle);
                var identity = Identify(handle);
                if (!identity.Directory) throw new IOException("Directory binding is not a directory: " + current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // 禁止链接入口写入/改名，连同目标路径一起保护，不能只固定最终目录。
                    var pinned = Open(current, 0x81, 1);
                    _handles.Add(pinned);
                    if (Identify(pinned) != identity) throw new IOException("Directory link changed: " + current);
                    var linkIdentity = identity;
                    if (!_resolving.Add(linkIdentity)) throw new IOException("Directory redirection cycle: " + current);
                    try
                    {
                        var resolved = Resolve(ReadTarget(pinned, current), !last || protectFinal, depth + 1);
                        current = resolved.Path;
                        identity = resolved.Identity;
                    }
                    finally { _resolving.Remove(linkIdentity); }
                }
                else if (!last || protectFinal) { _handles.Add(handle); handle = null!; }
                if (last)
                {
                    using var final = Open(current, 0x80, 7);
                    if (Identify(final) != identity) throw new IOException("Physical directory changed: " + current);
                    return (FinalPath(final, current), identity);
                }
            }
            finally { handle?.Dispose(); }
        }
        throw new IOException("Directory binding is empty.");
    }
    private static string ReadTarget(SafeFileHandle handle, string entry)
    {
        var buffer = new byte[16 * 1024];
        if (!DeviceIoControl(handle, 0x900A8, IntPtr.Zero, 0, buffer, buffer.Length, out var read, IntPtr.Zero))
            throw new IOException("Cannot read directory redirection: " + entry, new Win32Exception(Marshal.GetLastWin32Error()));
        if (read < 16) throw new IOException("Invalid directory redirection: " + entry);
        var tag = BitConverter.ToUInt32(buffer, 0);
        int start = tag switch { 0xA0000003 => 16, 0xA000000C => 20, _ => throw new IOException($"Unsupported directory reparse tag 0x{tag:X8}: {entry}") };
        int offset = BitConverter.ToUInt16(buffer, 8), length = BitConverter.ToUInt16(buffer, 10);
        if (read < start || ((offset | length) & 1) != 0 || start + offset + length > read)
            throw new IOException("Invalid directory redirection: " + entry);
        var target = Encoding.Unicode.GetString(buffer, start + offset, length);
        if (tag == 0xA000000C && (BitConverter.ToUInt32(buffer, 16) & 1) != 0)
            return Path.GetFullPath(target, Path.GetDirectoryName(entry)!);
        if (target.StartsWith("\\??\\UNC\\", StringComparison.OrdinalIgnoreCase)) return "\\\\" + target[8..];
        if (target.StartsWith("\\??\\Volume{", StringComparison.OrdinalIgnoreCase)) return "\\\\?\\" + target[4..];
        if (target.StartsWith("\\??\\", StringComparison.Ordinal)) target = target[4..];
        if (!Path.IsPathFullyQualified(target)) throw new IOException("Unsupported directory redirection target: " + entry);
        return target;
    }
    private static string FinalPath(SafeFileHandle handle, string path)
    {
        var flags = path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? 0u : 1u;
        var result = new StringBuilder(512);
        var size = GetFinalPathNameByHandleW(handle, result, (uint)result.Capacity, flags);
        if (size >= result.Capacity)
        {
            result.EnsureCapacity(checked((int)size + 1));
            size = GetFinalPathNameByHandleW(handle, result, (uint)result.Capacity, flags);
        }
        if (size == 0 || size >= result.Capacity) throw new IOException("Cannot resolve physical volume path.", new Win32Exception(Marshal.GetLastWin32Error()));
        return Path.TrimEndingDirectorySeparator(result.ToString());
    }
    public void Dispose()
    {
        for (int i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose();
        _handles.Clear();
    }

    public static DirectoryPathLease BindExistingAncestor(string directory)
    {
        var current = Path.GetFullPath(directory);
        while (true)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            { current = Path.GetDirectoryName(current) ?? throw new DirectoryNotFoundException(directory); continue; }
            if ((attributes & FileAttributes.Directory) == 0) throw new IOException("Storage parent is not a directory.");
            return new(current); // A broken existing redirection is not a missing directory to create.
        }
    }
    private static SafeFileHandle Open(string path, uint access, uint share)
    {
        var extended = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
            : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
        var handle = CreateFileW(extended, access, share, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error(); handle.Dispose();
        if (error == 2) throw new FileNotFoundException("Directory entry disappeared.", path);
        if (error == 3) throw new DirectoryNotFoundException(path);
        throw new IOException("Cannot bind storage directory.", new Win32Exception(error));
    }
    private static DirectoryObjectIdentity Identify(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, 18, out var id, (uint)Marshal.SizeOf<FileIdInfo>())
            || !GetFileInformationByHandle(handle, out var info))
            throw new IOException("Cannot identify storage directory.", new Win32Exception(Marshal.GetLastWin32Error()));
        return new(id.Volume, id.Low, id.High, (info.Attributes & (uint)FileAttributes.Directory) != 0);
    }
    private static FileAttributes Attributes(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw new IOException("Cannot inspect storage directory.", new Win32Exception(Marshal.GetLastWin32Error()));
        return (FileAttributes)info.Attributes;
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileIdInfo { internal ulong Volume, Low, High; }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out FileIdInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, int inputSize,
        byte[] output, int outputSize, out int bytes, IntPtr overlapped);
}

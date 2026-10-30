using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FolderRewind.History.Storage;

/// <summary>固定普通目录和已支持的链接入口；逻辑路径仍由调用方用于领域语义。</summary>
internal sealed class WindowsPhysicalDirectoryLease : IDisposable
{
    private readonly List<SafeFileHandle> _handles = [];
    private readonly HashSet<RestoreObjectIdentity> _resolving = [];
    internal string PhysicalPath { get; }
    internal RestoreObjectIdentity Identity { get; }
    internal WindowsPhysicalDirectoryLease(string directory, bool protectFinal = true)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Physical directory binding requires Windows.");
        try { (PhysicalPath, Identity) = Resolve(Path.GetFullPath(directory), protectFinal, 0); }
        catch { Dispose(); throw; }
    }
    private (string Path, RestoreObjectIdentity Identity) Resolve(string path, bool protectFinal, int depth)
    {
        if (depth > 32) throw new IOException("Directory redirection is cyclic or too deep: " + path);
        path = Path.GetFullPath(path);
        var root = Path.GetPathRoot(path) ?? throw new IOException("Directory binding has no volume: " + path);
        if (root.StartsWith("\\\\", StringComparison.Ordinal) && !root.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || root.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Network directory bindings are unsupported: " + path);
        var parts = path[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (int i = -1; i < parts.Length; i++)
        {
            if (i >= 0) current = Path.Combine(current, parts[i]);
            bool last = i == parts.Length - 1;
            var handle = WindowsRestoreFileSystem.Open(current, 0x81, 3);
            try
            {
                var attributes = WindowsRestoreFileSystem.Attributes(handle);
                var identity = WindowsRestoreFileSystem.Identity(handle);
                if (!identity.Directory) throw new IOException("Directory binding is not a directory: " + current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // 禁止链接入口写入/改名，连同目标路径一起保护，不能只固定最终目录。
                    var pinned = WindowsRestoreFileSystem.Open(current, 0x81, 1);
                    _handles.Add(pinned);
                    if (WindowsRestoreFileSystem.Identity(pinned) != identity) throw new IOException("Directory link changed: " + current);
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
                    using var final = WindowsRestoreFileSystem.Open(current, 0x80, 7);
                    if (WindowsRestoreFileSystem.Identity(final) != identity) throw new IOException("Physical directory changed: " + current);
                    return (FinalPath(final), identity);
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
    private static string FinalPath(SafeFileHandle handle)
    {
        var result = new StringBuilder(512);
        var size = GetFinalPathNameByHandleW(handle, result, (uint)result.Capacity, 1);
        if (size >= result.Capacity)
        {
            result.EnsureCapacity(checked((int)size + 1));
            size = GetFinalPathNameByHandleW(handle, result, (uint)result.Capacity, 1);
        }
        if (size == 0 || size >= result.Capacity) throw new IOException("Cannot resolve physical volume path.", new Win32Exception(Marshal.GetLastWin32Error()));
        return Path.TrimEndingDirectorySeparator(result.ToString());
    }
    public void Dispose()
    {
        for (int i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose();
        _handles.Clear();
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, int inputSize,
        byte[] output, int outputSize, out int bytes, IntPtr overlapped);
}

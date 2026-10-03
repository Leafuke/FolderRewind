using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;

namespace FolderRewind.Services;

// 任务快照是敏感本机暂存，不导出，不写回外部配置；路径冻结不能代替内容冻结。
public sealed class RcloneExecutionContext : IDisposable
{
    private readonly byte[] _revision;
    private readonly string _taskDirectory;
    private readonly FileStream _ownership;
    private readonly string[] _backendOptions;
    public string ExecutablePath { get; }
    public string ConfigPath { get; }
    public string WorkingDirectory { get; }
    public string RemoteRoot { get; }
    public string SnapshotPath { get; }
    public string RevisionEvidence => Convert.ToHexString(_revision);
    private bool _disposed;
    public RcloneExecutionContext(string executable, string configPath, string workingDirectory, string remoteRoot, string taskRoot)
    {
        if (!Path.IsPathFullyQualified(executable) || !Path.IsPathFullyQualified(configPath) || (!string.IsNullOrEmpty(workingDirectory) && !Path.IsPathFullyQualified(workingDirectory)))
            throw new IOException("Bound rclone connections require absolute executable, config, and working directory paths.");
        ExecutablePath = Path.GetFullPath(executable);
        ConfigPath = Path.GetFullPath(configPath);
        WorkingDirectory = Path.GetFullPath(string.IsNullOrEmpty(workingDirectory) ? Path.GetDirectoryName(ExecutablePath)! : workingDirectory);
        RemoteRoot = remoteRoot;
        if (!File.Exists(ExecutablePath) || !Directory.Exists(WorkingDirectory)) throw new IOException("Rclone executable or working directory is unavailable.");
        if (!File.Exists(ConfigPath) || new FileInfo(ConfigPath).Length > 1024 * 1024) throw new IOException("Rclone configuration is unavailable or exceeds the bound.");
        var content = ReadConfigurationBytes(ConfigPath);
        var config = System.Text.Encoding.UTF8.GetString(content).TrimStart('\uFEFF');
        _backendOptions = config.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.IndexOf('=') is var equals && equals >= 0 && line[..equals].Trim().Equals("type", StringComparison.OrdinalIgnoreCase))
            .Select(line => line[(line.IndexOf('=') + 1)..].Trim())
            .Where(type => type.Length != 0 && type.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            .Select(type => type.Replace('_', '-') + "-").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (config.Contains("RCLONE_ENCRYPT_V", StringComparison.Ordinal)) throw new InvalidOperationException("Interactive encrypted rclone configurations require foreground unlock; task snapshots are not supported.");
        var remoteName = remoteRoot.Split(':', 2)[0];
        if (!remoteRoot.Contains(':') || string.IsNullOrWhiteSpace(remoteName) || remoteName.IndexOfAny(['[', ']', '\r', '\n', '/', '\\']) >= 0
            || !config.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Any(line => line.Trim() == "[" + remoteName + "]"))
            throw new InvalidOperationException("The selected remote is missing from this configuration.");
        _revision = SHA256.HashData(content);
        var root = Path.GetFullPath(taskRoot);
        Directory.CreateDirectory(root);
        CleanupExpired(root, DateTimeOffset.UtcNow);
        _taskDirectory = Path.Combine(root, "task-" + Guid.NewGuid().ToString("N"));
        CreatePrivateDirectory(_taskDirectory);
        SnapshotPath = Path.Combine(_taskDirectory, "rclone.conf");
        _ownership = new FileStream(Path.Combine(_taskDirectory, "ownership.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        try { File.WriteAllBytes(SnapshotPath, content); }
        catch
        {
            _ownership.Dispose();
            try { File.Delete(SnapshotPath); File.Delete(Path.Combine(_taskDirectory, "ownership.lock")); Directory.Delete(_taskDirectory); } catch (IOException) { }
            throw;
        }
    }
    internal static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            FileSystemAclExtensions.Create(new DirectoryInfo(path), security);
        }
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    internal static byte[] ReadConfigurationBytes(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 1024 * 1024) throw new IOException("Rclone configuration exceeds its size bound.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new IOException("Rclone configuration changed during inspection.");
        return bytes;
    }
    public static int CleanupExpired(string taskRoot, DateTimeOffset now)
    {
        var root = Path.GetFullPath(taskRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return 0;
        int cleaned = 0;
        foreach (var directory in Directory.EnumerateDirectories(root, "task-*", SearchOption.TopDirectoryOnly).Take(1000))
        {
            var name = Path.GetFileName(directory);
            if (name.Length != 37 || !Guid.TryParseExact(name[5..], "N", out _) || !Path.GetFullPath(directory).StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 || now.UtcDateTime - Directory.GetLastWriteTimeUtc(directory) < TimeSpan.FromDays(2)) continue;
            var marker = Path.Combine(directory, "ownership.lock"); var config = Path.Combine(directory, "rclone.conf");
            if (!File.Exists(marker) || Directory.GetDirectories(directory).Length != 0 || Directory.GetFiles(directory).Any(file => file != marker && file != config)) continue;
            try
            {
                // 活跃任务持有此独占句柄；崩溃后只有锁已释放的本应用目录能回收。
                using (var lease = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) File.Delete(config);
                File.Delete(marker); Directory.Delete(directory); cleaned++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return cleaned;
    }
    public void RequireUnchanged()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!File.Exists(ConfigPath) || new FileInfo(ConfigPath).Length > 1024 * 1024
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(ReadConfigurationBytes(ConfigPath)), _revision))
            throw new InvalidOperationException("The rclone connection changed while the task was queued or running. Confirm the connection again.");
    }
    public ProcessStartInfo CreateStartInfo(IReadOnlyList<string> arguments)
    {
        RequireUnchanged();
        if (arguments.Count == 0 || !new[] { "ls", "lsf", "lsl", "lsjson", "cat", "copy", "copyto", "sync", "move", "moveto", "delete", "deletefile", "purge", "mkdir", "check", "checksum", "hashsum", "size", "about", "version" }
            .Contains(arguments[0], StringComparer.OrdinalIgnoreCase)
            || arguments.Any(arg => arg.Contains('\r') || arg.Contains('\n') || arg.StartsWith('-') &&
                (arg.TrimStart('-').Split('=', 2)[0].Equals("config", StringComparison.OrdinalIgnoreCase)
                || arg.TrimStart('-').StartsWith("password-command", StringComparison.OrdinalIgnoreCase)
                || arg.TrimStart('-').StartsWith("header", StringComparison.OrdinalIgnoreCase)
                || arg.TrimStart('-').StartsWith("ask-password", StringComparison.OrdinalIgnoreCase)
                || arg.TrimStart('-').StartsWith("webdav-", StringComparison.OrdinalIgnoreCase)
                || _backendOptions.Any(prefix => arg.TrimStart('-').StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))))
            throw new InvalidOperationException("Arguments conflict with the confirmed rclone connection.");
        var start = new ProcessStartInfo(ExecutablePath) { WorkingDirectory = WorkingDirectory, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("RCLONE_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.ArgumentList.Add("--config"); start.ArgumentList.Add(SnapshotPath);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ownership.Dispose();
        // 只清理由本任务创建的确切文件，不递归清理用户凭据目录。
        try { File.Delete(SnapshotPath); File.Delete(Path.Combine(_taskDirectory, "ownership.lock")); Directory.Delete(_taskDirectory); }
        catch (IOException) { } // 临时锁释放后可由有界过期回收处理，不改变业务成功事实。
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class RcloneExecutionScope : IDisposable
{
    private static readonly AsyncLocal<RcloneExecutionContext?> Ambient = new();
    public static RcloneExecutionContext? Current => Ambient.Value;
    private readonly RcloneExecutionContext? _previous;
    private readonly RcloneExecutionContext? _owned;
    public RcloneExecutionScope(RcloneExecutionContext? context, bool owns = true)
    {
        _previous = Ambient.Value;
        _owned = owns ? context : null;
        Ambient.Value = context;
    }
    public void Dispose() { Ambient.Value = _previous; _owned?.Dispose(); }
}

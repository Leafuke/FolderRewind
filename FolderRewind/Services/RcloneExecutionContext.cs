using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
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
    private byte[] _revision;
    internal byte[] SourceBytes { get; private set; }
    internal string[] AllowedTokenSections { get; }
    internal bool AllowTokenWriteback { get; }
    internal bool PreserveSnapshot { get; set; }
    internal string TaskDirectory => _taskDirectory;
    internal string TaskRoot => Path.GetDirectoryName(_taskDirectory)!;
    private static readonly ConcurrentDictionary<string, RcloneExecutionContext> Contexts = new(StringComparer.OrdinalIgnoreCase);
    internal static bool TryFind(string snapshot, out RcloneExecutionContext context) => Contexts.TryGetValue(snapshot, out context!);
    internal static bool HasActiveCredential(string path) => Path.IsPathFullyQualified(path) && Contexts.Values.Any(c => !c._disposed
        && StringComparer.OrdinalIgnoreCase.Equals(c.ConfigPath, Path.GetFullPath(path)));
    private readonly string _taskDirectory;
    private readonly FileStream _ownership;
    private readonly string[] _backendOptions;
    public string ExecutablePath { get; }
    public string ConfigPath { get; }
    public string WorkingDirectory { get; }
    public string RemoteRoot { get; }
    public string SnapshotPath { get; }
    public string RevisionEvidence => Convert.ToHexString(_revision);
    public string TargetRevisionEvidence { get; }
    internal Action? TargetGuard { get; set; }
    private bool _disposed;
    public RcloneExecutionContext(string executable, string configPath, string workingDirectory, string remoteRoot, string taskRoot,
        bool allowTokenWriteback = false)
    {
        if (!Path.IsPathFullyQualified(executable) || !Path.IsPathFullyQualified(configPath) || (!string.IsNullOrEmpty(workingDirectory) && !Path.IsPathFullyQualified(workingDirectory)))
            throw new IOException("Bound rclone connections require absolute executable, config, and working directory paths.");
        ExecutablePath = Path.GetFullPath(executable);
        ConfigPath = Path.GetFullPath(configPath);
        RcloneCredentialRefresh.RejectReparsePoints(ConfigPath);
        AllowTokenWriteback = allowTokenWriteback;
        WorkingDirectory = Path.GetFullPath(string.IsNullOrEmpty(workingDirectory) ? Path.GetDirectoryName(ExecutablePath)! : workingDirectory);
        RemoteRoot = remoteRoot;
        if (!File.Exists(ExecutablePath) || !Directory.Exists(WorkingDirectory)) throw new IOException("Rclone executable or working directory is unavailable.");
        byte[] content;
        try { content = ReadConfigurationBytes(ConfigPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw RcloneConnectionDiagnostics.Failure("cloud.source-config-unavailable", "CloudSetup_SourceConfigUnavailable", ex);
        }
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
            throw RcloneConnectionDiagnostics.InvalidFailure("cloud.remote-mismatch", "CloudSetup_RemoteMismatch");
        _revision = SHA256.HashData(content);
        SourceBytes = content;
        TargetRevisionEvidence = RcloneCredentialRefresh.TargetRevision(content);
        AllowedTokenSections = RcloneCredentialRefresh.AuthenticationSections(content, remoteName);
        if (AllowedTokenSections.Length != 0 && !allowTokenWriteback)
            throw RcloneConnectionDiagnostics.InvalidFailure("connection.external-oauth-unsupported", "CloudConnection_ExternalOAuthUnsupported");
        var root = Path.GetFullPath(taskRoot);
        _taskDirectory = Path.Combine(root, "task-" + Guid.NewGuid().ToString("N"));
        SnapshotPath = Path.Combine(_taskDirectory, "rclone.conf");
        FileStream? ownership = null;
        try
        {
            Directory.CreateDirectory(root);
            CleanupExpired(root, DateTimeOffset.UtcNow);
            CreatePrivateDirectory(_taskDirectory);
            ownership = new FileStream(Path.Combine(_taskDirectory, "ownership.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            RcloneCredentialRefresh.RegisterSnapshot(_taskDirectory, ConfigPath, RevisionEvidence);
            File.WriteAllBytes(SnapshotPath, content);
            _ownership = ownership;
            Contexts[SnapshotPath] = this;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ownership?.Dispose();
            try { File.Delete(SnapshotPath); File.Delete(Path.Combine(_taskDirectory, "snapshot.json")); File.Delete(Path.Combine(_taskDirectory, "ownership.lock")); Directory.Delete(_taskDirectory); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            throw RcloneConnectionDiagnostics.Failure("cloud.snapshot-create-failed", "CloudSetup_SnapshotCreateFailed", ex);
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
            var directory = new DirectoryInfo(path);
            if (directory.Exists) directory.SetAccessControl(security);
            else FileSystemAclExtensions.Create(directory, security);
        }
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    internal static void ProtectConfigurationFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
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
            var registration = Path.Combine(directory, "snapshot.json");
            // 旧版/未知快照没有刷新完成证据，不再按年龄删除可能唯一的新 Token。
            if (!File.Exists(marker) || File.Exists(config) || Directory.GetDirectories(directory).Length != 0
                || Directory.GetFiles(directory).Any(file => file != marker && file != registration)) continue;
            try
            {
                // 活跃任务持有此独占句柄；崩溃后只有锁已释放的本应用目录能回收。
                using (var lease = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) File.Delete(registration);
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
        if (!File.Exists(ConfigPath) || new FileInfo(ConfigPath).Length > 1024 * 1024)
            throw RcloneConnectionDiagnostics.InvalidFailure("cloud.connection-changed", "CloudSetup_ConnectionChanged");
        var current = ReadConfigurationBytes(ConfigPath);
        if (CryptographicOperations.FixedTimeEquals(SHA256.HashData(current), _revision)) return;
        if (!RcloneCredentialRefresh.AcceptsFrozenRevision(ConfigPath, RevisionEvidence, current)
            || !RcloneCredentialRefresh.TokenOnlyDifference(SourceBytes, current, AllowedTokenSections))
            throw RcloneConnectionDiagnostics.InvalidFailure("cloud.connection-changed", "CloudSetup_ConnectionChanged");
        AcceptCredentialRevision(current);
    }
    internal void AcceptCredentialRevision(byte[] bytes)
    {
        SourceBytes = bytes; _revision = SHA256.HashData(bytes);
    }
    public ProcessStartInfo CreateStartInfo(IReadOnlyList<string> arguments)
    {
        TargetGuard?.Invoke();
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
        Contexts.TryRemove(SnapshotPath, out _);
        _ownership.Dispose();
        if (PreserveSnapshot || File.Exists(Path.Combine(_taskDirectory, "attempt.json"))) return;
        // 只清理由本任务创建的确切文件，不递归清理用户凭据目录。
        try { File.Delete(SnapshotPath); File.Delete(Path.Combine(_taskDirectory, "snapshot.json")); File.Delete(Path.Combine(_taskDirectory, "ownership.lock")); Directory.Delete(_taskDirectory); }
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

// Keep diagnostics alongside the execution primitive so its existing linked-source
// consumers do not acquire a dependency on the application's configuration service.
internal static class RcloneConnectionDiagnostics
{
    internal static IOException Failure(string code, string resource, Exception? inner = null)
        => Mark(new IOException("[" + code + "] " + I18n.GetString(resource), inner), code);
    internal static InvalidOperationException InvalidFailure(string code, string resource)
        => Mark(new InvalidOperationException("[" + code + "] " + I18n.GetString(resource)), code);
    private static T Mark<T>(T exception, string code) where T : Exception
    {
        exception.Data["RcloneDiagnosticCode"] = code;
        return exception;
    }
    internal static string Code(Exception exception, string fallback = "cloud.access-unconfirmed")
    {
        if (exception.Data["RcloneDiagnosticCode"] is string code) return code;
        if (exception.Message.StartsWith("[cloud.snapshot-unavailable]", StringComparison.Ordinal)) return "cloud.snapshot-unavailable";
        return fallback;
    }
    internal static string ProcessMessage(ProcessStartInfo start, string error)
    {
        var configIndex = start.ArgumentList.IndexOf("--config");
        if (configIndex >= 0 && configIndex + 1 < start.ArgumentList.Count)
        {
            var snapshot = start.ArgumentList[configIndex + 1];
            // Require an explicit missing-file report naming our exact config argument.
            // A missing remote section alone does not establish a filesystem problem.
            if (error.Contains("Config file", StringComparison.OrdinalIgnoreCase)
                && error.Contains("not found", StringComparison.OrdinalIgnoreCase)
                && (error.Contains('"' + snapshot + '"', StringComparison.OrdinalIgnoreCase)
                    || error.Contains('\'' + snapshot + '\'', StringComparison.OrdinalIgnoreCase)
                    || error.Contains('"' + snapshot.Replace("\\", "\\\\", StringComparison.Ordinal) + '"', StringComparison.OrdinalIgnoreCase)))
                return "[cloud.snapshot-unavailable] " + I18n.GetString("CloudSetup_SnapshotUnavailable") + "\n" + CloudCommandSecurity.Redact(error);
        }
        return CloudCommandSecurity.Redact(error);
    }
}

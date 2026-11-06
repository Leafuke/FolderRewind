using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

// 凭据门闩只在命令执行层获取；不能持有 History/配置变更锁等待网络。
internal sealed partial class RcloneCredentialRefresh : IAsyncDisposable
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> AcceptedRevisions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, bool> UnsettledSources = new(StringComparer.OrdinalIgnoreCase);
    internal static bool MustPreserveOwnedCredentials(string source)
        => UnsettledSources.ContainsKey(source) || File.Exists(LedgerPath(source)) || RcloneCredentialStore.HasTrackedEvidence(source);
    private readonly RcloneExecutionContext _context;
    private readonly SemaphoreSlim _gate;
    private readonly FileStream _fileLock;
    private readonly string _attemptPath;
    private CredentialRefreshAttempt _attempt;
    private bool _completed;
    private static string StorageLockPath(string source, string taskRoot, bool managed)
        => managed ? source + ".folderrewind.lock" : Path.Combine(taskRoot, "credential-" + Hash(Encoding.UTF8.GetBytes(source.ToUpperInvariant())) + ".lock");
    internal static async Task PrepareStoreAsync(string source, string root, bool managed, CancellationToken token)
    {
        source = Path.GetFullPath(source); root = Path.GetFullPath(root);
        RcloneCredentialStore.Track(source, root, managed);
        try
        {
            await Task.Run(() => { RejectReparsePoints(source); RejectHardLinks(source); RejectReparsePoints(root); RcloneExecutionContext.CreatePrivateDirectory(root); }, token).ConfigureAwait(false);
        }
        catch (Exception error) { RcloneConnectionDiagnostics.AtStage(error, CredentialFailureStage.PrepareStore, source); throw; }
        var gate = Gates.GetOrAdd(source, _ => new(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        FileStream? fileLock = null;
        try
        {
            while (fileLock is null)
            {
                token.ThrowIfCancellationRequested();
                try { fileLock = new(StorageLockPath(source, root, managed), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { await Task.Delay(100, token).ConfigureAwait(false); }
            }
            await RecoverAsync(root, source, "", token, managed).ConfigureAwait(false);
        }
        catch (Exception error) { RcloneConnectionDiagnostics.AtStage(error, CredentialFailureStage.PrepareStore, source); throw; }
        finally { fileLock?.Dispose(); gate.Release(); }
    }

    private RcloneCredentialRefresh(RcloneExecutionContext context, SemaphoreSlim gate, FileStream fileLock, CredentialRefreshAttempt attempt)
    { _context = context; _gate = gate; _fileLock = fileLock; _attempt = attempt; _attemptPath = Path.Combine(context.TaskDirectory, "attempt.json"); }

    internal static async Task<RcloneCredentialRefresh?> BeginAsync(ProcessStartInfo start, CancellationToken token)
    {
        var index = start.ArgumentList.IndexOf("--config");
        if (index < 0 || index + 1 >= start.ArgumentList.Count
            || !RcloneExecutionContext.TryFind(start.ArgumentList[index + 1], out var context)) return null;
        var path = context.ConfigPath;
        RejectReparsePoints(path); RejectReparsePoints(context.TaskDirectory);
        RejectHardLinks(path);
        var gate = Gates.GetOrAdd(path, _ => new(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        FileStream? fileLock = null;
        try
        {
            if (File.Exists(Path.Combine(context.TaskDirectory, "attempt.json"))) throw Conflict();
            // 实际文件路径共用跨进程门闩。外部 OAuth 不允许更新；受管目录不接受重解析点别名。
            var lockPath = StorageLockPath(path, context.SharedTaskRoot, context.AllowTokenWriteback);
            while (fileLock is null)
            {
                token.ThrowIfCancellationRequested();
                try { fileLock = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { await Task.Delay(100, token).ConfigureAwait(false); }
            }
            await RecoverAsync(context.SharedTaskRoot, path, context.TaskDirectory, token, context.AllowTokenWriteback).ConfigureAwait(false);
            var source = RcloneExecutionContext.ReadConfigurationBytes(path);
            var revision = Hash(source);
            if (context.AllowedTokenSections.Length != 0) RequireTrustedSource(path, source);
            if (revision != context.RevisionEvidence)
            {
                if (!AcceptedRevisions.TryGetValue(path, out var accepted) || accepted != revision
                    || !TokenOnlyDifference(context.SourceBytes, source, context.AllowedTokenSections))
                    throw Conflict();
                context.AcceptCredentialRevision(source);
            }
            await AtomicFileService.WriteAsync(context.SnapshotPath, (s, ct) => s.WriteAsync(source, ct).AsTask(), token).ConfigureAwait(false);
            await AtomicFileService.WriteAsync(Path.Combine(context.TaskDirectory, "source.conf"), (s, ct) => s.WriteAsync(source, ct).AsTask(), token).ConfigureAwait(false);
            var attempt = new CredentialRefreshAttempt(1, path, revision, context.ExecutablePath,
                context.AllowedTokenSections, "Prepared", null, null);
            var lease = new RcloneCredentialRefresh(context, gate, fileLock, attempt);
            await lease.SaveAsync(token).ConfigureAwait(false);
            return lease;
        }
        catch (Exception error) { RcloneConnectionDiagnostics.AtStage(error, CredentialFailureStage.Begin, path); fileLock?.Dispose(); gate.Release(); throw; }
    }

    internal async Task StartingAsync(CancellationToken token)
    { _attempt = _attempt with { Phase = "Starting" }; await SaveAsync(token).ConfigureAwait(false); }
    internal async Task StartedAsync(Process process, CancellationToken token)
    {
        _attempt = _attempt with { Phase = "Running", ProcessId = process.Id, StartedUtcTicks = process.StartTime.ToUniversalTime().Ticks };
        await SaveAsync(token).ConfigureAwait(false);
    }
    internal async Task CompleteAsync(bool interrupted = false)
    {
        try { await CompleteCoreAsync(interrupted).ConfigureAwait(false); }
        catch (JsonException)
        {
            var error = EvidenceInvalid(_context.ConfigPath);
            RcloneConnectionDiagnostics.AtStage(error, CredentialFailureStage.Complete, _context.ConfigPath); throw error;
        }
        catch (Exception error) { RcloneConnectionDiagnostics.AtStage(error, CredentialFailureStage.Complete, _context.ConfigPath); throw; }
    }
    private async Task CompleteCoreAsync(bool interrupted)
    {
        // 子进程已结束之后的 finally 必须处理刷新，即使命令失败或被取消也不丢新 Token。
        _attempt = _attempt with { Phase = interrupted && _attempt.TokenSections.Length != 0 ? "Interrupted" : "Exited" };
        await SaveAsync(CancellationToken.None).ConfigureAwait(false);
        if (_attempt.Phase == "Interrupted" && _context.SourceBytes.AsSpan().SequenceEqual(RcloneExecutionContext.ReadConfigurationBytes(_context.SnapshotPath)))
            throw Conflict(); // 服务器可能已轮换但新值尚未落盘，必须重新授权。
        var bytes = Merge(_attempt, _context.TaskDirectory, _context.AllowTokenWriteback);
        _context.AcceptCredentialRevision(bytes);
        AcceptedRevisions[_context.ConfigPath] = Hash(bytes);
        File.Delete(_attemptPath); File.Delete(Path.Combine(_context.TaskDirectory, "source.conf"));
        _completed = true;
        UnsettledSources.TryRemove(_context.ConfigPath, out _);
    }
    private Task SaveAsync(CancellationToken token) => AtomicFileService.WriteAsync(_attemptPath,
        (s, ct) => JsonSerializer.SerializeAsync(s, _attempt, CredentialRefreshJsonContext.Default.CredentialRefreshAttempt, ct), token);

    internal static void RegisterSnapshot(string directory, string source, string revision)
        => AtomicFileService.Write(Path.Combine(directory, "snapshot.json"), stream => JsonSerializer.Serialize(stream,
            new CredentialSnapshotRegistration(1, source, revision), CredentialRefreshJsonContext.Default.CredentialSnapshotRegistration));

    internal static void RecoverIdleSnapshot(string directory, string source)
    {
        // The owner may finish cleanup between enumeration and acquiring ownership.
        // Missing payload after acquiring the lock proves there are no unique bytes to recover.
        if (!File.Exists(Path.Combine(directory, "rclone.conf"))) return;
        var registration = Path.Combine(directory, "snapshot.json");
        if (!File.Exists(registration)) throw RcloneConnectionDiagnostics.CredentialFailure(CredentialFailureReason.UnattributedSnapshot, CredentialFailureStage.Recover, source);
        RejectReparsePoints(registration);
        var recorded = JsonSerializer.Deserialize(RcloneCredentialStore.ReadMetadata(registration), CredentialRefreshJsonContext.Default.CredentialSnapshotRegistration);
        if (recorded is null || recorded.Version != 1 || recorded.SourceRevision is not { Length: 64 }
            || !recorded.SourceRevision.All(Uri.IsHexDigit)) throw EvidenceInvalid(source);
        if (!StringComparer.OrdinalIgnoreCase.Equals(recorded.SourcePath, source)) return;
        var snapshot = Path.Combine(directory, "rclone.conf");
        RejectReparsePoints(snapshot); RejectHardLinks(snapshot);
        var bytes = RcloneExecutionContext.ReadConfigurationBytes(snapshot);
        var current = RcloneExecutionContext.ReadConfigurationBytes(source);
        // No Attempt means no command can be running: Begin persists it before starting a child.
        // Only discard bytes already present in the source or a proven ancestor of its token ledger.
        if (!bytes.AsSpan().SequenceEqual(current) && (Hash(bytes) != recorded.SourceRevision
            || !AcceptsFrozenRevision(source, recorded.SourceRevision, current))) throw Conflict();
        File.Delete(snapshot);
        File.Delete(Path.Combine(directory, "source.conf"));
        File.Delete(registration);
    }

    internal static Task RecoverAsync(string root, string sourcePath, string currentDirectory, CancellationToken token, bool allowWriteback = true)
        => Task.Run(() => Recover(root, sourcePath, currentDirectory, token, allowWriteback), token);
    private static void Recover(string root, string sourcePath, string currentDirectory, CancellationToken token, bool allowWriteback)
    {
        _ = RcloneCredentialStore.HasFreshOrigin(sourcePath, allowWriteback);
        var unattributedLegacy = false;
        foreach (var (directory, legacy) in RcloneCredentialStore.Enumerate(root, sourcePath))
        {
            token.ThrowIfCancellationRequested();
            if (StringComparer.OrdinalIgnoreCase.Equals(directory, currentDirectory)) continue;
            var owner = RcloneCredentialStore.Owner(directory);
            if (owner is not null && !StringComparer.OrdinalIgnoreCase.Equals(owner, sourcePath))
            {
                if (legacy) continue;
                throw EvidenceInvalid(sourcePath);
            }
            if (owner is null)
            {
                var unsafeDirectory = (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0;
                if (!unsafeDirectory && !File.Exists(Path.Combine(directory, "rclone.conf")) && !File.Exists(Path.Combine(directory, "attempt.json"))) continue;
                if (legacy) { unattributedLegacy = true; continue; }
                throw RcloneConnectionDiagnostics.CredentialFailure(legacy ? CredentialFailureReason.UnattributedSnapshot : CredentialFailureReason.InvalidEvidence,
                    CredentialFailureStage.Recover, sourcePath);
            }
            try
            {
                RejectReparsePoints(directory);
                var attemptPath = Path.Combine(directory, "attempt.json");
                if (!File.Exists(attemptPath))
                {
                    if (File.Exists(Path.Combine(directory, "rclone.conf")))
                    {
                        var marker = Path.Combine(directory, "ownership.lock");
                        if (!File.Exists(marker)) throw EvidenceInvalid(sourcePath);
                        RejectReparsePoints(marker);
                        FileStream ownership;
                        try
                        {
                            ownership = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                        }
                        catch (IOException error)
                        {
                            if (!File.Exists(Path.Combine(directory, "rclone.conf")) || (error.HResult & 0xffff) is 32 or 33) continue;
                            throw;
                        }
                        using (ownership)
                        {
                            if (OperatingSystem.IsWindows()) RequireSingleLink(marker, InspectFile(ownership.SafeFileHandle));
                            RecoverIdleSnapshot(directory, sourcePath);
                        }
                    }
                    continue;
                }
                RejectReparsePoints(attemptPath); RejectHardLinks(attemptPath);
                var attempt = JsonSerializer.Deserialize(RcloneCredentialStore.ReadMetadata(attemptPath),
                    CredentialRefreshJsonContext.Default.CredentialRefreshAttempt) ?? throw EvidenceInvalid(sourcePath);
                if (attempt.Version != 1 || attempt.SourceRevision is not { Length: 64 } || attempt.TokenSections is null
                    || attempt.TokenSections.Length > 100 || !Path.IsPathFullyQualified(attempt.ExecutablePath)
                    || attempt.Phase is not ("Prepared" or "Starting" or "Running" or "Interrupted" or "Exited")) throw EvidenceInvalid(sourcePath);
                if (attempt.Phase is "Starting" or "Interrupted") throw Conflict();
                if (attempt.Phase == "Running")
                {
                    if (attempt.ProcessId is not { } pid || pid <= 0 || attempt.StartedUtcTicks is not { } ticks) throw EvidenceInvalid(sourcePath);
                    try
                    {
                        using var process = Process.GetProcessById(pid);
                        // PID 复用不能证明旧子进程已退出；进程仍存在时停止新刷新者。
                        if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == ticks) throw Conflict();
                    }
                    catch (ArgumentException) { }
                }
                var recovered = Merge(attempt, directory, allowWriteback);
                AcceptedRevisions[sourcePath] = Hash(recovered);
                File.Delete(attemptPath); File.Delete(Path.Combine(directory, "source.conf"));
                File.Delete(Path.Combine(directory, "rclone.conf"));
                File.Delete(Path.Combine(directory, "snapshot.json"));
                UnsettledSources.TryRemove(sourcePath, out _);
                // 保留快照目录给正常清理；这里只处理明确的刷新证据。
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            { throw EvidenceInvalid(sourcePath); }
            catch (Exception error) { RcloneConnectionDiagnostics.AtStage(error, CredentialFailureStage.Recover, sourcePath); throw; }
        }
        // Replay may complete a ledger interrupted after source writeback. Use the recovered revision here.
        if (unattributedLegacy && !RcloneCredentialStore.HasFreshOrigin(sourcePath, allowWriteback))
            throw RcloneConnectionDiagnostics.CredentialFailure(CredentialFailureReason.UnattributedSnapshot, CredentialFailureStage.Recover, sourcePath);
    }

    private static byte[] Merge(CredentialRefreshAttempt attempt, string directory, bool allowWriteback)
    {
        foreach (var file in new[] { Path.Combine(directory, "source.conf"), Path.Combine(directory, "rclone.conf"), attempt.SourcePath })
        { RejectReparsePoints(file); RejectHardLinks(file); }
        var original = RcloneExecutionContext.ReadConfigurationBytes(Path.Combine(directory, "source.conf"));
        if (Hash(original) != attempt.SourceRevision) throw EvidenceInvalid(attempt.SourcePath);
        var refreshed = RcloneExecutionContext.ReadConfigurationBytes(Path.Combine(directory, "rclone.conf"));
        var current = RcloneExecutionContext.ReadConfigurationBytes(attempt.SourcePath);
        if (attempt.TokenSections.Length != 0) RequireTrustedSource(attempt.SourcePath, original, requireCurrent: false);
        if (original.AsSpan().SequenceEqual(refreshed))
        {
            if (Hash(current) != attempt.SourceRevision) throw Conflict();
            return current;
        }
        if (!allowWriteback || !TokenOnlyDifference(original, refreshed, attempt.TokenSections)) throw Conflict();
        var merged = MergeTokens(original, refreshed);
        // 写回成功但终态记录尚未删除时再次回放必须幂等。
        if (current.AsSpan().SequenceEqual(merged))
        {
            RecordTrustedRefresh(attempt.SourcePath, original, current);
            return current;
        }
        if (Hash(current) != attempt.SourceRevision) throw Conflict();
        RejectReparsePoints(attempt.SourcePath);
        AtomicFileService.Write(attempt.SourcePath, s => s.Write(merged));
        RcloneExecutionContext.ProtectConfigurationFile(attempt.SourcePath);
        var verified = RcloneExecutionContext.ReadConfigurationBytes(attempt.SourcePath);
        if (!verified.AsSpan().SequenceEqual(merged)) throw Conflict();
        RecordTrustedRefresh(attempt.SourcePath, original, verified);
        return verified;
    }

    private static string LedgerPath(string source) => source + ".folderrewind-revisions.json";
    internal static bool HasPendingEvidence(string root, string source, bool managed = false)
    {
        if (!Directory.Exists(root)) return false;
        try
        {
            var fresh = RcloneCredentialStore.HasFreshOrigin(source, managed);
            foreach (var (directory, legacy) in RcloneCredentialStore.Enumerate(root, source))
            {
                var owner = RcloneCredentialStore.Owner(directory);
                if (owner is not null && !StringComparer.OrdinalIgnoreCase.Equals(owner, Path.GetFullPath(source))) { if (legacy) continue; return true; }
                if (owner is null && legacy && fresh) continue;
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0
                    || File.Exists(Path.Combine(directory, "attempt.json")) || File.Exists(Path.Combine(directory, "rclone.conf"))) return true;
            }
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException) { return true; }
    }
    private static CredentialRevisionLedger? ReadLedger(string source)
    {
        var path = LedgerPath(source);
        if (!File.Exists(path)) return null;
        RejectReparsePoints(path);
        RejectHardLinks(path);
        var ledger = ReadRevisionLedger(path, source);
        if (ledger.Version != 1 || ledger.CurrentRevision is not { Length: 64 } || ledger.TargetRevision is not { Length: 64 }
            || ledger.Revisions is null || ledger.Revisions.Length == 0 || ledger.Revisions.Length > 1000
            || ledger.Revisions.Any(r => r is not { Length: 64 }) || ledger.Revisions[^1] != ledger.CurrentRevision) throw EvidenceInvalid(source);
        return ledger;
    }
    private static CredentialRevisionLedger ReadRevisionLedger(string path, string source)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 128 * 1024) throw EvidenceInvalid(source);
            return JsonSerializer.Deserialize(stream, CredentialRefreshJsonContext.Default.CredentialRevisionLedger) ?? throw EvidenceInvalid(source);
        }
        catch (JsonException) { throw EvidenceInvalid(source); }
    }
    internal static bool AcceptsFrozenRevision(string source, string frozenRevision, byte[] current)
    {
        var revision = Hash(current);
        if (revision == frozenRevision) return true;
        var ledger = ReadLedger(source);
        return ledger is not null && ledger.CurrentRevision == revision && ledger.TargetRevision == TargetRevision(current)
            && ledger.Revisions.Contains(frozenRevision, StringComparer.Ordinal);
    }
    private static void RequireTrustedSource(string source, byte[] bytes, bool requireCurrent = true)
    {
        var revision = Hash(bytes); var ledger = ReadLedger(source);
        if (ledger is null)
        {
            SaveLedger(source, new(1, revision, TargetRevision(bytes), [revision])); return;
        }
        if (ledger.TargetRevision != TargetRevision(bytes) || !ledger.Revisions.Contains(revision, StringComparer.Ordinal)
            || requireCurrent && ledger.CurrentRevision != revision) throw Conflict();
    }
    private static void RecordTrustedRefresh(string source, byte[] original, byte[] refreshed)
    {
        var ledger = ReadLedger(source) ?? throw Conflict();
        var revision = Hash(refreshed);
        if (ledger.CurrentRevision == revision) return;
        if (ledger.CurrentRevision != Hash(original) || ledger.TargetRevision != TargetRevision(refreshed) || ledger.Revisions.Length >= 1000) throw Conflict();
        SaveLedger(source, ledger with { CurrentRevision = revision, Revisions = [.. ledger.Revisions, revision] });
    }
    private static void SaveLedger(string source, CredentialRevisionLedger ledger)
    {
        AtomicFileService.Write(LedgerPath(source), s => JsonSerializer.Serialize(s, ledger, CredentialRefreshJsonContext.Default.CredentialRevisionLedger));
        RcloneExecutionContext.ProtectConfigurationFile(LedgerPath(source));
    }

    private static byte[] MergeTokens(byte[] original, byte[] refreshed)
    {
        var values = Parse(refreshed);
        var lines = Encoding.UTF8.GetString(original).Split('\n'); string? section = null;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim().TrimStart('\uFEFF');
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
            var equals = line.IndexOf('=');
            if (equals > 0 && line[..equals].Trim() == "token" && section is not null)
            {
                var rawEquals = lines[i].IndexOf('=');
                lines[i] = lines[i][..(rawEquals + 1)] + " " + values[section + "\0token"] + (lines[i].EndsWith('\r') ? "\r" : "");
            }
        }
        return Encoding.UTF8.GetBytes(string.Join('\n', lines));
    }

    internal static string[] AuthenticationSections(byte[] bytes, string remote)
    {
        var values = Parse(bytes); var visited = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        while (visited.Add(remote) && visited.Count <= 100)
        {
            if (!values.TryGetValue(remote + "\0type", out var type)) throw ConfigurationInvalid();
            if (values.ContainsKey(remote + "\0token"))
            {
                if (type is not ("drive" or "onedrive" or "box" or "dropbox" or "pcloud" or "yandex" or "google photos"))
                    throw RcloneConnectionDiagnostics.InvalidFailure("connection.backend-refresh-unsupported", "CloudConnection_UnsupportedRefreshBackend");
                result.Add(remote);
            }
            if (type is not ("alias" or "crypt")) return result.ToArray();
            if (!values.TryGetValue(remote + "\0remote", out var wrapped)) throw ConfigurationInvalid();
            if (!wrapped.Contains(':')) return result.ToArray();
            remote = CloudRemotePath.Split(wrapped).Remote;
        }
        throw ConfigurationInvalid();
    }
    internal static bool TokenOnlyDifference(byte[] original, byte[] refreshed, string[] sections)
    {
        var before = Parse(original); var after = Parse(refreshed);
        if (before.Count != after.Count) return false;
        foreach (var (key, value) in before)
        {
            if (!after.TryGetValue(key, out var updated)) return false;
            if (value == updated) continue;
            var parts = key.Split('\0');
            if (parts[1] != "token" || !sections.Contains(parts[0], StringComparer.Ordinal)) return false;
            using var json = JsonDocument.Parse(updated);
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("access_token", out var access)
                || access.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(access.GetString())) return false;
        }
        return true;
    }
    private static Dictionary<string, string> Parse(byte[] content)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal); string? section = null;
        var lineNumber = 0;
        foreach (var raw in Encoding.UTF8.GetString(content).TrimStart('\uFEFF').Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim(); if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
            var equals = line.IndexOf('=');
            if (section is null || equals <= 0 || !values.TryAdd(section + "\0" + line[..equals].Trim(), line[(equals + 1)..].Trim())) throw ConfigurationInvalid(lineNumber);
        }
        return values;
    }
    internal static void RejectReparsePoints(string path)
    {
        var depth = 0;
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current), depth++)
            if (File.Exists(current) || Directory.Exists(current))
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw RcloneConnectionDiagnostics.CredentialFailure(CredentialFailureReason.ReparsePoint, CredentialFailureStage.ValidatePath,
                        path, depth, (int)attributes);
            }
    }
    internal static void RejectHardLinks(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        RequireSingleLink(path, InspectFile(path));
    }
    internal static void RequireSingleLink(string path, CredentialFileInformation information)
    {
        if (!information.Succeeded || information.LinkCount == 0)
            throw RcloneConnectionDiagnostics.CredentialFailure(CredentialFailureReason.MetadataUnavailable, CredentialFailureStage.InspectFile,
                path, links: information.Succeeded ? information.LinkCount : null, win32: information.Win32Error);
        if (information.LinkCount != 1)
            throw RcloneConnectionDiagnostics.CredentialFailure(CredentialFailureReason.MultipleLinks, CredentialFailureStage.InspectFile, path, links: information.LinkCount);
    }
    private static CredentialFileInformation InspectFile(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return InspectFile(handle);
    }
    private static CredentialFileInformation InspectFile(SafeFileHandle handle)
    {
        var succeeded = GetFileInformationByHandle(handle, out var info);
        var error = Marshal.GetLastWin32Error();
        return new(succeeded, info.NumberOfLinks, succeeded ? 0 : error);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint VolumeSerial, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static string TargetRevision(byte[] bytes)
        => Hash(Encoding.UTF8.GetBytes(string.Join("\n", Parse(bytes).Where(p => !p.Key.EndsWith("\0token", StringComparison.Ordinal))
            .OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value))));
    private static InvalidOperationException Conflict() => RcloneConnectionDiagnostics.CredentialFailure(CredentialFailureReason.RefreshConflict, CredentialFailureStage.Merge);
    private static InvalidOperationException EvidenceInvalid(string source)
        => RcloneConnectionDiagnostics.CredentialFailure(CredentialFailureReason.InvalidEvidence, CredentialFailureStage.Recover, source);
    private static InvalidOperationException ConfigurationInvalid(int? line = null)
        => RcloneConnectionDiagnostics.CredentialFailure(CredentialFailureReason.InvalidConfiguration, CredentialFailureStage.ParseConfiguration, line: line);
    public ValueTask DisposeAsync()
    {
        // 未完成的证据与快照必须保留，供下一次访问前回放或诊断。
        if (!_completed) { _context.PreserveSnapshot = true; UnsettledSources[_context.ConfigPath] = true; }
        _fileLock.Dispose(); _gate.Release(); return ValueTask.CompletedTask;
    }
}

internal sealed record CredentialRefreshAttempt(int Version, string SourcePath, string SourceRevision, string ExecutablePath,
    string[] TokenSections, string Phase, int? ProcessId, long? StartedUtcTicks);
internal sealed record CredentialRevisionLedger(int Version, string CurrentRevision, string TargetRevision, string[] Revisions);
internal sealed record CredentialSnapshotRegistration(int Version, string SourcePath, string SourceRevision);
[JsonSerializable(typeof(CredentialRefreshAttempt))]
[JsonSerializable(typeof(CredentialRevisionLedger))]
[JsonSerializable(typeof(CredentialSnapshotRegistration))]
[JsonSerializable(typeof(CredentialOrigin))]
internal partial class CredentialRefreshJsonContext : JsonSerializerContext;

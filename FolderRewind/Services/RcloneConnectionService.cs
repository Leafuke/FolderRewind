using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

public sealed record RcloneRemoteOption(string Name, string Backend);
public sealed record RcloneRemoteCatalog(IReadOnlyList<RcloneRemoteOption> Remotes, string RevisionEvidence);
public sealed record RemoteDirectoryResult(IReadOnlyList<string> Names, bool Truncated, int NextOffset = -1);
public sealed record ConnectionProbeResult(bool ReadWriteVerified, bool Canceled, string Diagnostic, string? RetainedObject);

public sealed class OwnedRcloneConnection(string configPath, string remoteRoot) : IDisposable
{
    public string ConfigPath { get; } = configPath;
    public string RemoteRoot { get; } = remoteRoot;
    private bool _committed;
    public void Commit() => _committed = true;
    public void Dispose()
    {
        if (_committed) return;
        // OAuth 可能已经轮换；取消向导也不能删除受管目录中的唯一新令牌。
        if (RcloneExecutionContext.HasActiveCredential(ConfigPath) || RcloneCredentialRefresh.MustPreserveOwnedCredentials(ConfigPath)) return;
        File.Delete(ConfigPath);
        File.Delete(ConfigPath + ".folderrewind.lock");
        Directory.Delete(Path.GetDirectoryName(ConfigPath)!);
    }
}

public static class RcloneConnectionService
{
    public static Uri ValidateWebDavUrl(string value, bool allowInsecure = false)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0
            || value.Contains('\r') || value.Contains('\n') || (uri.Scheme == "http" && !uri.IsLoopback && !allowInsecure))
            throw new ArgumentException(I18n.GetString("CloudSetup_InvalidUrl"));
        return uri;
    }

    public static IReadOnlyList<RcloneRemoteOption> ListRemotes(string configPath)
        => InspectRemotes(configPath).Remotes;

    public static RcloneRemoteCatalog InspectRemotes(string configPath)
    {
        byte[] bytes;
        try { bytes = RcloneExecutionContext.ReadConfigurationBytes(configPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw RcloneConnectionDiagnostics.Failure("cloud.source-config-unavailable", "CloudSetup_SourceConfigUnavailable", ex);
        }
        var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        if (text.Contains("RCLONE_ENCRYPT_V", StringComparison.Ordinal)) throw new InvalidOperationException(I18n.GetString("CloudSetup_UnlockRequired"));
        var result = new List<RcloneRemoteOption>();
        string? section = null; string backend = "";
        foreach (var raw in text.Split(['\r', '\n']))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (section is not null) result.Add(new(section, backend));
                section = line[1..^1]; backend = "";
                if (result.Count >= OnboardingOperationBudgets.RemoteInteraction) break;
            }
            else if (line.StartsWith("type", StringComparison.OrdinalIgnoreCase) && line.IndexOf('=') is var equals && equals >= 0
                && line[..equals].Trim().Equals("type", StringComparison.OrdinalIgnoreCase)) backend = line[(equals + 1)..].Trim();
        }
        if (section is not null) result.Add(new(section, backend));
        return new(result.Take(OnboardingOperationBudgets.RemoteInteraction).ToArray(), Convert.ToHexString(SHA256.HashData(bytes)));
    }

    public static async Task<OwnedRcloneConnection> CreateWebDavAsync(string executable, string credentialsRoot, string url, string user, string password,
        bool allowInsecure = false, CancellationToken token = default)
    {
        var endpoint = ValidateWebDavUrl(url, allowInsecure);
        if (user.IndexOfAny(['\r', '\n', '\0']) >= 0 || password.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException(I18n.GetString("CloudSetup_InvalidCredential"));
        var start = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("RCLONE_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.ArgumentList.Add("obscure"); start.ArgumentList.Add("-");
        var output = await RunAsync(start, TimeSpan.FromSeconds(15), token, password + "\n").ConfigureAwait(false);
        var obscured = output.TrimEnd('\r', '\n');
        if (obscured.IndexOfAny(['\r', '\n', '\0']) >= 0 || obscured.Length == 0) throw new IOException(I18n.GetString("CloudSetup_ObscureFailed"));
        if (!Path.IsPathFullyQualified(credentialsRoot)) throw new ArgumentException("Credentials directory must be absolute.", nameof(credentialsRoot));
        var directory = Path.Combine(Path.GetFullPath(credentialsRoot), "connection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        RcloneExecutionContext.CreatePrivateDirectory(directory);
        var path = Path.Combine(directory, "rclone.conf");
        var name = "fr_" + Guid.NewGuid().ToString("N");
        try
        {
            var content = Encoding.UTF8.GetBytes($"[{name}]\ntype = webdav\nurl = {endpoint.AbsoluteUri}\nvendor = other\nuser = {user}\npass = {obscured}\n");
            await AtomicFileService.WriteAsync(path, (stream, ct) => stream.WriteAsync(content, ct).AsTask(), token).ConfigureAwait(false);
            return new(path, name + ":");
        }
        catch { if (File.Exists(path)) File.Delete(path); Directory.Delete(directory); throw; }
    }

    public static async Task<RemoteDirectoryResult> BrowseAsync(RcloneExecutionContext context, CancellationToken token = default, int offset = 0)
    {
        var output = await RunAsync(context.CreateStartInfo(["lsjson", context.RemoteRoot, "--dirs-only", "--max-depth", "1"]),
            OnboardingOperationBudgets.RemoteBrowse, token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(output);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new IOException(I18n.GetString("CloudSetup_BrowseFailed"));
        var names = document.RootElement.EnumerateArray().Take(OnboardingOperationBudgets.RemoteInteraction + 1)
            .Select(entry => entry.GetProperty("Name").GetString() ?? "").ToArray();
        return PageDirectories(names, offset);
    }

    internal static RemoteDirectoryResult PageDirectories(IReadOnlyList<string> names, int offset)
    {
        if (offset < 0 || offset >= OnboardingOperationBudgets.RemoteInteraction) throw new ArgumentOutOfRangeException(nameof(offset));
        var available = Math.Min(names.Count, OnboardingOperationBudgets.RemoteInteraction);
        var count = Math.Min(OnboardingOperationBudgets.RemoteBatch, Math.Max(0, available - offset));
        var next = offset + count < available ? offset + count : -1;
        return new(names.Skip(offset).Take(count).ToArray(), names.Count > offset + count, next);
    }

    public static async Task<ConnectionProbeResult> VerifyWriteAsync(RcloneExecutionContext context, CancellationToken token = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(OnboardingOperationBudgets.ConnectionProbe);
        var directory = Path.Combine(Path.GetTempPath(), "FolderRewind-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "input"); var output = Path.Combine(directory, "output");
        var remote = CloudRemotePath.Append(context.RemoteRoot, ".folderrewind-probe-" + Guid.NewGuid().ToString("N"));
        bool attempted = false, verified = false;
        string diagnostic = ""; bool canceled = false;
        try
        {
            var bytes = RandomNumberGenerator.GetBytes(8192);
            await File.WriteAllBytesAsync(input, bytes, budget.Token).ConfigureAwait(false);
            attempted = true;
            await RunAsync(context.CreateStartInfo(["copyto", input, remote, "--immutable"]), OnboardingOperationBudgets.ConnectionProbe, budget.Token).ConfigureAwait(false);
            await RunAsync(context.CreateStartInfo(["copyto", remote, output]), OnboardingOperationBudgets.ConnectionProbe, budget.Token).ConfigureAwait(false);
            var downloaded = await File.ReadAllBytesAsync(output, budget.Token).ConfigureAwait(false);
            verified = bytes.AsSpan().SequenceEqual(downloaded);
            diagnostic = I18n.GetString(verified ? "CloudSetup_ReadWriteVerified" : "CloudSetup_ProbeMismatch");
        }
        catch (OperationCanceledException) { canceled = true; diagnostic = I18n.GetString("Common_Canceled"); }
        catch (Exception ex) { diagnostic = CloudCommandSecurity.Redact(ex.Message); }
        string? retained = attempted ? remote : null;
        try
        {
            if (verified)
            {
                // 仅在独占随机对象内容已读回核验后删除这一确切对象。
                using var cleanup = new CancellationTokenSource(OnboardingOperationBudgets.ProbeCleanup);
                await RunAsync(context.CreateStartInfo(["deletefile", remote]), OnboardingOperationBudgets.ProbeCleanup, cleanup.Token).ConfigureAwait(false);
                retained = null;
            }
        }
        catch { retained = remote; }
        finally { File.Delete(input); File.Delete(output); Directory.Delete(directory); }
        return new(verified, canceled, diagnostic, retained);
    }

    internal static async Task<string> RunAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken token, string? stdin = null)
    {
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        if (start.RedirectStandardInput) start.StandardInputEncoding = new UTF8Encoding(false);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(timeout);
        await using var credentials = await RcloneCredentialRefresh.BeginAsync(start, budget.Token).ConfigureAwait(false);
        using var process = new Process { StartInfo = start };
        if (credentials is not null) await credentials.StartingAsync(budget.Token).ConfigureAwait(false);
        if (!process.Start()) throw new IOException(I18n.GetString("CloudSync_Error_StartFailed"));
        var output = ReadBoundedAsync(process.StandardOutput, process, budget.Token);
        var error = ReadBoundedAsync(process.StandardError, process, budget.Token);
        try
        {
            if (credentials is not null) await credentials.StartedAsync(process, budget.Token).ConfigureAwait(false);
            if (stdin is not null) { await process.StandardInput.WriteAsync(stdin.AsMemory(), budget.Token).ConfigureAwait(false); process.StandardInput.Close(); }
            await process.WaitForExitAsync(budget.Token).ConfigureAwait(false);
            var result = await output.ConfigureAwait(false); var failure = await error.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new RcloneProcessException(process.ExitCode, stdin is null ? RcloneConnectionDiagnostics.ProcessMessage(start, failure) : I18n.GetString("CloudSetup_ObscureFailed"));
            return result;
        }
        finally
        {
            var interrupted = !process.HasExited;
            if (interrupted) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); }
            try { await Task.WhenAll(output, error).ConfigureAwait(false); } catch { }
            if (credentials is not null) await credentials.CompleteAsync(interrupted).ConfigureAwait(false);
        }
    }
    internal static async Task<byte[]> RunBytesAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken token, int maximumBytes)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(timeout);
        token.ThrowIfCancellationRequested();
        await using var credentials = await RcloneCredentialRefresh.BeginAsync(start, budget.Token).ConfigureAwait(false);
        using var process = new Process { StartInfo = start };
        if (credentials is not null) await credentials.StartingAsync(budget.Token).ConfigureAwait(false);
        if (!process.Start()) throw new IOException(I18n.GetString("CloudSync_Error_StartFailed"));
        var error = ReadBoundedAsync(process.StandardError, process, budget.Token);
        try
        {
            if (credentials is not null) await credentials.StartedAsync(process, budget.Token).ConfigureAwait(false);
            using var output = new MemoryStream(); var buffer = new byte[65536]; int count;
            while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer.AsMemory(), budget.Token).ConfigureAwait(false)) != 0)
            {
                if (output.Length + count > maximumBytes) throw new IOException(I18n.GetString("CloudSetup_OutputTruncated"));
                output.Write(buffer, 0, count);
            }
            await process.WaitForExitAsync(budget.Token).ConfigureAwait(false);
            var failure = await error.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new RcloneProcessException(process.ExitCode, RcloneConnectionDiagnostics.ProcessMessage(start, failure));
            return output.ToArray();
        }
        finally
        {
            var interrupted = !process.HasExited;
            if (interrupted) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); }
            try { await error.ConfigureAwait(false); } catch { }
            if (credentials is not null) await credentials.CompleteAsync(interrupted).ConfigureAwait(false);
        }
    }
    internal static async Task RunToFileAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken token,
        string path, long expectedLength, FolderRewind.Plugin.Runtime.Artifacts.ArtifactTransferBudget transferBudget)
    {
        using var space = FolderRewind.Plugin.Runtime.Artifacts.ArtifactSpaceBudget.Shared.Reserve(new FolderRewind.Plugin.Runtime.Artifacts.ArtifactSpaceRequest(path, expectedLength));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(timeout);
        await using var credentials = await RcloneCredentialRefresh.BeginAsync(start, deadline.Token).ConfigureAwait(false);
        using var process = new Process { StartInfo = start };
        if (credentials is not null) await credentials.StartingAsync(deadline.Token).ConfigureAwait(false);
        if (!process.Start()) throw new IOException(I18n.GetString("CloudSync_Error_StartFailed"));
        var error = ReadBoundedAsync(process.StandardError, process, deadline.Token);
        var completed = false; var created = false;
        try
        {
            if (credentials is not null) await credentials.StartedAsync(process, deadline.Token).ConfigureAwait(false);
            if (start.RedirectStandardInput) process.StandardInput.Close();
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                created = true;
                await transferBudget.CopyAsync(process.StandardOutput.BaseStream, output, expectedLength, deadline.Token).ConfigureAwait(false);
            }
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var failure = await error.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new RcloneProcessException(process.ExitCode, RcloneConnectionDiagnostics.ProcessMessage(start, failure));
            completed = true;
        }
        finally
        {
            var interrupted = !process.HasExited;
            if (interrupted) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); }
            try { await error.ConfigureAwait(false); } catch { }
            if (!completed && created && File.Exists(path)) File.Delete(path);
            if (credentials is not null) await credentials.CompleteAsync(interrupted).ConfigureAwait(false);
        }
    }
    internal static async Task RunLinesAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken token, Action<string> onLine)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(timeout);
        await using var credentials = await RcloneCredentialRefresh.BeginAsync(start, budget.Token).ConfigureAwait(false);
        using var process = new Process { StartInfo = start };
        if (credentials is not null) await credentials.StartingAsync(budget.Token).ConfigureAwait(false);
        if (!process.Start()) throw new IOException(I18n.GetString("CloudSync_Error_StartFailed"));
        var error = ReadBoundedAsync(process.StandardError, process, budget.Token);
        try
        {
            if (credentials is not null) await credentials.StartedAsync(process, budget.Token).ConfigureAwait(false);
            var buffer = new char[8192]; var line = new StringBuilder(); long total = 0; int count;
            while ((count = await process.StandardOutput.ReadAsync(buffer.AsMemory(), budget.Token).ConfigureAwait(false)) != 0)
            {
                total += count;
                if (total > 64 * 1024 * 1024) throw new IOException(I18n.GetString("CloudSetup_OutputTruncated"));
                foreach (var value in buffer.AsSpan(0, count))
                {
                    if (value == '\n') { if (line.Length != 0) onLine(line.ToString().TrimEnd('\r')); line.Clear(); }
                    else
                    {
                        if (line.Length >= 8192) throw new IOException(I18n.GetString("CloudSetup_OutputTruncated"));
                        line.Append(value);
                    }
                }
            }
            if (line.Length != 0) onLine(line.ToString().TrimEnd('\r'));
            await process.WaitForExitAsync(budget.Token).ConfigureAwait(false);
            var failure = await error.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new RcloneProcessException(process.ExitCode, RcloneConnectionDiagnostics.ProcessMessage(start, failure));
        }
        finally
        {
            var interrupted = !process.HasExited;
            if (interrupted) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); }
            try { await error.ConfigureAwait(false); } catch { }
            if (credentials is not null) await credentials.CompleteAsync(interrupted).ConfigureAwait(false);
        }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, Process process, CancellationToken token)
    {
        var output = new StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > 256 * 1024)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw new IOException(I18n.GetString("CloudSetup_OutputTruncated"));
            }
            output.Append(buffer, 0, count);
        }
        return output.ToString();
    }
}

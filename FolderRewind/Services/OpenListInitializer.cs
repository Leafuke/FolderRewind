using FolderRewind.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

// Only provisions a fresh, private directory. It never starts a server or resets an existing account.
internal static class OpenListInitializer
{
    internal static bool Supports(string version) => Regex.IsMatch(version, @"\bv?4\.2\.6\b")
        && version.Contains("OpenList", StringComparison.OrdinalIgnoreCase);
    internal static async Task VerifySupportedAsync(string executable, CancellationToken token)
    {
        var output = await RcloneConnectionService.RunAsync(Start(executable, Path.GetDirectoryName(executable)!, "version"), TimeSpan.FromSeconds(15), token);
        if (!Supports(output)) throw new InvalidOperationException("[openlist.initialization-version]");
    }
    internal static void RequireAvailablePort(int port)
    {
        try { var listener = new TcpListener(IPAddress.Loopback, port); try { listener.Start(); } finally { listener.Stop(); } }
        catch (SocketException) { throw new InvalidOperationException("[openlist.initialization-port]"); }
    }
    internal static async Task<OpenListRuntimeSettings> InitializeAsync(string executable, string root, string password,
        int port, string[] protectedPaths, CancellationToken token,
        Func<ProcessStartInfo, CancellationToken, Task<string>>? run = null)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable) || !Path.IsPathFullyQualified(root)
            || string.IsNullOrWhiteSpace(password) || password.IndexOfAny(['\r', '\n', '\0']) >= 0 || port < 1024 || port > 65535)
            throw new InvalidOperationException("[openlist.initialization-input]");
        root = Path.GetFullPath(root);
        if (!BackupPathOverlapPolicy.Validate(root, protectedPaths).IsSafe)
            throw new InvalidOperationException("[openlist.initialization-overlap]");
        RcloneCredentialRefresh.RejectReparsePoints(root);
        run ??= (start, ct) => RcloneConnectionService.RunAsync(start, TimeSpan.FromSeconds(60), ct);
        var version = await run(Start(executable, Path.GetDirectoryName(executable)!, "version"), token);
        if (!Supports(version)) throw new InvalidOperationException("[openlist.initialization-version]");
        RequireAvailablePort(port);
        token.ThrowIfCancellationRequested();
        var directory = Path.Combine(root, "instance-" + Guid.NewGuid().ToString("N"));
        RcloneExecutionContext.CreatePrivateDirectory(directory);
        var data = Path.Combine(directory, "data"); Directory.CreateDirectory(data);
        var config = Path.Combine(data, "config.json");
        var marker = Path.Combine(directory, ".folderrewind-initialization.json");
        await AtomicFileService.WriteAsync(marker, (s, ct) => JsonSerializer.SerializeAsync(s,
            new { version = 1, state = "prepared", executable, data, config, port }, cancellationToken: ct), token);
        await AtomicFileService.WriteAsync(config, (s, ct) => JsonSerializer.SerializeAsync(s, new
        {
            force = true,
            scheme = new { address = "127.0.0.1", http_port = port, https_port = -1 },
            database = new { type = "sqlite3", db_file = Path.Combine(data, "data.db") },
            log = new { enable = false },
            s3 = new { enable = false }, ftp = new { enable = false }, sftp = new { enable = false }, mcp = new { enable = false }
        }, cancellationToken: ct), token);
        var start = Start(executable, directory, "admin", "--data", data, "--config", config);
        start.Environment["OPENLIST_ADMIN_PASSWORD"] = password;
        try
        {
            // Console output contains the initial password. Never expose it as a diagnostic.
            _ = await run(start, token);
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(config, token));
            var settings = document.RootElement;
            if (settings.GetProperty("scheme").GetProperty("address").GetString() != "127.0.0.1"
                || settings.GetProperty("scheme").GetProperty("http_port").GetInt32() != port
                || settings.GetProperty("log").GetProperty("enable").GetBoolean()) throw new InvalidDataException();
            var database = settings.GetProperty("database").GetProperty("db_file").GetString();
            if (database is null || Path.GetFullPath(database) != Path.Combine(data, "data.db")) throw new InvalidDataException();
            RcloneCredentialRefresh.RejectReparsePoints(database);
            await using var db = File.OpenRead(database);
            var header = new byte[16]; await db.ReadExactlyAsync(header, token);
            if (db.Length < 100 || Encoding.ASCII.GetString(header) != "SQLite format 3\0") throw new InvalidDataException();
            await AtomicFileService.WriteAsync(marker, (s, ct) => JsonSerializer.SerializeAsync(s,
                new { version = 1, state = "initialized", executable, data, config, port }, cancellationToken: ct), token);
            return new() { ExecutablePath = executable, WorkingDirectory = directory, DataDirectory = data,
                ConfigFilePath = config, ServiceBaseUri = "http://127.0.0.1:" + port, Ownership = OpenListOwnership.External };
        }
        catch (OperationCanceledException) { throw; }
        catch { throw new InvalidOperationException("[openlist.initialization-failed]"); }
        finally { start.Environment.Remove("OPENLIST_ADMIN_PASSWORD"); }
    }
    private static ProcessStartInfo Start(string executable, string working, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = working, UseShellExecute = false,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("OPENLIST_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }
}

using FolderRewind.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

public static partial class ConfigService
{
    private sealed record CredentialReference(BackupConfig Project, CloudSettings Settings, string Remote);
    private sealed record CredentialMigrationResult(string? Target = null, byte[]? Revision = null, Exception? Error = null, bool Uncertain = false);
    private sealed class CredentialMigration(string source, string target, CredentialReference[] references)
    {
        internal readonly string Source = source;
        internal readonly string Target = target;
        internal readonly CredentialReference[] References = references;
        internal readonly TaskCompletionSource<CredentialMigrationResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Started;
        internal volatile bool Confirmed;
        internal volatile CredentialMigrationResult? Result;
    }

    private static readonly ConcurrentDictionary<string, CredentialMigration> CredentialMigrations = new(StringComparer.OrdinalIgnoreCase);
    private static readonly CancellationTokenSource CredentialMigrationCancellation = new();
    private static readonly SemaphoreSlim CredentialMigrationWorkers = new(2, 2);
    private static bool _credentialMigrationsStarted;
    private static bool _credentialMigrationsStopping;

    // Registration only examines loaded settings, never the filesystem or credential bytes.
    private static void RegisterRcloneCredentialMigrations()
    {
        if (IsRecoveryMode || _credentialMigrationsStopping) return;
        var legacyRoot = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FolderRewind", "credentials", "rclone"));
        var targetRoot = Path.GetFullPath(RcloneConnectionsDirectory);
        if (StringComparer.OrdinalIgnoreCase.Equals(legacyRoot, targetRoot)) return;
        var candidates = new Dictionary<string, List<CredentialReference>>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in CurrentConfig.BackupConfigs)
        {
            var settings = project.Cloud;
            if (settings.CommandMode != CloudCommandMode.Rclone || !TryLegacyCredential(settings.RcloneConfigPath, legacyRoot, out var source)) continue;
            if (!candidates.TryGetValue(source, out var references)) candidates[source] = references = [];
            references.Add(new(project, settings, settings.RemoteBasePath));
        }
        foreach (var (source, references) in candidates)
        {
            var target = Path.Combine(targetRoot, Path.GetFileName(Path.GetDirectoryName(source))!, "rclone.conf");
            var migration = CredentialMigrations.GetOrAdd(source, _ => new(source, target, references.ToArray()));
            // The UI transaction publishes the new path before its save completes.
            // Both spellings must share the same readiness/uncertainty barrier.
            CredentialMigrations.TryAdd(target, migration);
            if (_credentialMigrationsStarted) StartCredentialMigration(migration);
        }
    }

    private static bool TryLegacyCredential(string path, string root, out string source)
    {
        source = "";
        if (!Path.IsPathFullyQualified(path)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(full)!;
            var name = Path.GetFileName(directory);
            if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(full), "rclone.conf")
                || !StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(directory), root)
                || name.Length != 43 || !name.StartsWith("connection-", StringComparison.Ordinal)
                || !Guid.TryParseExact(name[11..], "N", out _)) return false;
            source = full;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    internal static void StartRcloneCredentialMigrations()
    {
        _credentialMigrationsStarted = true;
        foreach (var migration in CredentialMigrations.Values.Distinct()) StartCredentialMigration(migration);
    }

    private static void StartCredentialMigration(CredentialMigration migration)
    {
        if (_credentialMigrationsStopping || Interlocked.Exchange(ref migration.Started, 1) != 0) return;
        TaskObserver.Observe(Task.Run(async () =>
        {
            CredentialMigrationResult result;
            try
            {
                await CredentialMigrationWorkers.WaitAsync(CredentialMigrationCancellation.Token).ConfigureAwait(false);
                try { result = await MigrateCredentialAsync(migration, CredentialMigrationCancellation.Token).ConfigureAwait(false); }
                finally { CredentialMigrationWorkers.Release(); }
            }
            catch (OperationCanceledException) { result = new(); }
            catch (Exception ex)
            {
                var uncertain = ex is ConfigEditRollbackException;
                var diagnostic = !uncertain && RcloneConnectionDiagnostics.Code(ex) != "cloud.access-unconfirmed" ? ex
                    : RcloneConnectionDiagnostics.Failure(uncertain ? "cloud.credentials-save-uncertain" : "cloud.credentials-migration-failed",
                        uncertain ? "CloudSetup_CredentialSaveUncertain" : "CloudSetup_CredentialMigrationFailed", ex);
                result = new(Error: diagnostic, Uncertain: uncertain);
            }
            migration.Result = result;
            migration.Completion.TrySetResult(result);
            if (result.Error is { } error) LogService.LogWarning(CloudCommandSecurity.Redact(error.Message), nameof(ConfigService));
        }), nameof(ConfigService));
    }

    private static async Task<CredentialMigrationResult> MigrateCredentialAsync(CredentialMigration migration, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RejectCredentialReparsePoints(migration.Source);
        RejectCredentialReparsePoints(migration.Target);
        byte[] content;
        try { content = RcloneExecutionContext.ReadConfigurationBytes(migration.Source); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw RcloneConnectionDiagnostics.Failure("cloud.source-config-unavailable", "CloudSetup_SourceConfigUnavailable", ex);
        }
        var revision = SHA256.HashData(content);
        var remotes = RcloneConnectionService.InspectRemotes(migration.Source);
        if (remotes.RevisionEvidence != Convert.ToHexString(revision)
            || migration.References.Any(reference => !reference.Remote.Contains(':')
                || !remotes.Remotes.Any(remote => remote.Name == reference.Remote.Split(':', 2)[0])))
            throw RcloneConnectionDiagnostics.InvalidFailure("cloud.remote-mismatch", "CloudSetup_RemoteMismatch");

        var directory = Path.GetDirectoryName(migration.Target)!;
        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        RcloneExecutionContext.CreatePrivateDirectory(directory);
        RejectCredentialReparsePoints(migration.Target);
        if (!File.Exists(migration.Target))
        {
            // AtomicFileService replaces existing destinations. Stage privately, then move
            // without overwrite so a conflicting destination can never be destroyed.
            var staged = Path.Combine(directory, ".migration-" + Guid.NewGuid().ToString("N"));
            try
            {
                await AtomicFileService.WriteAsync(staged, (stream, ct) => stream.WriteAsync(content, ct).AsTask(), token).ConfigureAwait(false);
                RejectCredentialReparsePoints(migration.Target);
                try { File.Move(staged, migration.Target, overwrite: false); }
                catch (IOException) when (File.Exists(migration.Target)) { /* Verify the competing file below. */ }
            }
            finally { if (File.Exists(staged)) File.Delete(staged); }
        }
        var copied = RcloneExecutionContext.ReadConfigurationBytes(migration.Target);
        if (!content.AsSpan().SequenceEqual(copied) || !CryptographicOperations.FixedTimeEquals(revision, SHA256.HashData(copied)))
            throw RcloneConnectionDiagnostics.Failure("cloud.credentials-target-conflict", "CloudSetup_CredentialTargetConflict");
        RejectCredentialReparsePoints(migration.Target);
        RcloneExecutionContext.ProtectConfigurationFile(migration.Target);

        // Hold the checked source and destination against edits while the UI commits only
        // the path. No credential reads or hashes take place on the UI thread.
        RejectCredentialReparsePoints(migration.Source);
        RejectCredentialReparsePoints(migration.Target);
        using var sourceLease = new FileStream(migration.Source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var targetLease = new FileStream(migration.Target, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (sourceLease.Length > 1024 * 1024 || targetLease.Length > 1024 * 1024
            || !CryptographicOperations.FixedTimeEquals(revision, SHA256.HashData(sourceLease))
            || !CryptographicOperations.FixedTimeEquals(revision, SHA256.HashData(targetLease)))
            throw RcloneConnectionDiagnostics.InvalidFailure("cloud.connection-changed", "CloudSetup_ConnectionChanged");
        token.ThrowIfCancellationRequested();
        await UiDispatcherService.RunOnUiAsync(async () =>
        {
            token.ThrowIfCancellationRequested();
            if (IsRecoveryMode) throw new InvalidOperationException(I18n.GetString("Common_Failed"));
            var references = migration.References.Where(reference => CurrentConfig.BackupConfigs.Contains(reference.Project)
                && ReferenceEquals(reference.Project.Cloud, reference.Settings)
                && StringComparer.OrdinalIgnoreCase.Equals(reference.Settings.RcloneConfigPath, migration.Source)
                && reference.Settings.RemoteBasePath == reference.Remote
                && reference.Settings.CommandMode == CloudCommandMode.Rclone).ToArray();
            if (references.Length == 0) return;
            var previous = references.Select(reference => reference.Settings.RcloneConfigPath).ToArray();
            var profiles = CurrentConfig.CloudConnections.Where(p => StringComparer.OrdinalIgnoreCase.Equals(p.CredentialReference, migration.Source)).ToArray();
            await ConfigEditTransaction.ApplyAsync(
                () =>
                {
                    foreach (var reference in references) reference.Settings.RcloneConfigPath = migration.Target;
                    foreach (var profile in profiles) profile.CredentialReference = migration.Target;
                },
                () =>
                {
                    for (var i = 0; i < references.Length; i++)
                        if (references[i].Settings.RcloneConfigPath == migration.Target) references[i].Settings.RcloneConfigPath = previous[i];
                    foreach (var profile in profiles) if (profile.CredentialReference == migration.Target) profile.CredentialReference = migration.Source;
                }, () => SaveAsync(), I18n.GetString("CloudSetup_CredentialMigrationFailed"));
        }).ConfigureAwait(false);
        return new(migration.Target, revision);
    }

    private static void RejectCredentialReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Credential migration refuses reparse points.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static async Task<string> WaitForRcloneCredentialMigrationAsync(string path, CancellationToken token = default, bool allowUncertain = false)
    {
        token.ThrowIfCancellationRequested();
        if (App.IsShuttingDown) throw new OperationCanceledException(I18n.GetString("Common_Canceled"));
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || !CredentialMigrations.TryGetValue(Path.GetFullPath(path), out var migration)) return path;
        var result = await migration.Completion.Task.WaitAsync(token).ConfigureAwait(false);
        if (App.IsShuttingDown) throw new OperationCanceledException(I18n.GetString("Common_Canceled"));
        // An explicit wizard save supersedes this startup migration decision, including
        // a user's deliberate re-selection of the retained original config.
        if (migration.Confirmed) return path;
        if (result.Uncertain && !migration.Confirmed && !allowUncertain) throw result.Error!;
        if (result.Target is null || StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(path), migration.Target)) return path;
        await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            RejectCredentialReparsePoints(migration.Source);
            if (!CryptographicOperations.FixedTimeEquals(result.Revision!, SHA256.HashData(RcloneExecutionContext.ReadConfigurationBytes(migration.Source))))
                throw RcloneConnectionDiagnostics.InvalidFailure("cloud.connection-changed", "CloudSetup_ConnectionChanged");
        }, token).ConfigureAwait(false);
        return result.Target;
    }

    internal static Exception? GetRcloneCredentialMigrationError(string path)
        => Path.IsPathFullyQualified(path) && CredentialMigrations.TryGetValue(Path.GetFullPath(path), out var migration)
            && !migration.Confirmed ? migration.Result?.Error : null;

    internal static void ConfirmRcloneCredentialPath(string path)
    {
        foreach (var migration in CredentialMigrations.Values.Distinct())
            if (StringComparer.OrdinalIgnoreCase.Equals(path, migration.Source) || StringComparer.OrdinalIgnoreCase.Equals(path, migration.Target))
                migration.Confirmed = true;
    }

    internal static async Task StopRcloneCredentialMigrationsAsync()
    {
        _credentialMigrationsStopping = true;
        CredentialMigrationCancellation.Cancel();
        foreach (var migration in CredentialMigrations.Values.Distinct())
            if (Volatile.Read(ref migration.Started) == 0) migration.Completion.TrySetResult(new());
        await Task.WhenAll(CredentialMigrations.Values.Distinct().Select(migration => migration.Completion.Task)).ConfigureAwait(false);
    }
}

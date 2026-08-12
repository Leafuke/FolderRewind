namespace FolderRewind.Plugin.Runtime.Configuration;

public enum ConfigMigrationStage
{
    OriginalRead,
    RecoveryCopyFlushed,
    TemporaryFileFlushed,
    TemporaryFileValidated,
    AtomicReplaceCompleted,
    FormalFileValidated
}

public interface IConfigMigrationObserver
{
    void OnStage(ConfigMigrationStage stage);
}

public enum ConfigFilePreparationStatus
{
    Missing,
    Current,
    Migrated,
    RecoveryRequired
}

public sealed record ConfigRecoveryDiagnostic(
    string Code,
    string Message,
    string ConfigPath,
    string? RecoveryCopyPath = null);

public sealed record ConfigFilePreparationResult(
    ConfigFilePreparationStatus Status,
    byte[]? Utf8Json = null,
    ConfigRecoveryDiagnostic? Diagnostic = null,
    string? RecoveryCopyPath = null,
    IReadOnlyList<string>? Warnings = null)
{
    public bool IsReady => Status is ConfigFilePreparationStatus.Missing
        or ConfigFilePreparationStatus.Current
        or ConfigFilePreparationStatus.Migrated;
}

public sealed class ConfigFileMigrationService
{
    private readonly ConfigDocumentGate _gate;
    private readonly IConfigMigrationObserver? _observer;
    private readonly Func<DateTimeOffset> _utcNow;

    public ConfigFileMigrationService(
        ConfigDocumentGate? gate = null,
        IConfigMigrationObserver? observer = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _gate = gate ?? new ConfigDocumentGate();
        _observer = observer;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public ConfigFilePreparationResult Prepare(string configPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        var fullPath = Path.GetFullPath(configPath);
        if (!File.Exists(fullPath))
        {
            return new ConfigFilePreparationResult(ConfigFilePreparationStatus.Missing);
        }

        byte[] original;
        try
        {
            original = ReadAllBytes(fullPath);
            Observe(ConfigMigrationStage.OriginalRead);
        }
        catch (Exception ex)
        {
            return Recovery(fullPath, "config_read_failed", ex.Message);
        }

        var gateResult = _gate.Prepare(original);
        if (!gateResult.IsReady)
        {
            return Recovery(
                fullPath,
                gateResult.DiagnosticCode,
                gateResult.DiagnosticMessage);
        }

        if (gateResult.Status == ConfigDocumentGateStatus.Current)
        {
            return new ConfigFilePreparationResult(ConfigFilePreparationStatus.Current, original);
        }

        var recoveryCopyPath = string.Empty;
        var tempPath = BuildTemporaryPath(fullPath, "migration");
        var replaced = false;
        try
        {
            recoveryCopyPath = CreateRecoveryCopy(fullPath);
            Observe(ConfigMigrationStage.RecoveryCopyFlushed);

            WriteNewFileAndFlush(tempPath, gateResult.Utf8Json!);
            Observe(ConfigMigrationStage.TemporaryFileFlushed);
            ValidateCurrentFile(tempPath);
            Observe(ConfigMigrationStage.TemporaryFileValidated);

            File.Replace(tempPath, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            replaced = true;
            Observe(ConfigMigrationStage.AtomicReplaceCompleted);

            var formalBytes = ReadAllBytes(fullPath);
            ValidateCurrentBytes(formalBytes);
            Observe(ConfigMigrationStage.FormalFileValidated);

            return new ConfigFilePreparationResult(
                ConfigFilePreparationStatus.Migrated,
                formalBytes,
                RecoveryCopyPath: recoveryCopyPath,
                Warnings: gateResult.Warnings);
        }
        catch (Exception ex)
        {
            if (replaced && !string.IsNullOrWhiteSpace(recoveryCopyPath))
            {
                TryRestoreOriginal(fullPath, recoveryCopyPath);
            }

            TryDelete(tempPath);
            return Recovery(
                fullPath,
                "config_migration_commit_failed",
                ex.Message,
                string.IsNullOrWhiteSpace(recoveryCopyPath) ? null : recoveryCopyPath);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    public static IReadOnlyList<string> ListRecoveryCopies(string configPath)
    {
        var fullPath = Path.GetFullPath(configPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (directory is null || !Directory.Exists(directory))
        {
            return Array.Empty<string>();
        }

        var pattern = Path.GetFileName(fullPath) + ".recovery.*.json";
        return Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenByDescending(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private string CreateRecoveryCopy(string configPath)
    {
        var directory = Path.GetDirectoryName(configPath)!;
        var fileName = Path.GetFileName(configPath);
        var timestamp = _utcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmssfffffff'Z'");
        for (var sequence = 0; sequence < 10_000; sequence++)
        {
            var suffix = sequence == 0 ? string.Empty : $".{sequence:D4}";
            var candidate = Path.Combine(directory, $"{fileName}.recovery.{timestamp}{suffix}.json");
            try
            {
                using var source = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var destination = new FileStream(
                    candidate,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.WriteThrough);
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
            }
        }

        throw new IOException("Unable to allocate a unique recovery-copy path.");
    }

    private static void WriteNewFileAndFlush(string path, byte[] bytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private void ValidateCurrentFile(string path) => ValidateCurrentBytes(ReadAllBytes(path));

    private void ValidateCurrentBytes(byte[] bytes)
    {
        var prepared = _gate.Prepare(bytes);
        if (prepared.Status != ConfigDocumentGateStatus.Current)
        {
            throw new InvalidDataException($"Migrated configuration failed read-back validation: {prepared.DiagnosticCode} {prepared.DiagnosticMessage}");
        }
    }

    private static void TryRestoreOriginal(string configPath, string recoveryCopyPath)
    {
        var rollbackPath = BuildTemporaryPath(configPath, "rollback");
        try
        {
            WriteNewFileAndFlush(rollbackPath, ReadAllBytes(recoveryCopyPath));
            File.Replace(rollbackPath, configPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        finally
        {
            TryDelete(rollbackPath);
        }
    }

    private static byte[] ReadAllBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > int.MaxValue)
        {
            throw new IOException("Configuration file is too large.");
        }

        using var memory = new MemoryStream((int)stream.Length);
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static string BuildTemporaryPath(string configPath, string purpose)
        => Path.Combine(
            Path.GetDirectoryName(configPath)!,
            $".{Path.GetFileName(configPath)}.{purpose}.{Guid.NewGuid():N}.tmp");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private void Observe(ConfigMigrationStage stage) => _observer?.OnStage(stage);

    private static ConfigFilePreparationResult Recovery(
        string configPath,
        string code,
        string message,
        string? recoveryCopyPath = null)
        => new(
            ConfigFilePreparationStatus.RecoveryRequired,
            Diagnostic: new ConfigRecoveryDiagnostic(code, message, configPath, recoveryCopyPath),
            RecoveryCopyPath: recoveryCopyPath);
}

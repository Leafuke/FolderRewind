using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace FolderRewind.Services;

public enum ConfigImportMode { CrossDevice, LocalRestore }
public enum ConfigImportStatus { None, StagedRequiresRestart, Activating, Applied, RolledBack, Failed, Canceled }
public sealed record ConfigImportResult(ConfigImportStatus Status, string Diagnostic = "")
{
    public bool Success => Status is ConfigImportStatus.StagedRequiresRestart or ConfigImportStatus.Applied;
}
internal sealed record ConfigImportJournal(int FormatVersion, ConfigImportStatus Status,
    string CandidateSha256, bool HadOriginal = false, string OriginalSha256 = "", string Diagnostic = "",
    string[]? PendingConfigIds = null, string ImportId = "", bool HadSetupRegistry = false, string SetupRegistrySha256 = "", ConfigImportMode Mode = ConfigImportMode.CrossDevice);
internal sealed record ConfigImportSetupRegistry(string ImportId, string[] ConfigIds, int FormatVersion = 1,
    ConfigImportMode Mode = ConfigImportMode.CrossDevice, string OriginalSha256 = "");

/// <summary>Preparation never touches the active configuration. Only startup can activate it.</summary>
internal sealed class ConfigImportTransactionStore(string configPath)
{
    private static readonly object PreparationSync = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private string Root => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, "migration", "config-import");
    private string Candidate(string hash) => Path.Combine(Root, "candidate-" + hash + ".json");
    private string Original(string id) => Path.Combine(Root, "before-import-" + id + ".json");
    internal string[] SafetyCopies => Directory.Exists(Root) ? Directory.GetFiles(Root, "before-import-*.json") : [];
    private string JournalPath => Path.Combine(Root, "transaction.json");
    private string SetupRegistryPath => Path.Combine(Root, "setup.json");
    private string OriginalSetupRegistry(string id) => Path.Combine(Root, "before-setup-" + id + ".json");
    internal Action<string>? FaultInjector { get; set; }
    internal ConfigImportResult Result => Load() is { } j ? new(j.Status, j.Diagnostic) : new(ConfigImportStatus.None);

    internal ConfigImportResult Stage(byte[] bytes, Func<byte[], string?> validate, string[]? projectIds = null, ConfigImportMode mode = ConfigImportMode.CrossDevice)
    {
        lock (PreparationSync) return StageInsideLock(bytes, validate, projectIds, mode);
    }
    private ConfigImportResult StageInsideLock(byte[] bytes, Func<byte[], string?> validate, string[]? projectIds, ConfigImportMode mode)
    {
        Validate(bytes, validate);
        if (Load()?.Status == ConfigImportStatus.Activating)
            throw new InvalidOperationException("An interrupted configuration activation must be recovered first.");
        var hash = Hash(bytes);
        AtomicFileService.Write(Candidate(hash), stream => stream.Write(bytes));
        var readBack = File.ReadAllBytes(Candidate(hash));
        Validate(readBack, validate);
        if (!bytes.AsSpan().SequenceEqual(readBack)) throw new IOException("Candidate configuration changed during preparation.");
        Save(new(2, ConfigImportStatus.StagedRequiresRestart, Hash(bytes), PendingConfigIds: projectIds, ImportId: Guid.NewGuid().ToString("N"), Mode: mode));
        return Result;
    }

    internal bool BeginActivation(Func<byte[], string?> validate)
    {
        var journal = Load();
        if (journal?.Status == ConfigImportStatus.Activating)
        {
            Rollback("Configuration activation was interrupted; the previous configuration was restored.");
            return false;
        }
        if (journal?.Status != ConfigImportStatus.StagedRequiresRestart) return false;
        try
        {
            var candidate = File.ReadAllBytes(Candidate(journal.CandidateSha256));
            if (Hash(candidate) != journal.CandidateSha256) throw new InvalidDataException("Candidate configuration hash changed.");
            Validate(candidate, validate);
            var original = File.Exists(configPath) ? File.ReadAllBytes(configPath) : null;
            if (original is not null) AtomicFileService.Write(Original(journal.ImportId), stream => stream.Write(original));
            var setup = File.Exists(SetupRegistryPath) ? File.ReadAllBytes(SetupRegistryPath) : null;
            if (setup is not null) AtomicFileService.Write(OriginalSetupRegistry(journal.ImportId), stream => stream.Write(setup));
            journal = journal with { Status = ConfigImportStatus.Activating, HadOriginal = original is not null,
                OriginalSha256 = original is null ? "" : Hash(original), HadSetupRegistry = setup is not null,
                SetupRegistrySha256 = setup is null ? "" : Hash(setup) };
            Save(journal);
            FaultInjector?.Invoke("Activating");
            AtomicFileService.Write(configPath, stream => stream.Write(candidate));
            AtomicFileService.Write(SetupRegistryPath, stream => JsonSerializer.Serialize(stream,
                new ConfigImportSetupRegistry(journal.ImportId, journal.PendingConfigIds ?? [], journal.FormatVersion, journal.Mode, journal.OriginalSha256), JsonOptions));
            FaultInjector?.Invoke("Replaced");
            Validate(File.ReadAllBytes(configPath), validate);
            return true;
        }
        catch (Exception ex)
        {
            if (Load()?.Status == ConfigImportStatus.Activating) Rollback(ex.Message);
            else Save(journal with { Status = ConfigImportStatus.Failed, Diagnostic = ex.Message });
            return false;
        }
    }

    internal void CommitActivation()
    {
        var journal = Load() ?? throw new InvalidOperationException("Configuration activation journal is missing.");
        if (journal.Status != ConfigImportStatus.Activating || Hash(File.ReadAllBytes(configPath)) != journal.CandidateSha256)
            throw new InvalidDataException("Activated configuration differs from the prepared candidate.");
        Save(journal with { Status = ConfigImportStatus.Applied });
    }

    internal void Rollback(string diagnostic)
    {
        var journal = Load() ?? throw new InvalidDataException("Configuration rollback journal is missing.");
        if (journal.Status != ConfigImportStatus.Activating) return;
        if (journal.HadOriginal)
        {
            var original = File.ReadAllBytes(Original(journal.ImportId));
            if (Hash(original) != journal.OriginalSha256) throw new InvalidDataException("Configuration safety copy is damaged.");
            AtomicFileService.Write(configPath, stream => stream.Write(original));
        }
        else if (File.Exists(configPath)) File.Delete(configPath);
        if (journal.HadSetupRegistry)
        {
            var setup = File.ReadAllBytes(OriginalSetupRegistry(journal.ImportId));
            if (Hash(setup) != journal.SetupRegistrySha256) throw new InvalidDataException("Migration setup safety copy is damaged.");
            AtomicFileService.Write(SetupRegistryPath, stream => stream.Write(setup));
        }
        else if (File.Exists(SetupRegistryPath)) File.Delete(SetupRegistryPath);
        Save(journal with { Status = ConfigImportStatus.RolledBack, Diagnostic = diagnostic });
    }

    internal void Cancel()
    {
        var journal = Load();
        if (journal?.Status == ConfigImportStatus.StagedRequiresRestart)
            Save(journal with { Status = ConfigImportStatus.Canceled });
    }
    internal string? PendingToken(string configId)
    {
        var registry = LoadSetup();
        return registry is not null && Array.Exists(registry.ConfigIds, id => new History.Domain.HistoryConfigId(id).Matches(configId)) ? registry.ImportId : null;
    }
    internal ConfigImportMode ActiveMode => LoadSetup()?.Mode ?? ConfigImportMode.CrossDevice;
    internal bool HasLegacySetup => LoadSetup()?.FormatVersion == 1;
    internal byte[]? ReadPreviousConfig()
    {
        var registry = LoadSetup();
        if (registry is null || registry.OriginalSha256.Length == 0) return null;
        var path = Original(registry.ImportId);
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        if (Hash(bytes) != registry.OriginalSha256) throw new InvalidDataException("Configuration safety copy changed.");
        return bytes;
    }
    private ConfigImportSetupRegistry? LoadSetup()
    {
        // A staged/canceled candidate never replaces the active setup registry.
        if (!File.Exists(SetupRegistryPath)) return null;
        var registry = JsonSerializer.Deserialize<ConfigImportSetupRegistry>(File.ReadAllBytes(SetupRegistryPath), JsonOptions)
            ?? throw new InvalidDataException("Configuration setup registry is empty.");
        if (!Guid.TryParseExact(registry.ImportId, "N", out _) || registry.ConfigIds is null || registry.OriginalSha256 is null
            || registry.FormatVersion is not (1 or 2) || !Enum.IsDefined(registry.Mode))
            throw new InvalidDataException("Configuration setup registry is invalid.");
        return registry;
    }
    private ConfigImportJournal? Load()
    {
        if (!File.Exists(JournalPath)) return null;
        var journal = JsonSerializer.Deserialize<ConfigImportJournal>(File.ReadAllBytes(JournalPath), JsonOptions)
            ?? throw new InvalidDataException("Configuration import journal is empty.");
        if (journal.FormatVersion is not (1 or 2) || !Enum.IsDefined(journal.Mode) || !Enum.IsDefined(journal.Status)
            || journal.CandidateSha256 is null || journal.CandidateSha256.Length != 64 || !System.Linq.Enumerable.All(journal.CandidateSha256, Uri.IsHexDigit)
            || !Guid.TryParseExact(journal.ImportId, "N", out _))
            throw new InvalidDataException("Configuration import journal is incompatible.");
        return journal;
    }
    private void Save(ConfigImportJournal journal) => AtomicFileService.Write(JournalPath,
        stream => JsonSerializer.Serialize(stream, journal, JsonOptions));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Validate(byte[] bytes, Func<byte[], string?> validate)
    {
        var error = validate(bytes);
        if (!string.IsNullOrWhiteSpace(error)) throw new InvalidDataException(error);
    }
}

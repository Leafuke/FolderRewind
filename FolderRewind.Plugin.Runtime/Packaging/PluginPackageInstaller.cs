using System.Text.Json;
using System.Text.Json.Serialization;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Packaging;

public enum PluginInstallProvenance
{
    Manual = 0,
    OfficialCatalog = 1,
    BundledOfficial = 2
}

public enum PluginInstallTransactionPhase
{
    Prepared = 0,
    CandidateSelected = 1,
    CandidateValidated = 2,
    [Obsolete("Read compatibility for Revision 15 journals.")]
    ActivationValidated = CandidateValidated,
    Committed = 3,
    RolledBack = 4
}

public sealed record InstalledPluginVersion(string Version, string Sha256, DateTimeOffset InstalledAtUtc);
public sealed record PluginInstallState(
    int SchemaVersion,
    PluginId PluginId,
    string CurrentVersion,
    string PreviousKnownGoodVersion,
    PluginInstallProvenance Provenance,
    IReadOnlyList<InstalledPluginVersion> Versions,
    string LastTransactionId);
public sealed record PluginInstallTransactionJournal(
    string TransactionId,
    PluginId PluginId,
    string CandidateVersion,
    string PriorVersion,
    PluginInstallTransactionPhase Phase,
    string CandidatePath,
    DateTimeOffset UpdatedAtUtc);
public sealed record PluginInstallResult(
    PluginInstallState State,
    ParsedPluginPackageManifest Manifest,
    string InstalledPath,
    bool Updated);

public sealed class PluginPackageInstaller
{
    private readonly string _root;
    private readonly string _transactions;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public PluginPackageInstaller(string pluginsRoot)
    {
        _root = Path.GetFullPath(pluginsRoot);
        _transactions = Path.Combine(_root, ".transactions");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_transactions);
    }

    public async ValueTask<PluginInstallResult> InstallAsync(
        string packagePath,
        PluginInstallProvenance provenance,
        string? expectedSha256 = null,
        PluginPackageInstallValidationFacts? validationFacts = null,
        CancellationToken cancellationToken = default)
    {
        var package = await PluginPackageValidator.ValidateAsync(
            packagePath,
            expectedSha256,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RecoverWithoutLockAsync(cancellationToken).ConfigureAwait(false);
            var facts = validationFacts ?? PluginPackageInstallValidationFacts.Empty(package.Manifest.Contract.PluginId);
            PluginStaticCandidateValidator.ValidateFacts(package.Manifest, facts);
            var pluginRoot = PluginRoot(package.Manifest.Contract.PluginId);
            var versionsRoot = Path.Combine(pluginRoot, "versions");
            var candidatePath = Path.Combine(versionsRoot, package.Manifest.Contract.Version);
            var statePath = Path.Combine(pluginRoot, "install-state.v1.json");
            var prior = await ReadAsync<PluginInstallState>(statePath, cancellationToken).ConfigureAwait(false);
            if (prior is not null
                && string.Equals(prior.CurrentVersion, package.Manifest.Contract.Version, StringComparison.Ordinal)
                && prior.Versions.Any(value => StringComparer.OrdinalIgnoreCase.Equals(value.Sha256, package.Sha256)))
            {
                return new PluginInstallResult(prior, package.Manifest, candidatePath, Updated: false);
            }
            if (Directory.Exists(candidatePath))
                throw new IOException("A different payload already occupies the candidate plugin version.");

            var transactionId = Guid.NewGuid().ToString("N");
            var transactionRoot = Path.Combine(_transactions, transactionId);
            var staging = Path.Combine(transactionRoot, "payload");
            var journalPath = Path.Combine(transactionRoot, "journal.json");
            var journal = new PluginInstallTransactionJournal(
                transactionId,
                package.Manifest.Contract.PluginId,
                package.Manifest.Contract.Version,
                prior?.CurrentVersion ?? string.Empty,
                PluginInstallTransactionPhase.Prepared,
                candidatePath,
                DateTimeOffset.UtcNow);
            await WriteAtomicallyAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);

            try
            {
                await PluginPackageValidator.ExtractAsync(package, staging, cancellationToken).ConfigureAwait(false);
                Directory.CreateDirectory(versionsRoot);
                Directory.Move(staging, candidatePath);
                journal = journal with
                {
                    Phase = PluginInstallTransactionPhase.CandidateSelected,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
                await WriteAtomicallyAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);

                PluginStaticCandidateValidator.Validate(candidatePath, package.Manifest, facts);
                journal = journal with
                {
                    Phase = PluginInstallTransactionPhase.CandidateValidated,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
                await WriteAtomicallyAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);

                var versions = (prior?.Versions ?? Array.Empty<InstalledPluginVersion>())
                    .Where(value => !string.Equals(value.Version, package.Manifest.Contract.Version, StringComparison.Ordinal))
                    .Append(new InstalledPluginVersion(package.Manifest.Contract.Version, package.Sha256, DateTimeOffset.UtcNow))
                    .ToArray();
                var state = new PluginInstallState(
                    1,
                    package.Manifest.Contract.PluginId,
                    package.Manifest.Contract.Version,
                    prior?.CurrentVersion ?? string.Empty,
                    provenance,
                    versions,
                    transactionId);
                await WriteAtomicallyAsync(statePath, state, cancellationToken).ConfigureAwait(false);
                journal = journal with
                {
                    Phase = PluginInstallTransactionPhase.Committed,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
                await WriteAtomicallyAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);
                await RemoveOlderVersionsAsync(pluginRoot, state, cancellationToken).ConfigureAwait(false);
                return new PluginInstallResult(state, package.Manifest, candidatePath, prior is not null);
            }
            catch
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                if (Directory.Exists(candidatePath)
                    && !string.Equals(prior?.CurrentVersion, package.Manifest.Contract.Version, StringComparison.Ordinal))
                    Directory.Delete(candidatePath, recursive: true);
                journal = journal with
                {
                    Phase = PluginInstallTransactionPhase.RolledBack,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
                await WriteAtomicallyAsync(journalPath, journal, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask RecoverAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await RecoverWithoutLockAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async ValueTask<PluginInstallState?> ReadStateAsync(
        PluginId pluginId,
        CancellationToken cancellationToken = default)
        => await ReadAsync<PluginInstallState>(
            Path.Combine(PluginRoot(pluginId), "install-state.v1.json"),
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<PluginInstallState> RollbackToPreviousKnownGoodAsync(
        PluginId pluginId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pluginRoot = PluginRoot(pluginId);
            var statePath = Path.Combine(pluginRoot, "install-state.v1.json");
            var state = await ReadAsync<PluginInstallState>(statePath, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Plugin has no committed install state.");
            if (string.IsNullOrWhiteSpace(state.PreviousKnownGoodVersion))
                throw new InvalidOperationException("Plugin has no previous known-good version.");
            var previousPath = Path.Combine(pluginRoot, "versions", state.PreviousKnownGoodVersion);
            if (!Directory.Exists(previousPath))
                throw new InvalidDataException("Previous known-good payload is missing.");
            var rolledBack = state with
            {
                CurrentVersion = state.PreviousKnownGoodVersion,
                PreviousKnownGoodVersion = state.CurrentVersion,
                LastTransactionId = "rollback-" + Guid.NewGuid().ToString("N")
            };
            await WriteAtomicallyAsync(statePath, rolledBack, cancellationToken).ConfigureAwait(false);
            return rolledBack;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask RemoveInstalledCodeAsync(
        PluginId pluginId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pluginRoot = PluginRoot(pluginId);
            if (Directory.Exists(pluginRoot)) Directory.Delete(pluginRoot, recursive: true);
        }
        finally { _gate.Release(); }
    }

    private async ValueTask RecoverWithoutLockAsync(CancellationToken cancellationToken)
    {
        foreach (var path in Directory.EnumerateFiles(_transactions, "journal.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var journal = await ReadAsync<PluginInstallTransactionJournal>(path, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Plugin install journal is empty.");
            if (journal.Phase is PluginInstallTransactionPhase.Committed or PluginInstallTransactionPhase.RolledBack) continue;
            var state = await ReadStateAsync(journal.PluginId, cancellationToken).ConfigureAwait(false);
            if (state is not null
                && string.Equals(state.CurrentVersion, journal.CandidateVersion, StringComparison.Ordinal)
                && string.Equals(state.LastTransactionId, journal.TransactionId, StringComparison.Ordinal))
            {
                await WriteAtomicallyAsync(path, journal with
                {
                    Phase = PluginInstallTransactionPhase.Committed,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                }, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (Directory.Exists(journal.CandidatePath)) Directory.Delete(journal.CandidatePath, recursive: true);
            var staging = Path.Combine(Path.GetDirectoryName(path)!, "payload");
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            await WriteAtomicallyAsync(path, journal with
            {
                Phase = PluginInstallTransactionPhase.RolledBack,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask RemoveOlderVersionsAsync(
        string pluginRoot,
        PluginInstallState state,
        CancellationToken cancellationToken)
    {
        var retained = new HashSet<string>(StringComparer.Ordinal)
        {
            state.CurrentVersion,
            state.PreviousKnownGoodVersion
        };
        var versionsRoot = Path.Combine(pluginRoot, "versions");
        if (!Directory.Exists(versionsRoot)) return;
        foreach (var directory in Directory.EnumerateDirectories(versionsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!retained.Contains(Path.GetFileName(directory))) Directory.Delete(directory, recursive: true);
        }
        var normalized = state with { Versions = state.Versions.Where(value => retained.Contains(value.Version)).ToArray() };
        await WriteAtomicallyAsync(Path.Combine(pluginRoot, "install-state.v1.json"), normalized, cancellationToken)
            .ConfigureAwait(false);
    }

    private string PluginRoot(PluginId pluginId) => Path.Combine(_root, pluginId.Value);

    private async ValueTask<T?> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return default;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        return await JsonSerializer.DeserializeAsync<T>(stream, _json, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteAtomicallyAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
        {
            await JsonSerializer.SerializeAsync(stream, value, _json, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
        await using var verify = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        if (await JsonSerializer.DeserializeAsync<T>(verify, _json, cancellationToken).ConfigureAwait(false) is null)
            throw new InvalidDataException("Plugin install metadata read-back failed.");
    }
}

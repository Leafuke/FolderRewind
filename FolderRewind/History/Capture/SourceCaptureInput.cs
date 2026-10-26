using FolderRewind.History.Storage;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Capture;

/// <summary>The complete sampled inventory is independent from the possibly sparse input directory.</summary>
public sealed class SourceCaptureInput : IDisposable
{
    public string OriginalRoot { get; }
    public ImmutableSortedDictionary<string, SourceCaptureFileState> Files { get; }
    public ImmutableArray<string> Directories { get; }
    public SourceCaptureBaseline? Baseline { get; set; }
    public ScopeAwareSourceCaptureDiff? Changes { get; set; }
    public string BoundaryFingerprint { get; }
    public IReadOnlyList<string> MetadataPaths { get; set; } = [];
    public SourceCaptureReadLease? View { get; private set; }
    private bool _preparedCopy, _preparedProtection, _preparedDirectories;
    private bool _automaticCopySelected;
    private bool _disposed;

    public SourceCaptureInput(string root, IReadOnlyDictionary<string, SourceCaptureFileState> files,
        IEnumerable<string> directories, SourceCaptureBaseline? baseline, string boundaryFingerprint)
    {
        OriginalRoot = Path.GetFullPath(root);
        Files = files.ToImmutableSortedDictionary(StringComparer.Ordinal);
        Directories = [.. directories.Order(StringComparer.Ordinal)];
        Baseline = baseline;
        BoundaryFingerprint = boundaryFingerprint;
    }

    public async Task<SourceCaptureReadLease> PrepareAsync(IEnumerable<string> paths, bool copy, bool protect,
        string temporaryRoot, CancellationToken token = default, bool includeDirectories = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        token.ThrowIfCancellationRequested();
        var selected = paths.Select(ArtifactPathRules.NormalizeRelativePath).ToHashSet(StringComparer.Ordinal);
        // Baseline revalidation may need the complete input. Keep those exact bytes for the later sparse capture.
        if (View is { } prepared && _preparedCopy == copy && _preparedProtection == protect
            && (!includeDirectories || _preparedDirectories) && selected.IsSubsetOf(prepared.ReadPaths))
        {
            HistoryOperationDiagnostics.Current?.Count("source-input-lease-reuses");
            return prepared;
        }
        View?.Dispose(); View = null;
        View = await SourceCaptureReadLease.CreateAsync(this, selected, copy, protect, temporaryRoot, token, includeDirectories).ConfigureAwait(false);
        _preparedCopy = copy; _preparedProtection = protect; _preparedDirectories = includeDirectories;
        return View;
    }

    public Task<SourceCaptureReadLease> PrepareStatisticsValidationAsync(bool copy, string temporaryRoot, CancellationToken token)
        => PrepareAsync(Files.Keys, copy, protect: true, temporaryRoot, token);

    public Task<SourceCaptureReadLease> PrepareStatisticsValidationAsync(BackupSourceReadMode mode, string temporaryRoot, CancellationToken token)
        => PrepareForModeAsync(Files.Keys, mode, protect: true, temporaryRoot, token);

    public async Task<SourceCaptureReadLease> PrepareForModeAsync(IEnumerable<string> paths, BackupSourceReadMode mode,
        bool protect, string temporaryRoot, CancellationToken token = default, bool includeDirectories = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        token.ThrowIfCancellationRequested();
        var selected = paths.ToArray();
        var copy = mode == BackupSourceReadMode.AlwaysCopy || mode == BackupSourceReadMode.Automatic && _automaticCopySelected;
        HistoryOperationDiagnostics.Current?.Observe("source-read-mode", mode.ToString());
        try
        {
            return await PrepareAsync(selected, copy, protect, temporaryRoot, token, includeDirectories).ConfigureAwait(false);
        }
        catch (CaptureInputException error) when (mode == BackupSourceReadMode.Automatic && protect && !copy
            && error.IsFileSharingConflict && error.NativeErrorCode is 32 or 33 && !token.IsCancellationRequested)
        {
            // CreateAsync released every partially acquired guard before throwing. Recheck the same inventory
            // during copying; changed paths/statistics, copy failures and cancellation must propagate.
            _automaticCopySelected = true;
            HistoryOperationDiagnostics.Current?.Count("source-auto-copy-switches");
            HistoryOperationDiagnostics.Current?.Observe("source-auto-copy-path", error.SourcePath);
            HistoryOperationDiagnostics.Current?.Observe("source-auto-copy-error", error.NativeErrorCode.ToString()!);
            LogService.Log($"[CaptureInput] phase=auto-copy; reason=file-sharing-conflict; nativeError={error.NativeErrorCode}; path={error.SourcePath}; source={OriginalRoot}");
            return await PrepareAsync(selected, copy: true, protect, temporaryRoot, token, includeDirectories).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        View?.Dispose(); View = null;
    }
}

/// <summary>Input preparation failures must never be treated as semantic-transform fallback.</summary>
public sealed class CaptureInputException(string path, Exception error, string phase = "prepare", bool fileSharingConflict = false)
    : IOException($"Capture input failed: {path}; HResult=0x{error.HResult:X8}; {error.Message}", error)
{
    public string SourcePath { get; } = path;
    public string Phase { get; } = phase;
    public bool IsFileSharingConflict { get; } = fileSharingConflict;
    public int? NativeErrorCode { get; } = (error as System.ComponentModel.Win32Exception
        ?? error.InnerException as System.ComponentModel.Win32Exception)?.NativeErrorCode;
}

public sealed class SourceCaptureReadLease : IDisposable, IVersionMetadataSourceView
{
    private readonly List<IDisposable> _guards = [];
    private readonly HashSet<string> _parents = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _protected = new(StringComparer.Ordinal);
    private readonly HashSet<string> _paths;
    private readonly SourceCaptureInput _input;
    private IDisposable? _space;
    private string? _temporaryDirectory;
    private bool _disposed;
    public string SourcePath { get; private set; }
    public string OriginalSourcePath => _input.OriginalRoot;
    public bool IsStable { get; }
    public bool IsCopy => _temporaryDirectory is not null;
    public long CopiedBytes { get; private set; }
    public IReadOnlySet<string> ReadPaths => _paths;

    private SourceCaptureReadLease(SourceCaptureInput input, HashSet<string> paths, bool stable)
    { _input = input; _paths = paths; SourcePath = input.OriginalRoot; IsStable = stable; }

    internal static async Task<SourceCaptureReadLease> CreateAsync(SourceCaptureInput input, IEnumerable<string> paths,
        bool copy, bool protect, string temporaryRoot, CancellationToken token, bool includeDirectories)
    {
        token.ThrowIfCancellationRequested();
        var selected = paths.Select(ArtifactPathRules.NormalizeRelativePath).ToHashSet(StringComparer.Ordinal);
        if (selected.Any(path => !input.Files.ContainsKey(path))) throw new InvalidDataException("Capture input exceeds its inventory.");
        var lease = new SourceCaptureReadLease(input, selected, copy || protect);
        string current = input.OriginalRoot;
        string phase = "prepare";
        try
        {
            using var timing = HistoryOperationDiagnostics.Current?.Measure(copy ? "source-copy" : protect ? "source-protect" : "source-direct");
            if (copy || protect) lease._guards.Add(new WindowsPhysicalDirectoryLease(input.OriginalRoot));
            if (copy)
            {
                var bytes = selected.Sum(path => input.Files[path].Size);
                var copyDirectories = new HashSet<string>(StringComparer.Ordinal);
                if (includeDirectories) copyDirectories.UnionWith(input.Directories);
                foreach (var relative in selected)
                {
                    var relativeParent = relative;
                    while (relativeParent.LastIndexOf('/') is var separator && separator >= 0)
                    { relativeParent = relativeParent[..separator]; copyDirectories.Add(relativeParent); }
                }
                phase = "reserve";
                var reservedBytes = checked(bytes + (selected.Count + copyDirectories.Count + 1L) * 4096);
                lease._space = ArtifactSpaceBudget.Shared.Reserve(new ArtifactSpaceRequest(temporaryRoot,
                    reservedBytes));
                HistoryOperationDiagnostics.Current?.Count("source-copy-reserved-bytes", reservedBytes);
                var parent = Path.GetFullPath(temporaryRoot);
                Directory.CreateDirectory(parent);
                lease._temporaryDirectory = Path.Combine(parent, "capture-input-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(lease._temporaryDirectory);
                lease.SourcePath = lease._temporaryDirectory;
                foreach (var directory in copyDirectories)
                    Directory.CreateDirectory(ArtifactPathRules.ResolveUnderRoot(lease.SourcePath, directory));
            }
            byte[]? buffer = null;
            foreach (var relative in selected.Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                current = ArtifactPathRules.ResolveUnderRoot(input.OriginalRoot, relative);
                phase = copy ? "copy" : protect ? "protect" : "direct";
                lease.CheckPath(current);
                if (protect && !copy) lease.Protect(relative);
                else if (copy)
                {
                    lease.PinParent(current);
                    var expected = input.Files[relative];
                    RequireState(current, expected);
                    var target = ArtifactPathRules.ResolveUnderRoot(lease.SourcePath, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using (var source = new FileStream(current, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite, 65536, true))
                    await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    {
                        buffer ??= new byte[65536]; long count = 0; int read;
                        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                        {
                            count = checked(count + read);
                            if (count > expected.Size) throw new IOException("Source grew while copying.");
                            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                        }
                        if (count != expected.Size) throw new IOException("Source length changed while copying.");
                        lease.CopiedBytes += count;
                    }
                    lease.CheckPath(current);
                    RequireState(current, expected);
                    File.SetLastWriteTimeUtc(target, expected.LastWriteTimeUtc);
                }
            }
            HistoryOperationDiagnostics.Current?.Count("source-copy-bytes", lease.CopiedBytes);
            HistoryOperationDiagnostics.Current?.Count("source-copy-files", copy ? selected.Count : 0);
            HistoryOperationDiagnostics.Current?.Count("source-protected-files", lease._protected.Count);
            return lease;
        }
        catch (OperationCanceledException) { lease.Dispose(); throw; }
        catch (CaptureInputException) { lease.Dispose(); throw; }
        catch (Exception error) { lease.Dispose(); throw new CaptureInputException(current, error, phase); }
    }

    public bool TryProtectMetadata(IReadOnlyList<string> paths)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (paths.Count == 0 || paths.Any(path => !_paths.Contains(path))) return false;
        if (IsStable) return true;
        try { foreach (var path in paths) Protect(path); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    public bool CanReadMetadata(IReadOnlyList<string> paths) => !_disposed && paths.Count > 0
        && paths.All(path => _paths.Contains(path) && (IsStable || _protected.Contains(path)));

    private void Protect(string relative)
    {
        if (_protected.Contains(relative)) return;
        var path = ArtifactPathRules.ResolveUnderRoot(_input.OriginalRoot, relative);
        CheckPath(path);
        PinParent(path);
        FileStream stream;
        try { stream = WindowsDirectoryReadLease.OpenPayload(path); }
        catch (IOException error) when (error.InnerException is System.ComponentModel.Win32Exception native
            && native.NativeErrorCode is 32 or 33)
        { throw new CaptureInputException(path, error, "protect", fileSharingConflict: true); }
        _guards.Add(stream);
        CheckPath(path);
        RequireState(path, _input.Files[relative]);
        if (stream.Length != _input.Files[relative].Size) throw new IOException("Protected source differs from the sampled inventory.");
        _protected.Add(relative);
    }

    private void PinParent(string path)
    {
        var parent = Path.GetDirectoryName(path)!;
        if (_parents.Add(parent)) _guards.Add(new WindowsPhysicalDirectoryLease(parent));
    }

    private void CheckPath(string path)
    {
        for (var current = path; !StringComparer.OrdinalIgnoreCase.Equals(current, _input.OriginalRoot); current = Path.GetDirectoryName(current)!)
        {
            if (string.IsNullOrEmpty(current) || (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Capture input contains a filesystem alias: " + path);
        }
    }

    private static void RequireState(string path, SourceCaptureFileState expected)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length != expected.Size || file.LastWriteTimeUtc != expected.LastWriteTimeUtc)
            throw new IOException("Source changed after its capture inventory: " + path);
    }

    public ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken token)
    {
        var path = ArtifactPathRules.NormalizeRelativePath(relativePath);
        if (_input.Files.TryGetValue(path, out var state) && state.Size > 64L * 1024 * 1024)
            throw new InvalidDataException("Metadata input exceeds its byte limit.");
        return OpenCapturedReadAsync(path, token);
    }

    internal ValueTask<Stream> OpenCapturedReadAsync(string relativePath, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested();
        var path = ArtifactPathRules.NormalizeRelativePath(relativePath);
        if (!_paths.Contains(path) || (!IsStable && !_protected.Contains(path)))
            throw new IOException("No protected capture input: " + path);
        return ValueTask.FromResult<Stream>(new FileStream(ArtifactPathRules.ResolveUnderRoot(SourcePath, path),
            FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true));
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        for (var i = _guards.Count - 1; i >= 0; i--) _guards[i].Dispose();
        _guards.Clear();
        try
        {
            // This unique, owned directory is never the original source.
            if (_temporaryDirectory is { } owned && Directory.Exists(owned)) Directory.Delete(owned, true);
        }
        finally { _space?.Dispose(); _space = null; }
    }
}

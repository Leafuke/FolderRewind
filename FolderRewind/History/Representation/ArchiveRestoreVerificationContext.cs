using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Representation;

internal interface IArchiveRestorePreflightBackend
{
    object InputPolicyIdentity { get; }
    bool TryGetListing(string path, out Services.ArchiveSafeListing? listing);
    ValueTask<PayloadVerificationResult> PreflightAsync(VersionRepresentation representation, string path,
        bool fullContentTest, CancellationToken token);
    ValueTask MaterializeRestoreAsync(IReadOnlyList<ArchiveMaterializationInput> inputs, string staging,
        ArchiveRestoreVerificationContext context, CancellationToken token);
}

/// <summary>仅本次操作复用：先固定父目录和文件对象，再验证摘要/清单。生命周期覆盖全部必要读取。</summary>
public sealed class ArchiveRestoreVerificationContext(RestoreArchivePreflightMode mode) : IDisposable
{
    private sealed record Input(VersionRepresentation Representation, string Path,
        WindowsDirectoryReadLease Parents, FileStream Stream, object PolicyIdentity, PayloadVerificationResult Result,
        Services.ArchiveSafeListing? Listing);
    private readonly Dictionary<RepresentationId, Input> _inputs = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    public RestoreArchivePreflightMode Mode { get; } = Enum.IsDefined(mode) ? mode : throw new ArgumentOutOfRangeException(nameof(mode));
    internal async ValueTask<PayloadVerificationResult> VerifyAsync(IArchiveRestorePreflightBackend backend,
        VersionRepresentation representation, string path, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var absolute = Path.GetFullPath(path);
            if (_inputs.TryGetValue(representation.RepresentationId, out var input))
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(input.Path, absolute) || !Equivalent(input.Representation, representation)
                    || !Equals(input.PolicyIdentity, backend.InputPolicyIdentity))
                    throw new IOException("Restore input binding changed during the operation.");
                return input.Result;
            }
            var parents = new WindowsDirectoryReadLease(absolute);
            FileStream? stream = null;
            try
            {
                stream = WindowsDirectoryReadLease.OpenPayload(absolute);
                // 保持同一对象的无写/无删除租约。大小与 mtime 不作为内容身份或摘要证据。
                if (representation.RepresentationSpecificMetadata.TryGetValue("storageSha256", out var expected))
                {
                    using var timing = Services.HistoryOperationDiagnostics.Current?.Measure("storage-hash");
                    var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
                    if (!StringComparer.OrdinalIgnoreCase.Equals(actual, expected)) throw new IOException("Restore storage hash mismatch.");
                    stream.Position = 0;
                }
                var result = await backend.PreflightAsync(representation, absolute, Mode == RestoreArchivePreflightMode.Strict, token).ConfigureAwait(false);
                backend.TryGetListing(absolute, out var listing);
                _inputs.Add(representation.RepresentationId, new(representation, absolute, parents, stream, backend.InputPolicyIdentity, result, listing));
                return result;
            }
            catch { stream?.Dispose(); parents.Dispose(); throw; }
        }
        finally { _gate.Release(); }
    }
    private static bool Equivalent(VersionRepresentation a, VersionRepresentation b) =>
        a.VersionId == b.VersionId && a.Kind == b.Kind && a.Format == b.Format && a.Fidelity == b.Fidelity
        && a.LogicalSha256 == b.LogicalSha256 && a.StateFingerprint == b.StateFingerprint
        && a.DependencyRepresentationIds.SequenceEqual(b.DependencyRepresentationIds)
        && a.RepresentationSpecificMetadata.Count == b.RepresentationSpecificMetadata.Count
        && a.RepresentationSpecificMetadata.All(p => b.RepresentationSpecificMetadata.TryGetValue(p.Key, out var v) && v == p.Value);
    internal Services.ArchiveSafeListing RequireListing(ArchiveMaterializationInput input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_inputs.TryGetValue(input.Representation.RepresentationId, out var held) || !held.Result.Success
            || held.Listing is null || !Equivalent(held.Representation, input.Representation)
            || !StringComparer.OrdinalIgnoreCase.Equals(held.Path, Path.GetFullPath(input.LocalPath)))
            throw new IOException("Materialization input has no protected safe listing.");
        return held.Listing;
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var input in _inputs.Values) { input.Stream.Dispose(); input.Parents.Dispose(); }
        _inputs.Clear(); _gate.Dispose();
    }
}

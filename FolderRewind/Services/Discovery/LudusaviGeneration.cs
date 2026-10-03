using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Discovery;

/// <summary>An immutable disk generation protected from replacement/pruning for one scan.</summary>
public sealed class LudusaviGeneration : IAsyncDisposable
{
    private SemaphoreSlim? _lease;
    private readonly object _sync = new();
    private int _readers;
    private bool _disposed;
    internal LudusaviGeneration(LudusaviManifestCacheMetadata metadata, string indexPath, SemaphoreSlim lease)
    { Metadata = metadata; IndexPath = indexPath; _lease = lease; }
    public LudusaviManifestCacheMetadata Metadata { get; }
    internal string IndexPath { get; }
    public List<LudusaviCompilerDiagnostic> Diagnostics { get; } = new();
    public async IAsyncEnumerable<LudusaviCompiledGame> ReadGamesAsync([EnumeratorCancellation] CancellationToken token)
    {
        lock (_sync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LudusaviGeneration));
            if (_readers != 0) throw new InvalidOperationException("A generation cannot be enumerated concurrently.");
            _readers++;
        }
        try
        {
            Diagnostics.Clear();
            await using var file = new FileStream(IndexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var reader = new LudusaviIndexReader(Metadata.SourceSha256, Diagnostics);
            await foreach (var game in reader.ReadAsync(gzip, token).ConfigureAwait(false)) yield return game;
        }
        finally { lock (_sync) { _readers--; ReleaseIfFinished(); } }
    }
    public ValueTask DisposeAsync()
    { lock (_sync) { _disposed = true; ReleaseIfFinished(); } return ValueTask.CompletedTask; }
    private void ReleaseIfFinished()
    { if (_disposed && _readers == 0) Interlocked.Exchange(ref _lease, null)?.Release(); }
}

/// <summary>Consumes v3 JSON one game/diagnostic at a time, without a full JSON document.</summary>
internal sealed class LudusaviIndexReader(string expectedHash, List<LudusaviCompilerDiagnostic> diagnostics)
{
    private static readonly JsonSerializerOptions Options = new()
    { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    private JsonReaderState _state;
    private string? _property;
    private string? _array;
    private int _schema;
    private string? _hash;
    private bool _ended;
    private bool _gamesSeen;

    public async IAsyncEnumerable<LudusaviCompiledGame> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        var count = 0;
        var final = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var (consumed, game) = Consume(buffer.AsSpan(0, count), final);
            if (consumed > 0)
            { Buffer.BlockCopy(buffer, consumed, buffer, 0, count - consumed); count -= consumed; }
            if (game is not null) { yield return game; continue; }
            if (consumed > 0 && count > 0) continue;
            if (final)
            {
                if (count != 0 || !_ended || !_gamesSeen || _schema != LudusaviCompiledIndex.CurrentSchemaVersion
                    || !string.Equals(_hash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Invalid or truncated Ludusavi index.");
                yield break;
            }
            if (count == buffer.Length)
            {
                if (buffer.Length >= LudusaviManifestCacheService.MaximumManifestBytes)
                    throw new InvalidDataException("A Ludusavi index entry exceeds the input size limit.");
                Array.Resize(ref buffer, checked(buffer.Length * 2));
            }
            var read = await stream.ReadAsync(buffer.AsMemory(count), token).ConfigureAwait(false);
            count += read;
            final = read == 0;
        }
    }

    private (int Consumed, LudusaviCompiledGame? Game) Consume(ReadOnlySpan<byte> bytes, bool final)
    {
        var reader = new Utf8JsonReader(bytes, final, _state);
        while (true)
        {
            var before = reader;
            if (!reader.Read()) { _state = reader.CurrentState; return ((int)reader.BytesConsumed, null); }
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
            { _property = reader.GetString(); continue; }
            if (reader.TokenType == JsonTokenType.StartArray && reader.CurrentDepth == 1
                && (_property == "Games" || _property == "Diagnostics"))
            { _array = _property; if (_array == "Games") _gamesSeen = true; continue; }
            if (reader.TokenType == JsonTokenType.EndArray && reader.CurrentDepth == 1)
            { _array = null; continue; }
            if (reader.TokenType == JsonTokenType.StartObject && reader.CurrentDepth == 2 && _array is not null)
            {
                var start = (int)reader.TokenStartIndex;
                if (!reader.TrySkip()) { _state = before.CurrentState; return ((int)before.BytesConsumed, null); }
                var record = bytes.Slice(start, (int)reader.BytesConsumed - start);
                LudusaviCompiledGame? game = null;
                if (_array == "Games") game = JsonSerializer.Deserialize<LudusaviCompiledGame>(record, Options)
                    ?? throw new InvalidDataException("Empty game rule.");
                else diagnostics.Add(JsonSerializer.Deserialize<LudusaviCompilerDiagnostic>(record, Options)
                    ?? throw new InvalidDataException("Empty compiler diagnostic."));
                _state = reader.CurrentState;
                return ((int)reader.BytesConsumed, game);
            }
            if (reader.CurrentDepth == 1 && reader.TokenType != JsonTokenType.EndObject)
            {
                if (_property == "SchemaVersion") _schema = reader.GetInt32();
                else if (_property == "SourceSha256") _hash = reader.GetString();
                else if (!reader.TrySkip()) { _state = before.CurrentState; return ((int)before.BytesConsumed, null); }
            }
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0) _ended = true;
        }
    }
}

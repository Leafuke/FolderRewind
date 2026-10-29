using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal static class VerifiedAssetDownloader
{
    internal static async Task<byte[]> DownloadAsync(HttpClient client, IEnumerable<string> sources,
        string expectedSha256, long maximumBytes, CancellationToken token, Action<string, Exception>? onFailure = null)
    {
        if (expectedSha256.Length != 64 || !expectedSha256.All(char.IsAsciiHexDigit))
            throw new ArgumentException("A SHA-256 digest is required.", nameof(expectedSha256));
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        Exception? lastError = null;
        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
                budget.CancelAfter(TimeSpan.FromMinutes(5));
                using var response = await client.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > maximumBytes) throw new IOException("Download exceeds its size bound.");
                await using var input = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
                using var output = new MemoryStream();
                var buffer = new byte[65536];
                int count;
                while ((count = await input.ReadAsync(buffer, budget.Token).ConfigureAwait(false)) != 0)
                {
                    if (output.Length + count > maximumBytes) throw new IOException("Download exceeds its size bound.");
                    output.Write(buffer, 0, count);
                }
                var bytes = output.ToArray();
                if (!StringComparer.OrdinalIgnoreCase.Equals(Convert.ToHexString(SHA256.HashData(bytes)), expectedSha256))
                    throw new InvalidDataException("Downloaded asset SHA-256 does not match the pinned checksum.");
                token.ThrowIfCancellationRequested();
                return bytes;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                lastError = ex;
                onFailure?.Invoke(source, ex);
            }
        }
        throw lastError ?? new InvalidOperationException("No download source available.");
    }
}

using System;
using System.Threading.Tasks;

namespace FolderRewind.Services.KnotLink
{
    /// <summary>
    /// Adapts the persistent, synchronous KnotLink 2.x querier to the host's
    /// asynchronous one-request API. A dedicated connection preserves support
    /// for concurrent callers and is disposed after the reply is received.
    /// </summary>
    internal static class OpenSocketQueryAdapter
    {
        public static Task<string> QueryAsync(
            string appId,
            string openSocketId,
            string question,
            string host = "127.0.0.1",
            int port = 6376,
            int timeoutMs = 5000)
        {
            if (timeoutMs < -1)
            {
                throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            }

            return Task.Run(async () =>
            {
                using var querier = new OpenSocketQuerier(appId, openSocketId, host, port);
                await querier.InitializeAsync().ConfigureAwait(false);
                return querier.Query(question, timeoutMs);
            });
        }
    }
}

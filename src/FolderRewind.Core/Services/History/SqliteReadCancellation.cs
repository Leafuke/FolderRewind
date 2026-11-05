using Microsoft.Data.Sqlite;
using System;
using System.Threading;

namespace FolderRewind.Services;

/// <summary>Scoped to a serialized read; never left installed for the next write on the connection.</summary>
internal sealed class SqliteReadCancellation : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly bool _registered;
    private bool _disposed;
    internal SqliteReadCancellation(SqliteConnection connection, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _connection = connection;
        if (!token.CanBeCanceled) return;
        SQLitePCL.raw.sqlite3_progress_handler(connection.Handle ?? throw new InvalidOperationException("Spatial read requires an open connection."), 1000,
            static state => ((CancellationToken)state).IsCancellationRequested ? 1 : 0, token);
        _registered = true;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_registered) SQLitePCL.raw.sqlite3_progress_handler(_connection.Handle!, 0, null!, null!);
    }
}

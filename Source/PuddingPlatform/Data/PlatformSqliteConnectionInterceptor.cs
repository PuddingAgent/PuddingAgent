using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace PuddingPlatform.Data;

/// <summary>
/// Applies per-connection SQLite runtime settings for the platform database.
/// These PRAGMAs reduce write-tail latency for diagnostic/event append workloads.
/// </summary>
public sealed class PlatformSqliteConnectionInterceptor : DbConnectionInterceptor
{
    private static readonly string[] ConnectionPragmas =
    [
        "PRAGMA synchronous=NORMAL;",
        "PRAGMA temp_store=MEMORY;",
        "PRAGMA busy_timeout=5000;",
        "PRAGMA wal_autocheckpoint=4000;",
    ];

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ApplyPragmas(connection);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await ApplyPragmasAsync(connection, cancellationToken);
    }

    private static void ApplyPragmas(DbConnection connection)
    {
        if (connection is not SqliteConnection)
        {
            return;
        }

        foreach (var pragma in ConnectionPragmas)
        {
            using var command = connection.CreateCommand();
            command.CommandText = pragma;
            command.ExecuteNonQuery();
        }
    }

    private static async Task ApplyPragmasAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection is not SqliteConnection)
        {
            return;
        }

        foreach (var pragma in ConnectionPragmas)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = pragma;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}

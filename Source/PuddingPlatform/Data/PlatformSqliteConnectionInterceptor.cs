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
        // Install the wait policy before any other connection setup. These
        // PRAGMAs run inside OpenAsync and can collide with an active writer.
        "PRAGMA busy_timeout=30000;",
        "PRAGMA synchronous=NORMAL;",
        "PRAGMA temp_store=MEMORY;",
        "PRAGMA wal_autocheckpoint=4000;",

        // 2026-10-28：平台库 11.94 GiB 却用 SQLite 默认 2 MB 页缓存。实测（空闲期 20 分钟、
        // 240/240 个 5s 区间）读持续 30-44 MB/s，而同期写只有 KB 级，且库文件 delta=0
        // —— 典型的**读放大**：每次小写都要在超大 B 树里重新读页。
        // 32 MB 而不是代码索引那样的 128 MB：cache_size 是**按连接**生效的，而平台上下文是
        // Scoped、连接可能被多个并发上下文各自持有；128 MB × N 会直接把内存吃穿。
        // 这个值属于「先验证假设」的保守起点，验证成立后再按并发实测调。
        "PRAGMA cache_size=-32768;",
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

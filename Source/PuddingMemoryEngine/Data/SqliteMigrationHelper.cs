using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace PuddingMemoryEngine.Data;

/// <summary>
/// Safe SQLite migration helpers using PRAGMA table_info instead of
/// exception-based duplicate column detection.
/// </summary>
public static class SqliteMigrationHelper
{
    /// <summary>
    /// Checks whether a column exists in a table using PRAGMA table_info.
    /// </summary>
    public static async Task<bool> ColumnExistsAsync(
        DbContext db,
        string tableName,
        string columnName,
        CancellationToken ct = default)
    {
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName})";
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Adds a column if it does not already exist.
    /// </summary>
    public static async Task<bool> AddColumnIfNotExistsAsync(
        DbContext db,
        string tableName,
        string columnName,
        string columnDefinition,
        CancellationToken ct = default)
    {
        if (await ColumnExistsAsync(db, tableName, columnName, ct))
            return false;

        await db.Database.ExecuteSqlRawAsync(
            $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition}", ct);
        return true;
    }
}

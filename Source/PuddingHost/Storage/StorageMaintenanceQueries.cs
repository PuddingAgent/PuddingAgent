using Microsoft.Data.Sqlite;
using PuddingCodeIntelligence.Contracts;

namespace PuddingHost.Storage;

/// <summary>
/// 旧 /databases 分析器与派生清理目标共享的只读 SQLite 查询（全部来自内置白名单）。
/// </summary>
internal static class StorageMaintenanceQueries
{
    internal sealed record CodeIndexScopeCandidate(
        string WorkspaceId,
        string ProjectId,
        string DisplayName,
        long ArtifactRows);

    internal sealed record IndexDefinition(bool Unique, IReadOnlyList<string> Columns);

    internal static readonly string[] CodeIndexArtifactTables =
    [
        "CodeReferences",
        "CodeRelations",
        "CodeSymbols",
        "CodeFiles",
        "CodeIndexRuns",
    ];

    internal static readonly IReadOnlyDictionary<string, string> RedundantConversationIndexes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ix_ce_seq"] = "IX_conversation_events_conversation_id_sequence",
            ["ix_ce_eid"] = "IX_conversation_events_event_id",
            ["ix_ce_turn"] = "IX_conversation_events_turn_id_type",
        };

    public static async Task<SqliteConnection> OpenConnectionAsync(
        string databasePath, bool readOnly, CancellationToken ct)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(ct);
        await using var busy = connection.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout=15000";
        await busy.ExecuteNonQueryAsync(ct);
        return connection;
    }

    public static async Task<bool> TableExistsAsync(
        SqliteConnection connection, string tableName, CancellationToken ct,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) > 0;
    }

    public static async Task<long> ExecuteScalarLongAsync(
        SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 30;
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    public static async Task<CodeIndexScopeCandidate[]> FindObsoleteCodeIndexScopesAsync(
        string databasePath, DateTimeOffset now, CancellationToken ct)
    {
        if (!File.Exists(databasePath))
            return [];

        var staleBefore = now.Subtract(TimeSpan.FromHours(24)).ToString("O");
        await using var connection = await OpenConnectionAsync(databasePath, readOnly: true, ct);
        if (!await TableExistsAsync(connection, "CodeProjects", ct))
            return [];

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT WorkspaceId, ProjectId, COALESCE(DisplayName, ProjectPath, ProjectId)
            FROM CodeProjects
            WHERE ScopeState IN ('Covered', 'Removed')
               OR (
                    ScopeState IS NULL
                    AND Status IN ('Removed', 'Failed', 'Registering')
                    AND COALESCE(UpdatedAtUtc, AddedAtUtc, '') < $staleBefore
               )
            ORDER BY WorkspaceId, ProjectId
            """;
        command.Parameters.AddWithValue("$staleBefore", staleBefore);
        var candidates = new List<CodeIndexScopeCandidate>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                candidates.Add(new CodeIndexScopeCandidate(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    ArtifactRows: 0));
            }
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            long rows = 0;
            foreach (var table in CodeIndexArtifactTables)
            {
                if (!await TableExistsAsync(connection, table, ct))
                    continue;
                await using var countCommand = connection.CreateCommand();
                countCommand.CommandText =
                    $"SELECT COUNT(*) FROM {QuoteIdentifier(table)} " +
                    "WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId";
                countCommand.Parameters.AddWithValue("$workspaceId", candidate.WorkspaceId);
                countCommand.Parameters.AddWithValue("$projectId", candidate.ProjectId);
                countCommand.CommandTimeout = 60;
                rows += Convert.ToInt64(await countCommand.ExecuteScalarAsync(ct));
            }

            candidates[i] = candidate with { ArtifactRows = rows };
        }

        return [.. candidates];
    }

    public static async Task<Dictionary<string, IndexDefinition>> ReadIndexDefinitionsAsync(
        SqliteConnection connection, string tableName, CancellationToken ct)
    {
        var definitions = new Dictionary<string, IndexDefinition>(StringComparer.OrdinalIgnoreCase);
        var indexRows = new List<(string Name, bool Unique)>();
        await using (var listCommand = connection.CreateCommand())
        {
            listCommand.CommandText = $"PRAGMA index_list({QuoteIdentifier(tableName)})";
            await using var reader = await listCommand.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                indexRows.Add((reader.GetString(1), reader.GetInt64(2) != 0));
        }

        foreach (var (name, unique) in indexRows)
        {
            var columns = new List<string>();
            await using var infoCommand = connection.CreateCommand();
            infoCommand.CommandText = $"PRAGMA index_info({QuoteIdentifier(name)})";
            await using var infoReader = await infoCommand.ExecuteReaderAsync(ct);
            while (await infoReader.ReadAsync(ct))
                columns.Add(infoReader.GetString(2));
            definitions[name] = new IndexDefinition(unique, [.. columns]);
        }

        return definitions;
    }

    /// <summary>已确认重复/失效的索引（与 EF 正式索引定义完全重复的旧运行时索引）。</summary>
    public static async Task<string[]> FindRedundantIndexesAsync(
        string databasePath, CancellationToken ct)
    {
        if (!File.Exists(databasePath))
            return [];

        await using var connection = await OpenConnectionAsync(databasePath, readOnly: true, ct);
        if (!await TableExistsAsync(connection, "conversation_events", ct))
            return [];

        var indexDefinitions = await ReadIndexDefinitionsAsync(connection, "conversation_events", ct);
        var redundant = new List<string>();
        foreach (var (legacyName, canonicalName) in RedundantConversationIndexes)
        {
            if (!indexDefinitions.TryGetValue(legacyName, out var legacy)
                || !indexDefinitions.TryGetValue(canonicalName, out var canonical))
            {
                continue;
            }
            if (legacy.Unique == canonical.Unique
                && legacy.Columns.SequenceEqual(canonical.Columns, StringComparer.OrdinalIgnoreCase))
            {
                redundant.Add(legacyName);
            }
        }

        return [.. redundant];
    }

    public static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}

using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PuddingApproval.Sqlite;

public sealed record ApprovalOutboxItem(long Sequence, ApprovalRecord Record);

/// <summary>Versioned records and at-least-once change outbox share a SQLite transaction.</summary>
public sealed partial class SqliteApprovalStore : IApprovalStore, IApprovalInbox
{
    private readonly string _connectionString;
    private SqliteApprovalStore(string path) => _connectionString = new SqliteConnectionStringBuilder
        { DataSource = path, Pooling = false, DefaultTimeout = 15 }.ToString();

    public static async Task<SqliteApprovalStore> OpenAsync(string path, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute database path is required.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var store = new SqliteApprovalStore(path);
        await using var db = await store.ConnectAsync(ct);
        await using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS approvals (
                id TEXT PRIMARY KEY, invocation TEXT NOT NULL UNIQUE, version INTEGER NOT NULL, body TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS approvals_pending_scope ON approvals(
                json_extract(body,'$.Binding.WorkspaceId'), json_extract(body,'$.Binding.AgentId'),
                json_extract(body,'$.Binding.SessionId'), json_extract(body,'$.State'), id);
            CREATE TABLE IF NOT EXISTS approval_outbox (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT, approval_id TEXT NOT NULL,
                version INTEGER NOT NULL, body TEXT NOT NULL, UNIQUE(approval_id, version));
            """;
        await command.ExecuteNonQueryAsync(ct);
        return store;
    }

    /// <summary>Only Core's AwaitingHuman producer may call this after policy admission.</summary>
    public async Task<bool> CreateAsync(ApprovalRecord record, CancellationToken ct = default)
    {
        record.ValidateOperation();
        var b = record.Binding;
        if (record.State != ApprovalState.Pending || record.Version != 0 || record.Decision is not null
            || new[] { record.Id, b.WorkspaceId, b.AgentId, b.SessionId, b.RunId, b.TurnId, b.InvocationId,
                b.OperationFingerprint, b.PolicyRevision }.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A new request requires a complete binding and pending version zero.");
        await using var db = await ConnectAsync(ct);
        using var tx = db.BeginTransaction();
        await using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "INSERT INTO approvals(id,invocation,version,body) VALUES($id,$invocation,0,$body) ON CONFLICT DO NOTHING";
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$invocation", JsonSerializer.Serialize(new[] { b.WorkspaceId, b.AgentId, b.SessionId, b.RunId, b.TurnId, b.InvocationId }));
        command.Parameters.AddWithValue("$body", JsonSerializer.Serialize(record));
        if (await command.ExecuteNonQueryAsync(ct) != 1) return false;
        await AppendAsync(db, tx, record, ct); await tx.CommitAsync(ct); return true;
    }

    public async Task<ApprovalRecord?> ReadAsync(string id, CancellationToken ct = default)
    {
        await using var db = await ConnectAsync(ct);
        return await ReadAsync(db, null, id, ct);
    }

    public async Task<bool> CompareExchangeAsync(string id, long expectedVersion, ApprovalRecord next, CancellationToken ct)
    {
        next.ValidateOperation();
        if (next.Id != id || next.Version != checked(expectedVersion + 1)) throw new ArgumentException("Invalid approval identity or next version.");
        await using var db = await ConnectAsync(ct);
        using var tx = db.BeginTransaction();
        var current = await ReadAsync(db, tx, id, ct);
        if (current is null || current.Version != expectedVersion) return false;
        if (current.Binding != next.Binding || current.ExpiresAt != next.ExpiresAt || current.Operation != next.Operation)
            throw new ArgumentException("Approval execution binding, operation snapshot and expiry are immutable.");
        await using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "UPDATE approvals SET version=$next,body=$body WHERE id=$id AND version=$expected";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$expected", expectedVersion);
        command.Parameters.AddWithValue("$next", next.Version); command.Parameters.AddWithValue("$body", JsonSerializer.Serialize(next));
        if (await command.ExecuteNonQueryAsync(ct) != 1) return false;
        await AppendAsync(db, tx, next, ct); await tx.CommitAsync(ct); return true;
    }

    public async Task<IReadOnlyList<ApprovalOutboxItem>> ReadOutboxAsync(int limit = 100, CancellationToken ct = default)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var db = await ConnectAsync(ct); await using var command = db.CreateCommand();
        command.CommandText = "SELECT sequence,body FROM approval_outbox ORDER BY sequence LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<ApprovalOutboxItem>();
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetInt64(0), JsonSerializer.Deserialize<ApprovalRecord>(reader.GetString(1))!));
        return result;
    }

    /// <summary>Acknowledge only after the consumer durably deduplicates (approval ID, version).</summary>
    public async Task AcknowledgeAsync(long sequence, CancellationToken ct = default)
    {
        await using var db = await ConnectAsync(ct); await using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM approval_outbox WHERE sequence=$sequence";
        command.Parameters.AddWithValue("$sequence", sequence); await command.ExecuteNonQueryAsync(ct);
    }
    private async Task<SqliteConnection> ConnectAsync(CancellationToken ct)
    {
        var db = new SqliteConnection(_connectionString);
        try { await db.OpenAsync(ct); return db; } catch { await db.DisposeAsync(); throw; }
    }
    private static async Task<ApprovalRecord?> ReadAsync(SqliteConnection db, SqliteTransaction? tx, string id, CancellationToken ct)
    {
        await using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT body FROM approvals WHERE id=$id"; command.Parameters.AddWithValue("$id", id);
        var body = await command.ExecuteScalarAsync(ct) as string;
        return body is null ? null : JsonSerializer.Deserialize<ApprovalRecord>(body);
    }
    private static async Task AppendAsync(SqliteConnection db, SqliteTransaction tx, ApprovalRecord record, CancellationToken ct)
    {
        await using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "INSERT INTO approval_outbox(approval_id,version,body) VALUES($id,$version,$body)";
        command.Parameters.AddWithValue("$id", record.Id); command.Parameters.AddWithValue("$version", record.Version);
        command.Parameters.AddWithValue("$body", JsonSerializer.Serialize(record)); await command.ExecuteNonQueryAsync(ct);
    }
}

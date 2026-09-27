using System.Text.Json;

namespace PuddingApproval.Sqlite;

public sealed partial class SqliteApprovalStore
{
    public async Task<ApprovalInboxPage> ReadPendingAsync(ApprovalInboxScope scope, string? afterId = null,
        int limit = 25, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (new[] { scope.WorkspaceId, scope.AgentId, scope.SessionId }.Any(string.IsNullOrWhiteSpace)
            || afterId is not null && string.IsNullOrWhiteSpace(afterId))
            throw new ArgumentException("A complete approval scope and a nonempty cursor are required.");
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var db = await ConnectAsync(ct); await using var command = db.CreateCommand();
        command.CommandText = """
            SELECT body FROM approvals
            WHERE json_extract(body,'$.Binding.WorkspaceId')=$workspace
                AND json_extract(body,'$.Binding.AgentId')=$agent
                AND json_extract(body,'$.Binding.SessionId')=$session
                AND json_extract(body,'$.State')=$state AND id > $after
            ORDER BY id LIMIT $limit
            """;
        command.Parameters.AddWithValue("$workspace", scope.WorkspaceId);
        command.Parameters.AddWithValue("$agent", scope.AgentId);
        command.Parameters.AddWithValue("$session", scope.SessionId);
        command.Parameters.AddWithValue("$state", (int)ApprovalState.Pending);
        command.Parameters.AddWithValue("$after", afterId ?? "");
        command.Parameters.AddWithValue("$limit", limit + 1);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<ApprovalRecord>();
        while (await reader.ReadAsync(ct))
        {
            if (items.Count == limit) return new(items.AsReadOnly(), items[^1].Id);
            items.Add(JsonSerializer.Deserialize<ApprovalRecord>(reader.GetString(0))
                ?? throw new InvalidDataException("Invalid stored approval record."));
        }
        return new(items.AsReadOnly(), null);
    }
}

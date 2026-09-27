namespace PuddingChat;

/// <summary>Exact invocation identity. A reusable child session is not a run identity.</summary>
public sealed record SubAgentInspectionKey(RoleKey Role, string ParentSessionId, string RunId);
public sealed record SubAgentInspection(SubAgentInspectionKey Key, string Status, string Task,
    string? Output, ProcessItem[] Activities, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt = null,
    int? Rounds = null, int? ToolCalls = null, string? Error = null, string? ArchiveWarning = null);

public interface ISubAgentInspectionClient
{
    /// <summary>Core must validate workspace, role, parent session and archive identity before returning data.</summary>
    Task<SubAgentInspection> ReadAsync(SubAgentInspectionKey key, CancellationToken ct);
}

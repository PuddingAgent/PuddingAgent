namespace PuddingChat;

public sealed record Workspace(string WorkspaceId, string Name);
public sealed record Agent(string AgentId, string Name, string? DisplayName = null, string? Description = null,
    string? AvatarUrl = null, string? SourceTemplateId = null, string? MainSessionId = null, bool IsEnabled = true, bool IsFrozen = false)
{
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
}
public sealed record RoleKey(string WorkspaceId, string AgentId);
public sealed record AgentStatus(string AgentId, string Status, string Summary, int UnreadCount);
public sealed record ProcessItem(string Id, string Kind, string Status, string Text, long Sequence,
    string? Name = null, string? Arguments = null, string? Output = null, int? ExitCode = null,
    string? Message = null, string? ToolCallId = null, string? TurnId = null, string? DelegationRunId = null);
public sealed record ProcessSummary(int TotalItems, int ToolCalls, int FailedTools, bool HasDetails);
public sealed record TurnOutcome(string Status, string? ErrorCode, string? ErrorMessage);
public sealed record ContentPart(string Type, string? ArtifactId, string? Detail);
public sealed record ChatMessage(string MessageId, string? RunId, string Role, string SourceName,
    DateTimeOffset CreatedAt, string Content, string Status, ProcessItem[] ProcessItems,
    string? TurnId = null, ProcessSummary? ProcessSummary = null, TurnOutcome? TurnOutcome = null, ContentPart[]? ContentParts = null);
public sealed record EventWindow(string TurnId, long ThroughSequence, long MinSequence, long MaxSequence, bool HasMoreBefore);
public sealed record OutputSnapshot(string Markdown, ProcessItem[] ProcessItems, EventWindow? Window = null);
public sealed record ActiveRun(string RunId, string Status, string StatusText, string Summary, OutputSnapshot OutputSnapshot);
public sealed record Conversation(string WorkspaceId, string AgentId, string MainSessionId, ChatMessage[] Messages,
    ActiveRun? ActiveRun, long EventCursor);
public sealed record ProcessDetails(string MessageId, ProcessItem[] ProcessItems, EventWindow? Window = null);
public sealed record Acceptance(string ConversationId, string MessageId, string[] TurnIds, long AcceptedSequence);
public sealed record PendingSend(RoleKey Role, string ConversationId, string Text, string ClientRequestId, string ClientMessageId)
{
    public static PendingSend Create(RoleKey role, string conversation, string text) =>
        new(role, conversation, text, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
}

public interface IChatClient : IDisposable
{
    Task LoginAsync(string user, string password, CancellationToken ct);
    Task<Workspace[]> GetWorkspacesAsync(CancellationToken ct);
    Task<Agent[]> GetAgentsAsync(string workspace, CancellationToken ct);
    Task<AgentStatus[]> GetStatusesAsync(string workspace, CancellationToken ct);
    Task<Conversation?> GetConversationAsync(RoleKey role, long? cursor, CancellationToken ct);
    Task<string> EnsureSessionAsync(RoleKey role, Agent agent, CancellationToken ct);
    Task<Acceptance> SendAsync(PendingSend send, CancellationToken ct);
    Task CancelAsync(string workspace, string conversation, string turn, CancellationToken ct);
    Task<ProcessDetails> GetProcessAsync(RoleKey role, string message, CancellationToken ct);
}

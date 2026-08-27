namespace PuddingCode.Platform;

/// <summary>
/// Atomically appends a passive inbound message to a canonical Conversation.
/// Unlike SubmitTurn, this contract never creates a Turn or execution command.
/// </summary>
public interface IConversationNotificationStore
{
    Task<ConversationNotificationResult> AcceptAsync(
        ConversationNotificationRequest request,
        CancellationToken ct = default);
}

public sealed record ConversationNotificationRequest(
    string WorkspaceId,
    string ConversationId,
    string AgentInstanceId,
    string MessageId,
    string Content,
    long CreatedAt,
    string? UserId,
    IReadOnlyDictionary<string, string>? Metadata,
    string? CorrelationId,
    string? CausationId);

public sealed record ConversationNotificationResult(
    string ConversationId,
    string MessageId,
    long AcceptedSequence,
    bool AlreadyAccepted);

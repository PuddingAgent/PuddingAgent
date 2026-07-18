namespace PuddingCode.Platform;

/// <summary>
/// Executes a user-authored system command without creating an Agent turn.
/// </summary>
public interface ISystemCommandHandler
{
    Task<SystemCommandResult> HandleAsync(
        SystemCommandRequest request,
        CancellationToken ct = default);
}

public sealed record SystemCommandRequest(
    string ConversationId,
    string WorkspaceId,
    string AgentId,
    string UserId,
    string ClientRequestId,
    string ClientMessageId,
    string ResponseMessageId,
    string CommandText);

public sealed record SystemCommandResult(
    string ConversationId,
    string ClientMessageId,
    string ResponseMessageId,
    string Command,
    string Message,
    string RuntimeMode);

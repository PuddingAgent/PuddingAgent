using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingPlatform.Controllers.Api;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatform.Services;

/// <summary>
/// Encapsulates chat command acceptance: ID generation, idempotency, persistence, and turn.accepted event emission.
/// Shared by ChatApiController. All operations within a single call are logically atomic.
/// </summary>
public sealed class ChatCommandAcceptanceService
{
    private readonly IChatCommandStore _commandStore;
    private readonly ISessionStateManager _ssm;
    private readonly ILogger<ChatCommandAcceptanceService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public ChatCommandAcceptanceService(
        IChatCommandStore commandStore,
        ISessionStateManager ssm,
        ILogger<ChatCommandAcceptanceService> logger)
    {
        _commandStore = commandStore;
        _ssm = ssm;
        _logger = logger;
    }

    internal async Task<ChatCommandAcceptanceResult> AcceptAsync(
        string workspaceId,
        AdminChatRequest req,
        ChatAgentDispatch primaryDispatch,
        int fanoutCount,
        string channelId,
        string userExternalId,
        string clientRequestId,
        CancellationToken ct)
    {
        var turnId = Guid.NewGuid().ToString("N");
        var commandId = Guid.NewGuid().ToString("N");
        var messageId = Guid.NewGuid().ToString("N");

        var sessionId = req.SessionId ?? Guid.NewGuid().ToString("N");

        if (!string.IsNullOrWhiteSpace(req.ClientRequestId))
        {
            var existing = await _commandStore.FindByClientRequestIdAsync(req.ClientRequestId, workspaceId, ct);
            if (existing is not null)
            {
                _logger.LogInformation(
                    "[Chat:Queue] Idempotent hit cmd={CommandId} clientRequestId={ClientRequestId}",
                    existing.CommandId, req.ClientRequestId);
                return new ChatCommandAcceptanceResult
                {
                    Status = existing.Status == "pending" ? "accepted" : existing.Status,
                    CommandId = existing.CommandId,
                    MessageId = existing.MessageId,
                    TurnId = existing.TurnId,
                    SessionId = existing.SessionId,
                    EventCursor = long.TryParse(existing.EventCursor, out var c) ? c : null,
                    ClientRequestId = existing.ClientRequestId,
                    Idempotent = true,
                };
            }
        }

        var payload = new Dictionary<string, object?>
        {
            ["channelId"] = channelId,
            ["userExternalId"] = userExternalId,
            ["messageText"] = req.MessageText,
            ["llmConfig"] = primaryDispatch.LlmConfig,
            ["agentTemplateId"] = primaryDispatch.AgentTemplateId,
            ["agentInstanceId"] = primaryDispatch.AgentId,
            ["capabilityPolicy"] = primaryDispatch.CapabilityPolicy,
            ["toolDefinitions"] = primaryDispatch.ToolDefinitions,
            ["skillPackages"] = primaryDispatch.SkillPackages,
            ["metadata"] = ChatMessageExecutionService.BuildChatIngressMetadata(req, primaryDispatch,
                fanoutIndex: 0, fanoutCount, turnId: turnId, clientRequestId: clientRequestId),
        };

        var command = new ChatCommandRecord
        {
            CommandId = commandId,
            ClientRequestId = clientRequestId,
            WorkspaceId = workspaceId,
            SessionId = sessionId,
            MessageId = messageId,
            TurnId = turnId,
            AgentInstanceId = primaryDispatch.AgentId,
            AgentTemplateId = primaryDispatch.AgentTemplateId,
            UserId = userExternalId,
            PayloadJson = JsonSerializer.Serialize(payload, JsonOpts),
            Status = "pending",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        await _commandStore.SaveAsync(command, ct);

        var acceptedFrame = ServerSentEventFrame.Json("turn.accepted", new
        {
            commandId,
            messageId,
            turnId,
            sessionId,
            clientRequestId,
        });
        await _ssm.AppendAsync(sessionId, workspaceId, acceptedFrame, ct);

        var eventCursor = await _ssm.GetLatestSequenceNumAsync(sessionId, ct);

        _logger.LogInformation(
            "[Chat:Queue] Command queued cmd={CommandId} turn={TurnId} msg={MessageId} session={SessionId}",
            commandId, turnId, messageId, sessionId);

        return new ChatCommandAcceptanceResult
        {
            Status = "accepted",
            CommandId = commandId,
            MessageId = messageId,
            TurnId = turnId,
            SessionId = sessionId,
            EventCursor = eventCursor,
            ClientRequestId = clientRequestId,
            Idempotent = false,
        };
    }
}

public sealed record ChatCommandAcceptanceResult
{
    public string Status { get; init; } = "accepted";
    public string CommandId { get; init; } = string.Empty;
    public string MessageId { get; init; } = string.Empty;
    public string TurnId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public long? EventCursor { get; init; }
    public string? ClientRequestId { get; init; }
    public bool Idempotent { get; init; }
}

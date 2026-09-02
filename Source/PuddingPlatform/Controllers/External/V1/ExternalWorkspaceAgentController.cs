using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Security;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;
using PuddingPlatform.Services.ExternalApi;
using PuddingPlatform.Services.MessageGateway;
using PuddingPlatform.Services.Security;

namespace PuddingPlatform.Controllers.External.V1;

/// <summary>
/// ADR-082 External workspace/Agent/message API. It exposes safe directory projections and
/// routes messages through Message Fabric -> canonical Conversation acceptance. A delivery ACK
/// is reported separately from the Agent execution terminal state.
/// </summary>
[ApiController]
[Route("api/external/v1/workspaces")]
[ServiceFilter(typeof(ExternalApiGateFilter))]
public sealed class ExternalWorkspaceAgentController(
    IDbContextFactory<PlatformDbContext> dbFactory,
    IWorkspaceAgentCatalog agentCatalog,
    IMessageSystem messageSystem,
    ExternalApiIdempotencyStore idempotency,
    ExternalTaskApiOptionsProvider optionsProvider) : ControllerBase
{
    private const int MaxMessageChars = 65_536;
    private static readonly JsonSerializerOptions StableJson = new(JsonSerializerDefaults.Web);

    /// <summary>GET /workspaces — only workspaces present in the Token allow-list.</summary>
    [HttpGet]
    [Authorize(Policy = ExternalAccessTokenPolicyNames.ExternalWorkspacesRead)]
    public async Task<ActionResult<IReadOnlyList<ExternalWorkspaceDto>>> ListWorkspaces(
        CancellationToken ct = default)
    {
        var allowed = User.FindAll(ExternalAccessTokenClaimNames.Workspace)
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var workspaces = await db.Workspaces.AsNoTracking()
            .Where(workspace => allowed.Contains(workspace.WorkspaceId))
            .OrderBy(workspace => workspace.WorkspaceId)
            .ToListAsync(ct);
        return Ok(workspaces.Select(ToDto).ToList());
    }

    /// <summary>GET /workspaces/{workspaceId}。scope: workspaces.read + workspace allow-list。</summary>
    [HttpGet("{workspaceId}")]
    [Authorize(Policy = ExternalAccessTokenPolicyNames.ExternalWorkspaceRead)]
    public async Task<IActionResult> GetWorkspace(string workspaceId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var workspace = await db.Workspaces.AsNoTracking()
            .FirstOrDefaultAsync(item => item.WorkspaceId == workspaceId, ct);
        return workspace is null
            ? NotFound(Error("workspace.not_found", $"Workspace '{workspaceId}' not found."))
            : Ok(ToDto(workspace));
    }

    /// <summary>GET /workspaces/{workspaceId}/agents。scope: agents.read + workspace allow-list。</summary>
    [HttpGet("{workspaceId}/agents")]
    [Authorize(Policy = ExternalAccessTokenPolicyNames.ExternalAgentsRead)]
    public async Task<ActionResult<IReadOnlyList<ExternalAgentDto>>> ListAgents(
        string workspaceId,
        [FromQuery] bool enabledOnly = false,
        CancellationToken ct = default)
    {
        if (!await WorkspaceExistsAsync(workspaceId, ct))
            return NotFound(Error("workspace.not_found", $"Workspace '{workspaceId}' not found."));

        var agents = await agentCatalog.ListAgentsAsync(workspaceId, ct);
        var items = agents
            .Where(agent => !enabledOnly || (agent.IsEnabled && !agent.IsFrozen))
            .OrderBy(agent => agent.AgentId, StringComparer.Ordinal)
            .Select(ToDto)
            .ToList();
        return Ok(items);
    }

    /// <summary>GET /workspaces/{workspaceId}/agents/{agentId}。scope: agents.read。</summary>
    [HttpGet("{workspaceId}/agents/{agentId}")]
    [Authorize(Policy = ExternalAccessTokenPolicyNames.ExternalAgentsRead)]
    public async Task<IActionResult> GetAgent(
        string workspaceId,
        string agentId,
        CancellationToken ct = default)
    {
        if (!await WorkspaceExistsAsync(workspaceId, ct))
            return NotFound(Error("workspace.not_found", $"Workspace '{workspaceId}' not found."));

        var agent = await FindAgentAsync(workspaceId, agentId, ct);
        return agent is null
            ? NotFound(Error("agent.not_found", $"Agent '{agentId}' not found in workspace '{workspaceId}'."))
            : Ok(ToDto(agent));
    }

    /// <summary>
    /// POST /workspaces/{workspaceId}/agents/{agentId}/messages — asynchronous canonical ingress.
    /// scope: messages.send；requires Idempotency-Key。
    /// </summary>
    [HttpPost("{workspaceId}/agents/{agentId}/messages")]
    [Authorize(Policy = ExternalAccessTokenPolicyNames.ExternalMessagesSend)]
    public async Task<IActionResult> SendMessage(
        string workspaceId,
        string agentId,
        [FromBody] ExternalSendAgentMessageRequest request,
        CancellationToken ct = default)
    {
        var content = request.Content?.Trim();
        if (string.IsNullOrWhiteSpace(content))
            return InvalidRequest("content 必填。");
        if (content.Length > MaxMessageChars)
            return InvalidRequest($"content 最长 {MaxMessageChars} 字符。");

        var gate = await TryClaimIdempotencyAsync(request, ct);
        if (gate.EarlyResult is not null)
            return gate.EarlyResult;
        if (gate.ReplayMessageId is not null)
        {
            var replay = await BuildReceiptAsync(workspaceId, agentId, gate.ReplayMessageId, ct);
            if (replay is null)
                return Conflict(Error("external.idempotency_resource_missing", "幂等记录存在，但原消息事实不可用。"));

            Response.Headers.Location = replay.StatusUrl;
            return StatusCode(StatusCodes.Status202Accepted, replay);
        }

        // Replay must win over mutable availability. Once a message was accepted, disabling the
        // Agent later must not turn an identical retry into a different response or send again.
        (bool IsEnabled, bool IsFrozen)? workspaceState;
        try
        {
            workspaceState = await GetWorkspaceStateAsync(workspaceId, ct);
        }
        catch
        {
            await ReleaseIdempotencyAsync(gate.Key);
            throw;
        }
        if (workspaceState is null)
        {
            await ReleaseIdempotencyAsync(gate.Key);
            return NotFound(Error("workspace.not_found", $"Workspace '{workspaceId}' not found."));
        }
        if (!workspaceState.Value.IsEnabled || workspaceState.Value.IsFrozen)
        {
            await ReleaseIdempotencyAsync(gate.Key);
            return Conflict(Error("workspace.unavailable", "Workspace 已停用或冻结，不能接收外部消息。"));
        }

        WorkspaceAgentDto? agent;
        try
        {
            agent = await FindAgentAsync(workspaceId, agentId, ct);
        }
        catch
        {
            await ReleaseIdempotencyAsync(gate.Key);
            throw;
        }
        if (agent is null)
        {
            await ReleaseIdempotencyAsync(gate.Key);
            return NotFound(Error("agent.not_found", $"Agent '{agentId}' not found in workspace '{workspaceId}'."));
        }
        if (!agent.IsEnabled || agent.IsFrozen)
        {
            await ReleaseIdempotencyAsync(gate.Key);
            return Conflict(Error("agent.unavailable", "Agent 已停用或冻结，不能接收外部消息。"));
        }

        var messageId = CreateStableMessageId(gate.Key!);
        try
        {
            var result = await messageSystem.SendAsync(new MessageEnvelope
            {
                MessageId = messageId,
                From = new MessageAddress
                {
                    Kind = MessageEndpointKinds.Connector,
                    Id = ActorId,
                    WorkspaceId = workspaceId,
                    DisplayName = TokenName,
                },
                To =
                [
                    new MessageAddress
                    {
                        Kind = MessageEndpointKinds.Agent,
                        Id = agent.AgentId,
                        WorkspaceId = workspaceId,
                        DisplayName = agent.DisplayName ?? agent.Name,
                    },
                ],
                RoomId = "external-api",
                Audience = MessageAudiences.Direct,
                Visibility = MessageVisibilities.Private,
                ContentType = MessageContentTypes.Text,
                Content = content,
                CorrelationId = messageId,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["source"] = "external.api",
                    ["intent"] = MessageIntents.Ask,
                    [MessageDeliveryPolicy.RequiresResponseMetadataKey] = "true",
                    [MessageDeliveryPolicy.CanonicalTurnMetadataKey] = "true",
                    ["external_api_version"] = "v1",
                },
            }, ct);

            await idempotency.CompleteAsync(
                TokenId,
                "POST",
                CanonicalRoute,
                gate.Key!,
                StatusCodes.Status202Accepted,
                result.MessageId,
                ct);

            var now = DateTimeOffset.UtcNow;
            var receipt = new ExternalAgentMessageReceiptDto
            {
                MessageId = result.MessageId,
                WorkspaceId = workspaceId,
                AgentId = agent.AgentId,
                DeliveryStatus = "queued",
                Deliveries = result.DeliveryIds.Select(id => new ExternalMessageDeliveryDto
                {
                    DeliveryId = id,
                    Status = MessageDeliveryStatuses.Queued,
                    AttemptCount = 0,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                }).ToList(),
                AcceptedAtUtc = now,
                StatusUrl = StatusUrl(workspaceId, agent.AgentId, result.MessageId),
            };
            Response.Headers.Location = receipt.StatusUrl;
            return StatusCode(StatusCodes.Status202Accepted, receipt);
        }
        catch
        {
            await ReleaseIdempotencyAsync(gate.Key);
            throw;
        }
    }

    /// <summary>
    /// GET message receipt — delivery state plus canonical command terminal reply when available.
    /// scope: messages.send; only messages authored by the current Token actor are visible.
    /// </summary>
    [HttpGet("{workspaceId}/agents/{agentId}/messages/{messageId}")]
    [Authorize(Policy = ExternalAccessTokenPolicyNames.ExternalMessagesSend)]
    public async Task<IActionResult> GetMessage(
        string workspaceId,
        string agentId,
        string messageId,
        CancellationToken ct = default)
    {
        var receipt = await BuildReceiptAsync(workspaceId, agentId, messageId, ct);
        return receipt is null
            ? NotFound(Error("message.not_found", $"Message '{messageId}' not found."))
            : Ok(receipt);
    }

    private string TokenId
        => User.FindFirstValue(ExternalAccessTokenClaimNames.TokenId) ?? string.Empty;

    private string ActorId
        => $"{ExternalAccessTokenDefaults.ActorIdPrefix}{TokenId}";

    private string TokenName
        => User.FindFirstValue(ClaimTypes.Name) ?? "external";

    private string CanonicalRoute => Request.Path.Value ?? "/";

    private async Task<bool> WorkspaceExistsAsync(string workspaceId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Workspaces.AsNoTracking()
            .AnyAsync(workspace => workspace.WorkspaceId == workspaceId, ct);
    }

    private async Task<(bool IsEnabled, bool IsFrozen)?> GetWorkspaceStateAsync(
        string workspaceId,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var workspace = await db.Workspaces.AsNoTracking()
            .FirstOrDefaultAsync(item => item.WorkspaceId == workspaceId, ct);
        return workspace is null
            ? null
            : (workspace.IsEnabled, workspace.IsFrozen);
    }

    private async Task<WorkspaceAgentDto?> FindAgentAsync(
        string workspaceId,
        string agentId,
        CancellationToken ct)
    {
        var agents = await agentCatalog.ListAgentsAsync(workspaceId, ct);
        return agents.FirstOrDefault(agent =>
            string.Equals(agent.AgentId, agentId, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record IdempotencyGate(
        string? Key,
        string? ReplayMessageId,
        IActionResult? EarlyResult);

    private async Task<IdempotencyGate> TryClaimIdempotencyAsync(
        ExternalSendAgentMessageRequest request,
        CancellationToken ct)
    {
        var key = Request.Headers["Idempotency-Key"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(key))
            return new IdempotencyGate(null, null, InvalidRequest("消息发送必须携带 Idempotency-Key（≤128 字符）。"));
        if (key.Length > 128 || key.Any(char.IsControl))
            return new IdempotencyGate(null, null, InvalidRequest("Idempotency-Key 最长 128 且不允许控制字符。"));

        var body = JsonSerializer.Serialize(request, StableJson);
        var retention = TimeSpan.FromDays(Math.Max(1, optionsProvider.Current.IdempotencyRetentionDays));
        var claim = await idempotency.TryClaimAsync(
            TokenId,
            "POST",
            CanonicalRoute,
            key,
            body,
            retention,
            ct);
        return claim.Outcome switch
        {
            ExternalIdempotencyOutcome.Conflict => new IdempotencyGate(
                key,
                null,
                Conflict(Error("external.idempotency_conflict", "同 Idempotency-Key 已用于不同请求体。"))),
            ExternalIdempotencyOutcome.InProgress => new IdempotencyGate(
                key,
                null,
                Conflict(Error("external.idempotency_in_progress", "同 Idempotency-Key 请求正在处理中。"))),
            ExternalIdempotencyOutcome.Replay => new IdempotencyGate(key, claim.ResourceId, null),
            _ => new IdempotencyGate(key, null, null),
        };
    }

    private Task ReleaseIdempotencyAsync(string? key)
        => string.IsNullOrEmpty(key)
            ? Task.CompletedTask
            : idempotency.ReleaseAsync(TokenId, "POST", CanonicalRoute, key, CancellationToken.None);

    private string CreateStableMessageId(string idempotencyKey)
    {
        var hash = ExternalApiIdempotencyStore.ComputeKeyHash(
            TokenId,
            "POST",
            CanonicalRoute,
            idempotencyKey);
        return $"extmsg-{hash[..32].ToLowerInvariant()}";
    }

    private async Task<ExternalAgentMessageReceiptDto?> BuildReceiptAsync(
        string workspaceId,
        string agentId,
        string messageId,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var message = await db.RoomMessages.AsNoTracking()
            .FirstOrDefaultAsync(item =>
                item.WorkspaceId == workspaceId
                && item.MessageId == messageId
                && item.FromKind == MessageEndpointKinds.Connector
                && item.FromId == ActorId,
                ct);
        if (message is null)
            return null;

        var deliveries = await db.MessageDeliveries.AsNoTracking()
            .Where(item =>
                item.WorkspaceId == workspaceId
                && item.MessageId == messageId
                && item.TargetKind == MessageEndpointKinds.Agent
                && item.TargetId == agentId)
            .OrderBy(item => item.DeliveryId)
            .ToListAsync(ct);
        if (deliveries.Count == 0)
            return null;

        var candidates = await db.ChatExecutionCommands.AsNoTracking()
            .Where(command =>
                command.WorkspaceId == workspaceId
                && command.AgentInstanceId == agentId
                && command.MetadataJson != null
                && command.MetadataJson.Contains(messageId))
            .OrderByDescending(command => command.Id)
            .Take(20)
            .ToListAsync(ct);
        var command = candidates.FirstOrDefault(candidate =>
        {
            var metadata = DeserializeMetadata(candidate.MetadataJson);
            return string.Equals(
                Get(metadata, MessageFabricTurnMetadata.MessageId),
                messageId,
                StringComparison.Ordinal);
        });

        ConversationTerminalPresentation? presentation = null;
        if (command?.TerminalSequence is long terminalSequence)
        {
            var terminalEvent = await db.ConversationEvents.AsNoTracking()
                .FirstOrDefaultAsync(evt =>
                    evt.ConversationId == command.SessionId
                    && evt.Sequence == terminalSequence,
                    ct);
            if (terminalEvent is not null)
                presentation = ConversationTerminalMessageFormatter.Parse(terminalEvent.Payload);
        }

        return new ExternalAgentMessageReceiptDto
        {
            MessageId = messageId,
            WorkspaceId = workspaceId,
            AgentId = agentId,
            DeliveryStatus = AggregateDeliveryStatus(deliveries.Select(item => item.Status)),
            ExecutionStatus = command?.Status,
            ConversationId = command?.SessionId,
            Reply = presentation?.Content,
            ReplySummary = presentation?.Summary,
            ReplyIsError = presentation?.IsError,
            Deliveries = deliveries.Select(item => new ExternalMessageDeliveryDto
            {
                DeliveryId = item.DeliveryId,
                Status = item.Status,
                AttemptCount = item.AttemptCount,
                CreatedAtUtc = FromUnixMilliseconds(item.CreatedAt),
                UpdatedAtUtc = FromUnixMilliseconds(item.UpdatedAt),
                AcknowledgedAtUtc = item.AckAt.HasValue
                    ? FromUnixMilliseconds(item.AckAt.Value)
                    : null,
            }).ToList(),
            AcceptedAtUtc = FromUnixMilliseconds(message.CreatedAt),
            CompletedAtUtc = command?.CompletedAt is long completedAt
                ? FromUnixMilliseconds(completedAt)
                : null,
            StatusUrl = StatusUrl(workspaceId, agentId, messageId),
        };
    }

    private static string AggregateDeliveryStatus(IEnumerable<string> statuses)
    {
        var values = statuses.ToList();
        if (values.Any(status => status is MessageDeliveryStatuses.DeadLetter
                or MessageDeliveryStatuses.Failed
                or MessageDeliveryStatuses.Cancelled
                or MessageDeliveryStatuses.Expired))
            return "failed";
        if (values.Any(status => status == MessageDeliveryStatuses.Retrying))
            return MessageDeliveryStatuses.Retrying;
        if (values.Any(status => status == MessageDeliveryStatuses.Delivering))
            return MessageDeliveryStatuses.Delivering;
        if (values.Any(status => status == MessageDeliveryStatuses.Queued))
            return MessageDeliveryStatuses.Queued;
        return values.Count > 0 && values.All(status => status == MessageDeliveryStatuses.Delivered)
            ? "accepted"
            : "unknown";
    }

    private static Dictionary<string, string> DeserializeMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, StableJson)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static string? Get(IReadOnlyDictionary<string, string> metadata, string key)
        => metadata.TryGetValue(key, out var value) ? value : null;

    private static DateTimeOffset FromUnixMilliseconds(long value)
        => DateTimeOffset.FromUnixTimeMilliseconds(value);

    private static string StatusUrl(string workspaceId, string agentId, string messageId)
        => $"/api/external/v1/workspaces/{Uri.EscapeDataString(workspaceId)}/agents/{Uri.EscapeDataString(agentId)}/messages/{Uri.EscapeDataString(messageId)}";

    private static ExternalWorkspaceDto ToDto(PuddingPlatform.Data.Entities.WorkspaceEntity workspace)
        => new()
        {
            WorkspaceId = workspace.WorkspaceId,
            Slug = workspace.Slug,
            Name = workspace.Name,
            Description = workspace.Description,
            IsEnabled = workspace.IsEnabled,
            IsFrozen = workspace.IsFrozen,
            CreatedAtUtc = workspace.CreatedAt,
        };

    private static ExternalAgentDto ToDto(WorkspaceAgentDto agent)
        => new()
        {
            AgentId = agent.AgentId,
            Name = agent.Name,
            DisplayName = agent.DisplayName,
            Description = agent.Description,
            AvatarUrl = agent.AvatarUrl,
            Role = agent.Role,
            PreferredProviderId = agent.PreferredProviderId,
            PreferredModelId = agent.PreferredModelId,
            IsEnabled = agent.IsEnabled,
            IsFrozen = agent.IsFrozen,
            CapabilityIds = agent.SelectedCapabilityIds ?? [],
            CreatedAtUtc = agent.CreatedAt,
            UpdatedAtUtc = agent.UpdatedAt,
        };

    private BadRequestObjectResult InvalidRequest(string message)
        => BadRequest(Error("external.invalid_request", message));

    private static ExternalErrorResponse Error(string code, string? message = null)
        => new()
        {
            Code = code,
            Message = message,
            TraceId = Activity.Current?.Id,
        };
}

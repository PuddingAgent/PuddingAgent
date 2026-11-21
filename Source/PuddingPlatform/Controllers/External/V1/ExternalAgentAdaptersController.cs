using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingAgents;
using PuddingAgents.Contracts;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Security;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.ExternalApi;
using PuddingPlatform.Services.Security;

namespace PuddingPlatform.Controllers.External.V1;

[ApiController]
[Route("api/external/v1/agent-adapters")]
[ApiExplorerSettings(GroupName = DocumentName)]
[Authorize(Policy = ExternalAccessTokenPolicyNames.ExternalAgentAdaptersConnect)]
[ServiceFilter(typeof(ExternalApiGateFilter))]
public sealed class ExternalAgentAdaptersController(
    IDbContextFactory<PlatformDbContext> databaseFactory,
    AgentAdapterRegistry registry,
    IMessageSystem messages,
    ExternalApiIdempotencyStore idempotency,
    ExternalTaskApiOptionsProvider options,
    TimeProvider clock) : ControllerBase
{
    public const string DocumentName = "agent-adapters-v1";
    private const string ExternalEndpointKind = "external_agent";
    private const string MessagesRoom = "external-agent-adapters";
    private const string DelegationsRoom = "external-agent-delegations";
    private const string MessageRoute = "/api/external/v1/agent-adapters/messages";
    private const string DelegationRoute = "/api/external/v1/agent-adapters/delegations";

    [HttpPost("register")]
    [ProducesResponseType<AgentAdapterConnectionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ExternalErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register([FromBody] RegisterAgentAdapterRequest request, CancellationToken ct)
    {
        await using var db = await databaseFactory.CreateDbContextAsync(ct);
        var adapter = await FindAdapterAsync(db, ct);
        if (adapter is null)
            return Forbid();

        var binding = Bind(adapter);
        AgentAdapterLease lease;
        try { lease = registry.Register(binding, request.ReplacesLeaseId); }
        catch (AgentBackendException error) { return Conflict(new ExternalErrorResponse { Code = error.Code }); }
        try
        {
            var now = clock.GetUtcNow();
            await db.ExternalAgentAdapters
                .Where(x => x.TokenId == adapter.TokenId && x.RegisteredAtUtc == null)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.RegisteredAtUtc, now), ct);
        }
        catch
        {
            registry.Release(binding, lease.LeaseId);
            throw;
        }
        return Ok(ToResponse(lease));
    }

    [HttpPost("renew")]
    [ProducesResponseType<AgentAdapterConnectionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ExternalErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Renew([FromBody] AgentAdapterLeaseRequest request, CancellationToken ct)
    {
        await using var db = await databaseFactory.CreateDbContextAsync(ct);
        var adapter = await FindAdapterAsync(db, ct);
        if (adapter is null)
            return Forbid();
        var lease = registry.Renew(Bind(adapter), request.LeaseId);
        return lease is null ? RejectedLease() : Ok(ToResponse(lease));
    }

    [HttpPost("disconnect")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ExternalErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Disconnect([FromBody] AgentAdapterLeaseRequest request, CancellationToken ct)
    {
        await using var db = await databaseFactory.CreateDbContextAsync(ct);
        var adapter = await FindAdapterAsync(db, ct);
        if (adapter is null)
            return Forbid();
        return registry.Release(Bind(adapter), request.LeaseId) ? NoContent() : RejectedLease();
    }

    [HttpGet("agents")]
    [ProducesResponseType<AgentAdapterDirectoryResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Directory(Guid leaseId, string? after = null, int limit = 20, CancellationToken ct = default)
    {
        if (after is { Length: > 128 } || limit is < 1 or > 100)
            return BadRequest(new ExternalErrorResponse { Code = "agent.adapter_invalid_cursor" });
        await using var db = await databaseFactory.CreateDbContextAsync(ct);
        var adapter = await FindAdapterAsync(db, ct);
        if (adapter is null)
            return Forbid();
        if (!OwnsLease(adapter, leaseId))
            return RejectedLease();

        var rows = await (
            from candidate in db.ExternalAgentAdapters.AsNoTracking()
            join token in db.ExternalAccessTokens.AsNoTracking() on candidate.TokenId equals token.TokenId
            where candidate.WorkspaceId == adapter.WorkspaceId && token.RevokedAtUtc == null &&
                (after == null || string.Compare(candidate.AgentId, after) > 0) &&
                db.AppUsers.Any(u => u.UserId == token.OwnerUserId && u.IsEnabled)
            orderby candidate.AgentId
            select new { candidate.AgentId, token.Name, token.ExpiresAtUtc })
            .Take(limit).ToListAsync(ct);
        if (!OwnsLease(adapter, leaseId))
            return RejectedLease();

        var now = clock.GetUtcNow();
        return Ok(new AgentAdapterDirectoryResponse(
            rows.Where(row => row.ExpiresAtUtc > now)
                .Select(row => new AgentAdapterDirectoryItem(row.AgentId, row.Name,
                    registry.Resolve(adapter.WorkspaceId, row.AgentId) is not null)).ToArray(),
            rows.Count < limit ? null : rows[^1].AgentId));
    }

    /// <summary>Polls this Agent's durable text inbox. Store nextCursor after processing the returned messages.</summary>
    [HttpGet("inbox")]
    [ProducesResponseType<AgentAdapterInboxResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Inbox(Guid leaseId, long after = 0, int limit = 20, CancellationToken ct = default)
    {
        if (after < 0 || limit is < 1 or > 100)
            return BadRequest(new ExternalErrorResponse { Code = "agent.adapter_invalid_cursor" });

        await using var db = await databaseFactory.CreateDbContextAsync(ct);
        var adapter = await FindAdapterAsync(db, ct);
        if (adapter is null)
            return Forbid();
        if (!OwnsLease(adapter, leaseId))
            return RejectedLease();

        var items = await (
            from delivery in db.MessageDeliveries.AsNoTracking()
            join message in db.RoomMessages.AsNoTracking() on delivery.MessageId equals message.MessageId
            where delivery.WorkspaceId == adapter.WorkspaceId && delivery.TargetKind == ExternalEndpointKind &&
                delivery.TargetId == adapter.AgentId && delivery.Id > after
            orderby delivery.Id
            select new AgentAdapterInboxMessage(delivery.Id, delivery.MessageId, message.FromId, message.Content,
                message.RoomId == DelegationsRoom ? "delegation" : "message"))
            .Take(limit).ToListAsync(ct);

        if (!OwnsLease(adapter, leaseId))
            return RejectedLease();
        return Ok(new AgentAdapterInboxResponse(items, items.Count == 0 ? after : items[^1].Sequence));
    }

    /// <summary>Queues a text notification for another registered adapter in the same workspace.</summary>
    [HttpPost("messages")]
    [ProducesResponseType<AgentAdapterMessageReceipt>(StatusCodes.Status202Accepted)]
    public Task<IActionResult> Send([FromBody] SendAgentAdapterMessageRequest request, CancellationToken ct)
        => EnqueueAsync(request, MessageRoute, MessagesRoom, MessageIntents.Inform, ct);

    /// <summary>Delegates a task to another adapter in the same workspace. Use the returned messageId to read its result.</summary>
    [HttpPost("delegations")]
    [ProducesResponseType<AgentAdapterMessageReceipt>(StatusCodes.Status202Accepted)]
    public Task<IActionResult> Delegate([FromBody] SendAgentAdapterMessageRequest request, CancellationToken ct)
        => EnqueueAsync(request, DelegationRoute, DelegationsRoom, MessageIntents.Ask, ct);

    /// <summary>Reads the current state and any terminal result of a delegation owned by the caller.</summary>
    [HttpGet("delegations/{messageId}")]
    [ProducesResponseType<AgentAdapterDelegationReceipt>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ReadDelegation(string messageId, Guid leaseId, CancellationToken ct)
    {
        await using var db = await databaseFactory.CreateDbContextAsync(ct);
        var adapter = await FindAdapterAsync(db, ct);
        if (adapter is null)
            return Forbid();
        if (!OwnsLease(adapter, leaseId))
            return RejectedLease();

        var receipt = await (
            from message in Delegations(db, adapter.WorkspaceId, messageId)
            where message.FromId == adapter.AgentId
            join delivery in db.MessageDeliveries.AsNoTracking() on message.MessageId equals delivery.MessageId
            where delivery.WorkspaceId == adapter.WorkspaceId && delivery.TargetKind == ExternalEndpointKind
            join terminal in db.ExternalAgentDelegationResults.AsNoTracking()
                on message.MessageId equals terminal.MessageId into results
            from terminal in results.DefaultIfEmpty()
            select new { delivery.TargetId, Result = terminal }).SingleOrDefaultAsync(ct);
        if (!OwnsLease(adapter, leaseId))
            return RejectedLease();
        if (receipt is null)
            return NotFound();
        return Ok(new AgentAdapterDelegationReceipt(messageId, receipt.TargetId,
            receipt.Result?.State ?? "queued", receipt.Result is { } result ? ToResult(result) : null));
    }

    /// <summary>Records the designated recipient's immutable terminal result. Exact retries are idempotent.</summary>
    [HttpPut("delegations/{messageId}/result")]
    [ProducesResponseType<AgentAdapterDelegationResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SettleDelegation(string messageId,
        [FromBody] SettleAgentAdapterDelegationRequest request, CancellationToken ct)
    {
        if (!AgentBinding.Valid(request.TurnId) ||
            request.State is not ("completed" or "failed" or "cancelled") ||
            (request.State == "completed"
                ? string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 16_000 || request.Reason is not null
                : request.Text is not null || !AgentBinding.Valid(request.Reason)))
            return BadRequest(new ExternalErrorResponse { Code = "agent.adapter_invalid_result" });

        await using var db = await databaseFactory.CreateDbContextAsync(ct);
        var adapter = await FindAdapterAsync(db, ct);
        if (adapter is null)
            return Forbid();
        if (!OwnsLease(adapter, request.LeaseId))
            return RejectedLease();

        var ownsTask = await Delegations(db, adapter.WorkspaceId, messageId).AnyAsync(message =>
            db.MessageDeliveries.Any(delivery => delivery.MessageId == message.MessageId &&
                delivery.WorkspaceId == adapter.WorkspaceId && delivery.TargetKind == ExternalEndpointKind &&
                delivery.TargetId == adapter.AgentId), ct);
        if (!ownsTask)
            return NotFound();
        if (!OwnsLease(adapter, request.LeaseId))
            return RejectedLease();

        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO external_agent_delegation_results (message_id, state, turn_id, result_text, reason_code, reported_at)
            VALUES ({messageId}, {request.State}, {request.TurnId}, {request.Text}, {request.Reason}, {now})
            ON CONFLICT (message_id) DO NOTHING
            """, ct);
        var result = await db.ExternalAgentDelegationResults.AsNoTracking().SingleAsync(x => x.MessageId == messageId, ct);
        return result.State == request.State && result.TurnId == request.TurnId &&
            result.Text == request.Text && result.Reason == request.Reason
            ? Ok(ToResult(result))
            : Conflict(new ExternalErrorResponse { Code = "agent.adapter_result_conflict" });
    }

    private static IQueryable<RoomMessageEntity> Delegations(PlatformDbContext db, string workspaceId, string messageId) =>
        db.RoomMessages.AsNoTracking().Where(message => message.WorkspaceId == workspaceId &&
            message.MessageId == messageId && message.FromKind == ExternalEndpointKind && message.RoomId == DelegationsRoom);

    private static AgentAdapterDelegationResult ToResult(ExternalAgentDelegationResultEntity result) =>
        new(result.State, result.TurnId, result.Text, result.Reason, DateTimeOffset.FromUnixTimeMilliseconds(result.ReportedAt));

    private async Task<IActionResult> EnqueueAsync(SendAgentAdapterMessageRequest request,
        string route, string room, string intent, CancellationToken ct)
    {
        if (request.OperationId == Guid.Empty || !AgentBinding.Valid(request.TargetAgentId)
            || string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 16_000)
            return BadRequest(new ExternalErrorResponse { Code = "agent.adapter_invalid_message" });

        await using var db = await databaseFactory.CreateDbContextAsync(ct);
        var adapter = await FindAdapterAsync(db, ct);
        if (adapter is null)
            return Forbid();
        if (!OwnsLease(adapter, request.LeaseId))
            return RejectedLease();

        var key = request.OperationId.ToString("N");
        var body = JsonSerializer.Serialize(new { request.TargetAgentId, request.Text });
        var messageId = ExternalApiIdempotencyStore.ComputeKeyHash(adapter.TokenId, "POST", route,
            key + ":" + ExternalApiIdempotencyStore.ComputeRequestHash(body));
        var claim = await idempotency.TryClaimAsync(adapter.TokenId, "POST", route, key, body,
            TimeSpan.FromDays(options.Current.IdempotencyRetentionDays), ct);
        if (claim.Outcome == ExternalIdempotencyOutcome.Conflict)
            return Conflict(new ExternalErrorResponse { Code = "agent.adapter_operation_conflict" });
        if (claim.Outcome == ExternalIdempotencyOutcome.Replay)
            return Accepted(new AgentAdapterMessageReceipt(request.OperationId, claim.ResourceId ?? messageId, "queued"));
        if (claim.Outcome == ExternalIdempotencyOutcome.InProgress)
            return await db.RoomMessages.AnyAsync(x => x.MessageId == messageId, ct)
                ? Accepted(new AgentAdapterMessageReceipt(request.OperationId, messageId, "queued"))
                : Conflict(new ExternalErrorResponse { Code = "agent.adapter_operation_pending" });

        var now = clock.GetUtcNow();
        var targets = from candidate in db.ExternalAgentAdapters
                      join token in db.ExternalAccessTokens on candidate.TokenId equals token.TokenId
                      where candidate.WorkspaceId == adapter.WorkspaceId && candidate.AgentId == request.TargetAgentId
                          && token.RevokedAtUtc == null && db.AppUsers.Any(u => u.UserId == token.OwnerUserId && u.IsEnabled)
                      select (DateTimeOffset?)token.ExpiresAtUtc;
        var expiry = await targets.SingleOrDefaultAsync(ct);
        if (expiry is null || expiry <= now || !OwnsLease(adapter, request.LeaseId))
        {
            await idempotency.ReleaseAsync(adapter.TokenId, "POST", route, key, CancellationToken.None);
            return expiry is null || expiry <= now
                ? NotFound(new ExternalErrorResponse { Code = "agent.adapter_target_unavailable" })
                : RejectedLease();
        }

        try
        {
            await messages.SendAsync(new MessageEnvelope
            {
                MessageId = messageId,
                From = new MessageAddress { Kind = ExternalEndpointKind, Id = adapter.AgentId, WorkspaceId = adapter.WorkspaceId },
                To = [new MessageAddress { Kind = ExternalEndpointKind, Id = request.TargetAgentId, WorkspaceId = adapter.WorkspaceId }],
                RoomId = room,
                Audience = MessageAudiences.Direct,
                Visibility = MessageVisibilities.Private,
                Content = request.Text,
                CorrelationId = messageId,
                Metadata = new Dictionary<string, string> { ["intent"] = intent },
            }, ct);
        }
        catch
        {
            // The route can already be durable if event publication fails afterward.
            if (!await db.RoomMessages.AnyAsync(x => x.MessageId == messageId, CancellationToken.None))
                await idempotency.ReleaseAsync(adapter.TokenId, "POST", route, key, CancellationToken.None);
            throw;
        }

        await idempotency.CompleteAsync(adapter.TokenId, "POST", route, key, StatusCodes.Status202Accepted, messageId, ct);
        return Accepted(new AgentAdapterMessageReceipt(request.OperationId, messageId, "queued"));
    }

    private Task<ExternalAgentAdapterEntity?> FindAdapterAsync(PlatformDbContext db, CancellationToken ct)
    {
        var tokenId = User.FindFirstValue(ExternalAccessTokenClaimNames.TokenId);
        return db.ExternalAgentAdapters.AsNoTracking().SingleOrDefaultAsync(
            adapter => adapter.TokenId == tokenId && db.Workspaces.Any(
                w => w.WorkspaceId == adapter.WorkspaceId && w.IsEnabled && !w.IsFrozen), ct);
    }

    private static AgentAdapterBinding Bind(ExternalAgentAdapterEntity adapter) =>
        new(adapter.WorkspaceId, adapter.AgentId, adapter.TokenId);

    private bool OwnsLease(ExternalAgentAdapterEntity adapter, Guid leaseId)
    {
        var lease = registry.Resolve(adapter.WorkspaceId, adapter.AgentId);
        return lease?.LeaseId == leaseId && lease.Binding.InstallationId == adapter.TokenId;
    }

    private static AgentAdapterConnectionResponse ToResponse(AgentAdapterLease lease) =>
        new(lease.Binding.WorkspaceId, lease.Binding.AgentId, lease.LeaseId, lease.ExpiresAtUtc);

    private ConflictObjectResult RejectedLease() =>
        Conflict(new ExternalErrorResponse { Code = AgentAdapterErrorCodes.LeaseRejected });
}

public sealed record RegisterAgentAdapterRequest(Guid? ReplacesLeaseId);
public sealed record AgentAdapterLeaseRequest(Guid LeaseId);
public sealed record AgentAdapterConnectionResponse(string WorkspaceId, string AgentId, Guid LeaseId, DateTimeOffset ExpiresAtUtc);
public sealed record AgentAdapterDirectoryItem(string AgentId, string Name, bool Online);
public sealed record AgentAdapterDirectoryResponse(IReadOnlyList<AgentAdapterDirectoryItem> Items, string? NextCursor);
public sealed record AgentAdapterInboxMessage(long Sequence, string MessageId, string FromAgentId, string Text, string Kind);
public sealed record AgentAdapterInboxResponse(IReadOnlyList<AgentAdapterInboxMessage> Items, long NextCursor);
public sealed record SendAgentAdapterMessageRequest(Guid LeaseId, Guid OperationId, string TargetAgentId, string Text);
public sealed record AgentAdapterMessageReceipt(Guid OperationId, string MessageId, string Status);
public sealed record SettleAgentAdapterDelegationRequest(Guid LeaseId, string State, string TurnId, string? Text, string? Reason);
public sealed record AgentAdapterDelegationReceipt(string MessageId, string TargetAgentId, string State, AgentAdapterDelegationResult? Result);
public sealed record AgentAdapterDelegationResult(string State, string TurnId, string? Text, string? Reason, DateTimeOffset ReportedAtUtc);

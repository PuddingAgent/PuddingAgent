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
    private const string MessageRoute = "/api/external/v1/agent-adapters/messages";

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
            select new AgentAdapterInboxMessage(delivery.Id, delivery.MessageId, message.FromId, message.Content))
            .Take(limit).ToListAsync(ct);

        if (!OwnsLease(adapter, leaseId))
            return RejectedLease();
        return Ok(new AgentAdapterInboxResponse(items, items.Count == 0 ? after : items[^1].Sequence));
    }

    /// <summary>Queues a text notification for another registered adapter in the same workspace.</summary>
    [HttpPost("messages")]
    [ProducesResponseType<AgentAdapterMessageReceipt>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Send([FromBody] SendAgentAdapterMessageRequest request, CancellationToken ct)
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
        var messageId = ExternalApiIdempotencyStore.ComputeKeyHash(adapter.TokenId, "POST", MessageRoute,
            key + ":" + ExternalApiIdempotencyStore.ComputeRequestHash(body));
        var claim = await idempotency.TryClaimAsync(adapter.TokenId, "POST", MessageRoute, key, body,
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
            await idempotency.ReleaseAsync(adapter.TokenId, "POST", MessageRoute, key, CancellationToken.None);
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
                RoomId = "external-agent-adapters",
                Audience = MessageAudiences.Direct,
                Visibility = MessageVisibilities.Private,
                Content = request.Text,
                CorrelationId = messageId,
                Metadata = new Dictionary<string, string> { ["intent"] = MessageIntents.Inform },
            }, ct);
        }
        catch
        {
            // The route can already be durable if event publication fails afterward.
            if (!await db.RoomMessages.AnyAsync(x => x.MessageId == messageId, CancellationToken.None))
                await idempotency.ReleaseAsync(adapter.TokenId, "POST", MessageRoute, key, CancellationToken.None);
            throw;
        }

        await idempotency.CompleteAsync(adapter.TokenId, "POST", MessageRoute, key, StatusCodes.Status202Accepted, messageId, ct);
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
public sealed record AgentAdapterInboxMessage(long Sequence, string MessageId, string FromAgentId, string Text);
public sealed record AgentAdapterInboxResponse(IReadOnlyList<AgentAdapterInboxMessage> Items, long NextCursor);
public sealed record SendAgentAdapterMessageRequest(Guid LeaseId, Guid OperationId, string TargetAgentId, string Text);
public sealed record AgentAdapterMessageReceipt(Guid OperationId, string MessageId, string Status);

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingAgents;
using PuddingCode.Security;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Security;

namespace PuddingPlatform.Controllers.Api;

[ApiController]
[Route("api/admin/agent-adapters")]
[Authorize(Policy = ExternalAccessTokenPolicyNames.AdminAccessTokenManagement)]
public sealed class AdminAgentAdaptersController(
    IDbContextFactory<PlatformDbContext> databaseFactory,
    ExternalAccessTokenService credentials,
    AgentAdapterRegistry registry,
    TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        if (page < 1 || pageSize is < 1 or > 100 || (long)(page - 1) * pageSize > int.MaxValue)
            return BadRequest(new { code = "agent.adapter_invalid_page" });
        await using var db = await databaseFactory.CreateDbContextAsync(ct);
        var query =
            from adapter in db.ExternalAgentAdapters.AsNoTracking()
            join credential in db.ExternalAccessTokens.AsNoTracking() on adapter.TokenId equals credential.TokenId
            select new
            {
                Adapter = adapter,
                credential.KeyId,
                credential.Name,
                credential.Version,
                credential.ExpiresAtUtc,
                credential.RevokedAtUtc,
                credential.LastUsedAtUtc,
                OwnerEnabled = db.AppUsers.Any(u => u.UserId == credential.OwnerUserId && u.IsEnabled),
                WorkspaceEnabled = db.Workspaces.Any(w => w.WorkspaceId == adapter.WorkspaceId && w.IsEnabled && !w.IsFrozen),
            };
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(x => x.Adapter.AgentId).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var now = clock.GetUtcNow();
        return Ok(new
        {
            total,
            items = rows.Select(x =>
            {
                var active = x.RevokedAtUtc is null && now < x.ExpiresAtUtc && x.OwnerEnabled && x.WorkspaceEnabled;
                var lease = active ? registry.Resolve(x.Adapter.WorkspaceId, x.Adapter.AgentId) : null;
                return new
                {
                    x.Adapter.TokenId, x.Adapter.AgentId, x.Adapter.WorkspaceId, ak = x.KeyId, x.Name, x.Version,
                    x.ExpiresAtUtc, x.RevokedAtUtc, x.LastUsedAtUtc, x.Adapter.RegisteredAtUtc, active,
                    online = lease?.Binding.InstallationId == x.Adapter.TokenId,
                    leaseExpiresAtUtc = lease?.ExpiresAtUtc,
                };
            }),
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAgentAdapterRequest request, CancellationToken ct)
    {
        var owner = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(owner))
            return Forbid();
        if (string.IsNullOrWhiteSpace(request.WorkspaceId))
            return BadRequest(new { code = "agent.adapter_workspace_required" });

        var result = await credentials.CreateAsync(new ExternalAccessTokenCreateCommand
        {
            OwnerUserId = owner,
            Name = request.Name ?? "",
            WorkspaceIds = [request.WorkspaceId],
            Scopes = [ExternalTaskApiScopes.AgentAdaptersConnect],
            LifetimeDays = request.LifetimeDays,
            IssueForAgentAdapter = true,
        }, ct);
        if (!result.IsOk)
            return StatusCode(result.Error == ExternalAccessTokenCreateError.TooManyActiveTokens ? 409 : 422,
                new { code = "agent.adapter_credential_rejected", reason = result.Error.ToString() });

        var created = result.Value!;
        Response.Headers.CacheControl = "no-store";
        return StatusCode(StatusCodes.Status201Created, new
        {
            created.Item.TokenId, agentId = created.AdapterAgentId, workspaceId = created.Item.Workspaces.Single(),
            ak = created.Item.KeyId, sk = created.AccessToken[(created.AccessToken.IndexOf('.') + 1)..],
            created.Item.ExpiresAtUtc,
        });
    }
}

public sealed record CreateAgentAdapterRequest(string? Name, string? WorkspaceId, int? LifetimeDays);

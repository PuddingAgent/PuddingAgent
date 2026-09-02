using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Security;
using PuddingPlatform.Services.Security;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// ADR-075: Admin Access Token 管理 API。
/// Policy 显式锁定 JWT scheme + admin role —— External Access Token 永远不能管理 Token。
/// 明文 Secret 只出现在 POST 201 响应中一次；不提供 reveal、unrevoke、硬删除或扩大 scope/workspace 端点。
/// </summary>
[ApiController]
[Route("api/admin/access-tokens")]
[Authorize(Policy = ExternalAccessTokenPolicyNames.AdminAccessTokenManagement)]
public class AdminAccessTokenController(
    ExternalAccessTokenService service,
    ExternalTaskApiOptionsProvider optionsProvider) : ControllerBase
{
    /// <summary>GET /api/admin/access-tokens/status — External API 运行策略展示（不含 Secret）。</summary>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var options = optionsProvider.Current;
        return Ok(new
        {
            enabled = options.Enabled,
            publicBaseUrl = options.PublicBaseUrl,
            requireHttps = options.RequireHttps,
            defaultTokenLifetimeDays = options.DefaultTokenLifetimeDays,
            maxTokenLifetimeDays = options.MaxTokenLifetimeDays,
            maxActiveTokensPerOwner = options.MaxActiveTokensPerOwner,
            boundBaseUrl = $"{Request.Scheme}://{Request.Host}",
        });
    }
    /// <summary>GET /api/admin/access-tokens — 分页/筛选元数据（无 Secret/Hash）。</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        [FromQuery] string? ownerUserId,
        [FromQuery] string? workspaceId,
        [FromQuery] string? scope,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        ExternalAccessTokenStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<ExternalAccessTokenStatus>(status, ignoreCase: true, out var value))
                return BadRequest(new { code = "external.invalid_status", message = "未知状态过滤值。" });
            parsedStatus = value;
        }

        var (items, total) = await service.ListAsync(new ExternalAccessTokenListFilter
        {
            Status = parsedStatus,
            OwnerUserId = string.IsNullOrWhiteSpace(ownerUserId) ? null : ownerUserId,
            WorkspaceId = string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId,
            Scope = string.IsNullOrWhiteSpace(scope) ? null : scope,
            Page = page,
            PageSize = pageSize,
        }, ct);

        // Do not return the persistence projection directly: its enum would be
        // serialized as a number by the platform MVC settings, which breaks the
        // Admin UI's status/revoke state machine. Keep list/detail on one stable
        // string wire contract.
        return Ok(new
        {
            items = items.Select(ToResponse).ToArray(),
            total,
            page,
            pageSize,
        });
    }

    /// <summary>POST /api/admin/access-tokens — 创建；明文 accessToken 只在本响应出现一次。</summary>
    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateAccessTokenRequest request,
        CancellationToken ct)
    {
        var result = await service.CreateAsync(new ExternalAccessTokenCreateCommand
        {
            Name = request.Name ?? string.Empty,
            WorkspaceIds = request.WorkspaceIds ?? [],
            Scopes = request.Scopes ?? [],
            LifetimeDays = request.LifetimeDays,
            OwnerUserId = CurrentUserId,
        }, ct);

        if (!result.IsOk)
        {
            var (status, code) = result.Error switch
            {
                ExternalAccessTokenCreateError.TooManyActiveTokens => (409, "external.too_many_active_tokens"),
                _ => (422, "external.invalid_token_request"),
            };
            return StatusCode(status, new
            {
                code,
                error = result.Error,
            });
        }

        var created = result.Value!;
        return StatusCode(StatusCodes.Status201Created, new CreatedAccessTokenResponse
        {
            TokenId = created.Item.TokenId,
            KeyId = created.Item.KeyId,
            DisplayPrefix = created.Item.DisplayPrefix,
            Name = created.Item.Name,
            OwnerUserId = created.Item.OwnerUserId,
            Version = created.Item.Version,
            CreatedAtUtc = created.Item.CreatedAtUtc,
            ExpiresAtUtc = created.Item.ExpiresAtUtc,
            Scopes = created.Item.Scopes,
            Workspaces = created.Item.Workspaces,
            Status = created.Item.Status.ToString(),
            /// 明文只出现一次，此后任何响应都不再包含。
            AccessToken = created.AccessToken,
        });
    }

    /// <summary>GET /api/admin/access-tokens/{tokenId} — 详情，无 Secret/Hash。</summary>
    [HttpGet("{tokenId}")]
    public async Task<IActionResult> GetDetail(string tokenId, CancellationToken ct)
    {
        var record = await service.GetDetailAsync(tokenId, ct);
        if (record is null)
            return NotFound(new { code = "external.token_not_found" });

        return Ok(new AccessTokenDetailResponse
        {
            TokenId = record.TokenId,
            KeyId = record.KeyId,
            DisplayPrefix = record.DisplayPrefix,
            Name = record.Name,
            OwnerUserId = record.OwnerUserId,
            Version = record.Version,
            CreatedAtUtc = record.CreatedAtUtc,
            ExpiresAtUtc = record.ExpiresAtUtc,
            RevokedAtUtc = record.RevokedAtUtc,
            RevokedByUserId = record.RevokedByUserId,
            RevocationReason = record.RevocationReason,
            LastUsedAtUtc = record.LastUsedAtUtc,
            Scopes = record.Scopes,
            Workspaces = record.Workspaces,
            Status = record.Status.ToString(),
        });
    }

    /// <summary>PATCH /api/admin/access-tokens/{tokenId} — 只允许重命名；要求 expectedVersion CAS。</summary>
    [HttpPatch("{tokenId}")]
    public async Task<IActionResult> Rename(
        string tokenId,
        [FromBody] RenameAccessTokenRequest request,
        CancellationToken ct)
    {
        var result = await service.RenameAsync(tokenId, request.ExpectedVersion, request.Name ?? string.Empty, CurrentUserId, ct);
        return result.IsOk
            ? Ok(ToResponse(result.Value!))
            : MapError(result.Error);
    }

    /// <summary>POST /api/admin/access-tokens/{tokenId}/revoke — 撤销不可逆且即时生效；要求 expectedVersion。</summary>
    [HttpPost("{tokenId}/revoke")]
    public async Task<IActionResult> Revoke(
        string tokenId,
        [FromBody] RevokeAccessTokenRequest request,
        CancellationToken ct)
    {
        var result = await service.RevokeAsync(tokenId, request.ExpectedVersion, CurrentUserId, request.Reason, ct);
        return result.IsOk
            ? Ok(ToResponse(result.Value!))
            : MapError(result.Error);
    }

    private string CurrentUserId
        => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown-admin";

    private static AccessTokenDetailResponse ToResponse(ExternalAccessTokenListItem item)
        => new()
        {
            TokenId = item.TokenId,
            KeyId = item.KeyId,
            DisplayPrefix = item.DisplayPrefix,
            Name = item.Name,
            OwnerUserId = item.OwnerUserId,
            Version = item.Version,
            CreatedAtUtc = item.CreatedAtUtc,
            ExpiresAtUtc = item.ExpiresAtUtc,
            RevokedAtUtc = item.RevokedAtUtc,
            RevokedByUserId = item.RevokedByUserId,
            RevocationReason = item.RevocationReason,
            LastUsedAtUtc = item.LastUsedAtUtc,
            Scopes = item.Scopes,
            Workspaces = item.Workspaces,
            Status = item.Status.ToString(),
        };

    private IActionResult MapError(ExternalAccessTokenManagementError error)
        => error switch
        {
            ExternalAccessTokenManagementError.NotFound => NotFound(new { code = "external.token_not_found" }),
            ExternalAccessTokenManagementError.VersionConflict => Conflict(new
            {
                code = "external.token_version_conflict",
                message = "expectedVersion 与当前 Token version 不一致，请刷新后重试。",
            }),
            ExternalAccessTokenManagementError.InvalidName => UnprocessableEntity(new { code = "external.invalid_name" }),
            ExternalAccessTokenManagementError.InvalidReason => UnprocessableEntity(new { code = "external.invalid_reason" }),
            _ => BadRequest(),
        };

    public sealed class CreateAccessTokenRequest
    {
        public string? Name { get; set; }
        public IReadOnlyList<string>? WorkspaceIds { get; set; }
        public IReadOnlyList<string>? Scopes { get; set; }
        public int? LifetimeDays { get; set; }
    }

    public sealed class CreatedAccessTokenResponse : AccessTokenDetailResponse
    {
        /// <summary>canonical token 明文，仅此响应出现一次；关闭页面后不可恢复。</summary>
        public string AccessToken { get; set; } = string.Empty;
    }

    public class AccessTokenDetailResponse
    {
        public string TokenId { get; set; } = string.Empty;
        public string KeyId { get; set; } = string.Empty;
        public string DisplayPrefix { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string OwnerUserId { get; set; } = string.Empty;
        public int Version { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset ExpiresAtUtc { get; set; }
        public DateTimeOffset? RevokedAtUtc { get; set; }
        public string? RevokedByUserId { get; set; }
        public string? RevocationReason { get; set; }
        public DateTimeOffset? LastUsedAtUtc { get; set; }
        public IReadOnlyList<string> Scopes { get; set; } = [];
        public IReadOnlyList<string> Workspaces { get; set; } = [];
        /// <summary>稳定 wire 名称（Active/Revoked/Expired），不依赖 JSON enum converter。</summary>
        public string Status { get; set; } = string.Empty;
    }

    public sealed class RenameAccessTokenRequest
    {
        public string? Name { get; set; }
        public int ExpectedVersion { get; set; }
    }

    public sealed class RevokeAccessTokenRequest
    {
        public int ExpectedVersion { get; set; }
        public string? Reason { get; set; }
    }
}

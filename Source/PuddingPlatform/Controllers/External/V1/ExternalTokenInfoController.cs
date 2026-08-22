using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Security;
using PuddingPlatform.Services.Security;

namespace PuddingPlatform.Controllers.External.V1;

/// <summary>
/// ADR-075: 外部 Token 自检端点（whoami/doctor）。
/// 只接受 PuddingExternalAccessToken scheme；返回当前 Token 名称、scope、workspace 与到期时间，
/// 不返回 Secret。受 ExternalApiGateFilter（Enabled 门控 + 非 Loopback HTTPS 强制）保护。
/// </summary>
[ApiController]
[Route("api/external/v1")]
[ServiceFilter(typeof(ExternalApiGateFilter))]
public class ExternalTokenInfoController(ExternalAccessTokenService service) : ControllerBase
{
    /// <summary>GET /api/external/v1/token — Token 自检。</summary>
    [HttpGet("token")]
    [Authorize(Policy = ExternalAccessTokenPolicyNames.ExternalApiAuthenticated)]
    public async Task<IActionResult> WhoAmI(CancellationToken ct)
    {
        var tokenId = User.FindFirstValue(ExternalAccessTokenClaimNames.TokenId);
        if (string.IsNullOrEmpty(tokenId))
            return Unauthorized();

        var record = await service.GetDetailAsync(tokenId, ct);
        if (record is null)
            return Unauthorized();

        return Ok(new
        {
            tokenId = record.TokenId,
            name = record.Name,
            displayPrefix = record.DisplayPrefix,
            scopes = record.Scopes,
            workspaces = record.Workspaces,
            expiresAtUtc = record.ExpiresAtUtc,
            status = record.Status.ToString(),
        });
    }
}

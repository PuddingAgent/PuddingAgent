using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingCode.Security;
using AuthenticationProperties = Microsoft.AspNetCore.Authentication.AuthenticationProperties;

namespace PuddingPlatform.Services.Security;

/// <summary>认证 scheme 常量（Platform 侧；Core 侧公共常量见 ExternalAccessTokenDefaults）。</summary>
public static class ExternalAccessTokenAuthentication
{
    public const string Scheme = ExternalAccessTokenDefaults.Scheme;
    public const string BearerPrefix = "Bearer ";
}

public sealed class ExternalAccessTokenOptions : AuthenticationSchemeOptions;

/// <summary>
/// ADR-075: PuddingExternalAccessToken 认证 Handler。
/// 处理序列：Header 缺失 → NoResult；非单一 Bearer/超长/格式非法 → Fail("invalid_token")；
/// keyId 索引查询 → SHA-256 固定时间摘要比较 → revoked/expired/owner fail closed → ClaimsPrincipal。
/// 正确性路径只读数据库；成功使用投递 last-used 合并器，不产生同步写。
/// 绝不注入 admin role；401 统一 invalid_token，不区分未知/过期/撤销/owner 禁用。
/// </summary>
public sealed class ExternalAccessTokenHandler(
    IOptionsMonitor<ExternalAccessTokenOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<ExternalAccessTokenOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var headerValues = Request.Headers.Authorization;
        if (headerValues.Count == 0)
            return AuthenticateResult.NoResult();

        // 只接受单个 Authorization 值（多值 Header 视为攻击面，直接失败）。
        if (headerValues.Count > 1)
            return InvalidToken();

        var headerValue = headerValues.ToString();
        if (!headerValue.StartsWith(ExternalAccessTokenAuthentication.BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return InvalidToken();

        var presentedToken = headerValue[ExternalAccessTokenAuthentication.BearerPrefix.Length..].Trim();
        if (presentedToken.Length > ExternalAccessTokenDefaults.MaxCanonicalLength)
            return InvalidToken();

        var service = Context.RequestServices.GetRequiredService<ExternalAccessTokenService>();
        var outcome = await service.ValidateAsync(presentedToken);
        if (outcome.Principal is null)
            return InvalidToken();

        var principal = outcome.Principal;
        var identity = new ClaimsIdentity(
            authenticationType: Scheme.Name,
            nameType: ClaimTypes.Name,
            roleType: ClaimTypes.Role);

        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, $"{ExternalAccessTokenDefaults.ActorIdPrefix}{principal.TokenId}"));
        identity.AddClaim(new Claim(ClaimTypes.Name, principal.Name));
        identity.AddClaim(new Claim(ExternalAccessTokenClaimNames.ActorType, ExternalAccessTokenDefaults.ActorType));
        identity.AddClaim(new Claim(ExternalAccessTokenClaimNames.TokenId, principal.TokenId));
        identity.AddClaim(new Claim(ExternalAccessTokenClaimNames.OwnerUserId, principal.OwnerUserId));
        foreach (var scope in principal.Scopes)
            identity.AddClaim(new Claim(ExternalAccessTokenClaimNames.Scope, scope));
        foreach (var workspace in principal.Workspaces)
            identity.AddClaim(new Claim(ExternalAccessTokenClaimNames.Workspace, workspace));

        // last-used 合并写：认证热路径只投递，不落库（服务可选，缺注册不阻断认证）。
        Context.RequestServices
            .GetService<ExternalAccessTokenUsageCoalescer>()
            ?.RecordSuccess(principal.TokenId, DateTimeOffset.UtcNow);

        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = $"{Scheme.Name} error=\"invalid_token\"";
        return base.HandleChallengeAsync(properties);
    }

    private static AuthenticateResult InvalidToken()
        => AuthenticateResult.Fail("invalid_token");
}

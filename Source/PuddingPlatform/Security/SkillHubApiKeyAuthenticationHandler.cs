using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PuddingPlatform.Security;

/// <summary>
/// SKILL Hub 机器凭据认证常量：让进程内 skill_hub Agent 工具在无人类 JWT 的情况下，
/// 通过 <see cref="SkillHubApiKeyAuthentication.HeaderName"/> 请求头（机器凭据，
/// 类似 npm publish 的 publish token）发布技能到 SKILL Hub。
/// </summary>
public static class SkillHubApiKeyAuthentication
{
    /// <summary>认证 scheme 名。仅由 <see cref="SkillHubApiPolicyNames.SkillHubClient"/> 策略显式选择，绝不作为全局默认 scheme。</summary>
    public const string Scheme = "SkillHubApiKey";

    /// <summary>机器凭据请求头（沿用 skill_hub 工具侧既有约定）。</summary>
    public const string HeaderName = "X-Admin-Api-Key";

    /// <summary>presented key 长度上限：超长输入直接判失败，防滥用。</summary>
    public const int MaxApiKeyLength = 512;

    /// <summary>服务端密钥配置键（与工具侧读取键一致）。</summary>
    public const string ApiKeyConfigKey = "SkillHub:ApiKey";

    /// <summary>服务端密钥回退配置键（与工具侧读取键一致；两者均为空时 fail-closed）。</summary>
    public const string AdminApiKeyConfigKey = "AdminApiKey";
}

/// <summary>SKILL Hub API-Key scheme 的 Options（当前无额外参数；密钥值不落日志/输出）。</summary>
public sealed class SkillHubApiKeyOptions : AuthenticationSchemeOptions;

/// <summary>SKILL Hub 端点 Policy 名（Host 组合根注册，Controller 类级引用）。</summary>
public static class SkillHubApiPolicyNames
{
    /// <summary>api/skill-hub 专用：JwtBearer（管理界面行为零变化）或 SkillHubApiKey（机器凭据）任一认证成功即可。</summary>
    public const string SkillHubClient = "SkillHubClient";
}

/// <summary>
/// SKILL Hub 机器凭据认证 Handler（AuthenticationHandler&lt;TOptions&gt; 范式，与
/// ExternalAccessTokenHandler 同目录模式一致）。
/// 安全要点：
/// - fail-closed：服务端未配置任何密钥（SkillHub:ApiKey / AdminApiKey 均为空）时，
///   凡携带凭据头的请求一律认证失败；"未配置密钥"绝不等价于"免认证"。
/// - 密钥比较使用 CryptographicOperations.FixedTimeEquals（UTF-8 字节、等长后常量时间比较）。
/// - 密钥值绝不写入日志、异常消息或任何响应；日志只记录"已配置/未配置""匹配失败"等事实。
/// - 认证成功仅建立"已认证"机器身份（无任何角色 claim），作用范围由 SkillHubClient 策略
///   限定在 api/skill-hub 端点；默认授权策略与其它端点零影响。
/// </summary>
public sealed class SkillHubApiKeyAuthenticationHandler(
    IOptionsMonitor<SkillHubApiKeyOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration)
    : AuthenticationHandler<SkillHubApiKeyOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var headerValues = Request.Headers[SkillHubApiKeyAuthentication.HeaderName];

        // 多值 Header 视为攻击面，直接失败（与 ExternalAccessTokenHandler 同一处理哲学）。
        if (headerValues.Count > 1)
            return Task.FromResult(AuthenticateResult.Fail("invalid_token: multiple_header_values"));

        var presentedKey = headerValues.Count == 1 ? headerValues.ToString().Trim() : null;

        // 未携带机器凭据头：NoResult —— 交给同策略下的 JwtBearer（管理界面行为零变化）。
        if (string.IsNullOrEmpty(presentedKey))
            return Task.FromResult(AuthenticateResult.NoResult());

        if (presentedKey.Length > SkillHubApiKeyAuthentication.MaxApiKeyLength)
            return Task.FromResult(AuthenticateResult.Fail("invalid_token"));

        // fail-closed：服务端未配置任何候选密钥 → 一律认证失败。
        var configuredKeys = CollectConfiguredKeys();
        if (configuredKeys.Count == 0)
        {
            Logger.LogWarning(
                "SkillHubApiKey auth rejected: no server API key configured (SkillHub:ApiKey / AdminApiKey are both empty).");
            return Task.FromResult(AuthenticateResult.Fail("invalid_token: server_key_not_configured"));
        }

        var presentedBytes = Encoding.UTF8.GetBytes(presentedKey);
        foreach (var configuredKey in configuredKeys)
        {
            var configuredBytes = Encoding.UTF8.GetBytes(configuredKey);
            if (configuredBytes.Length == presentedBytes.Length
                && CryptographicOperations.FixedTimeEquals(configuredBytes, presentedBytes))
            {
                // 只建立"已认证"身份：明确不授予 admin（或任何其它）角色。
                var identity = new ClaimsIdentity(
                    authenticationType: Scheme.Name,
                    nameType: ClaimTypes.Name,
                    roleType: ClaimTypes.Role);
                identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "skill-hub-machine"));
                identity.AddClaim(new Claim(ClaimTypes.Name, "skill-hub-machine"));

                var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
                return Task.FromResult(AuthenticateResult.Success(ticket));
            }
        }

        Logger.LogWarning("SkillHubApiKey auth failed: presented key does not match the configured key.");
        return Task.FromResult(AuthenticateResult.Fail("invalid_token"));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = $"{Scheme.Name} error=\"invalid_token\"";
        return base.HandleChallengeAsync(properties);
    }

    /// <summary>收集服务端候选密钥（SkillHub:ApiKey 优先，回退 AdminApiKey）；空/空白值忽略。</summary>
    private List<string> CollectConfiguredKeys()
    {
        List<string> keys = [];
        foreach (var key in (string[])
        [
            SkillHubApiKeyAuthentication.ApiKeyConfigKey,
            SkillHubApiKeyAuthentication.AdminApiKeyConfigKey,
        ])
        {
            var value = configuration[key];
            if (!string.IsNullOrWhiteSpace(value))
                keys.Add(value.Trim());
        }

        return keys;
    }
}

using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace PuddingPlatform.Services;

/// <summary>
/// 登录态 JWT 的生效配置。唯一来源是配置链里的 <c>Jwt:*</c> 键
/// （由 <c>&lt;DataRoot&gt;/config/security.json</c> 的 <c>jwt</c> 段提供，环境变量/命令行可覆盖）。
/// <para>
/// <b>不变量</b>：签名密钥必须来自配置——代码内不得再出现任何硬编码密钥兜底；
/// 密钥缺失或过短一律抛 <see cref="InvalidOperationException"/>（fail closed），
/// 而不是退回到某个"公开的默认值"。
/// </para>
/// </summary>
public sealed record PuddingJwtSettings(string Key, string Issuer, string Audience, int ExpiryHours)
{
    public const string KeyPath = "Jwt:Key";
    public const string IssuerPath = "Jwt:Issuer";
    public const string AudiencePath = "Jwt:Audience";
    public const string ExpiryHoursPath = "Jwt:ExpiryHours";

    /// <summary>缺省有效期：7 天（168 小时）。</summary>
    public const int DefaultExpiryHours = 168;

    public const string DefaultIssuer = "pudding-platform";
    public const string DefaultAudience = "pudding-admin";

    /// <summary>HS256 要求密钥 ≥128 bit。</summary>
    public const int MinimumKeyLength = 16;

    /// <summary>登录态 JWT 签名密钥的配置文件位置提示（错误信息与文档共用同一措辞）。</summary>
    public const string KeyFileHint = "<DataRoot>/config/security.json 的 jwt.key";

    /// <summary>
    /// 从配置链解析生效设置。密钥缺失/过短 → 抛异常；Issuer/Audience 用缺省值；
    /// ExpiryHours 缺省 7 天，显式配置但非法（非正整数）→ 抛异常。
    /// </summary>
    public static PuddingJwtSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var key = configuration[KeyPath];
        if (string.IsNullOrWhiteSpace(key) || key.Length < MinimumKeyLength)
        {
            var detail = string.IsNullOrWhiteSpace(key)
                ? "当前未配置"
                : $"当前值长度仅 {key.Length} 字符";
            throw new InvalidOperationException(
                $"登录态 JWT 签名密钥缺失或过短（配置键 {KeyPath}，{detail}）。"
                + $"请在 {KeyFileHint} 写入长度 ≥ {MinimumKeyLength} 字符的随机密钥（推荐 32 字符以上）后重启 Core；"
                + "代码内不再提供硬编码密钥兜底。");
        }

        var issuer = configuration[IssuerPath];
        var audience = configuration[AudiencePath];

        var expiryHours = DefaultExpiryHours;
        var expiryRaw = configuration[ExpiryHoursPath];
        if (!string.IsNullOrWhiteSpace(expiryRaw))
        {
            if (!int.TryParse(expiryRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                || parsed <= 0)
            {
                throw new InvalidOperationException(
                    $"配置键 {ExpiryHoursPath} 必须是正整数小时（例如 168 表示 7 天），当前值：{expiryRaw}。");
            }

            expiryHours = parsed;
        }

        return new PuddingJwtSettings(
            key,
            string.IsNullOrWhiteSpace(issuer) ? DefaultIssuer : issuer,
            string.IsNullOrWhiteSpace(audience) ? DefaultAudience : audience,
            expiryHours);
    }
}

/// <summary>
/// 登录态 JWT 的唯一签发入口（登录、Bootstrap 首次初始化、以及未来的续期共用同一份实现），
/// 避免"两处各写一遍签发逻辑"导致有效期/密钥口径漂移。
/// 认证校验侧（<c>PuddingApplicationHost</c> 的 JwtBearer）复用 <see cref="PuddingJwtSettings"/> 的同一份解析。
/// </summary>
public static class JwtTokenFactory
{
    /// <summary>签发登录态 JWT（HS256 + 附带 ECDSA payload 签名 claim <c>sm2_sig</c>）。</summary>
    public static string CreateToken(
        IConfiguration configuration,
        Sm2JwtSigner signer,
        string userId,
        string displayName,
        string email,
        string authority)
        => CreateToken(
            PuddingJwtSettings.FromConfiguration(configuration),
            signer,
            userId,
            displayName,
            email,
            authority);

    /// <summary>签发登录态 JWT（使用已解析的生效设置）。</summary>
    public static string CreateToken(
        PuddingJwtSettings settings,
        Sm2JwtSigner signer,
        string userId,
        string displayName,
        string email,
        string authority)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(signer);

        var utcNow = DateTime.UtcNow;
        var expiresAt = utcNow.AddHours(settings.ExpiryHours);
        var jti = Guid.NewGuid().ToString();

        var secKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.Key));
        var creds = new SigningCredentials(secKey, SecurityAlgorithms.HmacSha256);

        var sm2Payload = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["aud"] = settings.Audience,
            ["email"] = email,
            ["exp"] = new DateTimeOffset(expiresAt).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ["iss"] = settings.Issuer,
            ["jti"] = jti,
            ["name"] = displayName,
            ["nameid"] = userId,
            ["role"] = authority,
            ["sub"] = userId,
        };
        var sm2Signature = signer.SignPayload(JsonSerializer.Serialize(sm2Payload));

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId),
            new Claim(JwtRegisteredClaimNames.Jti, jti),
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Name, displayName),
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.Role, authority),
            new Claim("sm2_sig", sm2Signature),
        };

        var token = new JwtSecurityToken(
            issuer: settings.Issuer,
            audience: settings.Audience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

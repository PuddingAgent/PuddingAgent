using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace PuddingWebApiTests;

/// <summary>
/// 测试用 JWT 令牌生成工具。
/// 使用与开发环境一致的密钥和配置。
/// </summary>
public static class JwtHelper
{
    private const string TestKey = "Pudding-Platform-JWT-DevKey-MUST-CHANGE-IN-PRODUCTION-32PLUS!";
    private const string Issuer = "pudding-platform";
    private const string Audience = "pudding-admin";

    /// <summary>
    /// 生成用于测试的 Bearer Token。
    /// </summary>
    public static string GenerateToken(string username = "admin", string userId = "admin", string role = "admin")
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.Role, role),
        };

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// 为 HttpClient 设置默认 Bearer 认证头。
    /// </summary>
    public static void SetBearerToken(HttpClient client, string username = "admin")
    {
        var token = GenerateToken(username);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }
}

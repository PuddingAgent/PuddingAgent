using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Utils;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// Admin SPA 认证 API。登录时对照数据库验证凭证，成功后签发 JWT 令牌。
/// 前端将 Token 存入 localStorage，后续请求带 Authorization: Bearer &lt;token&gt;。
/// </summary>
[ApiController]
[Route("api")]
public class AuthApiController(IConfiguration config, PlatformDbContext db) : ControllerBase
{
    /// <summary>POST /api/login/account</summary>
    [HttpPost("login/account")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var user = await db.AppUsers
            .FirstOrDefaultAsync(u => u.UserId == request.Username || u.Email == request.Username, ct);

        if (user is null || !user.IsEnabled || !PasswordHasher.Verify(request.Password, user.PasswordHash))
            return Ok(new { status = "error", type = "account", currentAuthority = "guest" });

        var authority = user.UserType == UserType.Admin ? "admin" : "user";
        var token = GenerateJwt(user.UserId, user.DisplayName ?? user.Username, user.Email, authority);

        // 同时写入 Session，兼容 SSR 页面
        HttpContext.Session.SetString("username", user.UserId);
        HttpContext.Session.SetString("authority", authority);

        return Ok(new { status = "ok", type = "account", currentAuthority = authority, token });
    }

    /// <summary>GET /api/currentUser — 优先读 JWT Claim，兼容 Session</summary>
    [AllowAnonymous]
    [HttpGet("currentUser")]
    public IActionResult CurrentUser()
    {
        var userId    = User.FindFirstValue(ClaimTypes.NameIdentifier)
                       ?? HttpContext.Session.GetString("username");
        var name      = User.FindFirstValue(ClaimTypes.Name);
        var email     = User.FindFirstValue(ClaimTypes.Email);
        var authority = User.FindFirstValue(ClaimTypes.Role)
                       ?? HttpContext.Session.GetString("authority");

        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized(new
            {
                data        = new { isLogin = false },
                errorCode   = "401",
                errorMessage = "请先登录！",
                success     = true,
            });
        }

        return Ok(new
        {
            success = true,
            data    = new
            {
                name      = name ?? userId,
                avatar    = "https://gw.alipayobjects.com/zos/antfincdn/XAosXuNZyF/BiazfanxmamNRoxxVxka.png",
                userid    = userId,
                access    = authority ?? "user",
                email     = email ?? $"{userId}@pudding.local",
                signature = "Pudding Platform 管理控制台",
                title     = authority == "admin" ? "系统管理员" : "普通用户",
                group     = "Pudding Team",
                unreadCount = 0,
            },
        });
    }

    /// <summary>POST /api/login/outLogin</summary>
    [HttpPost("login/outLogin")]
    public IActionResult Logout()
    {
        HttpContext.Session.Clear();
        return Ok(new { data = "ok" });
    }

    private string GenerateJwt(string userId, string displayName, string email, string authority)
    {
        var key         = config["Jwt:Key"] ?? "Pudding-Platform-JWT-DevKey-MUST-CHANGE-IN-PRODUCTION-32PLUS!";
        var issuer      = config["Jwt:Issuer"] ?? "pudding-platform";
        var audience    = config["Jwt:Audience"] ?? "pudding-admin";
        var expiryHours = int.TryParse(config["Jwt:ExpiryHours"], out var h) ? h : 8;

        var secKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
        var creds  = new SigningCredentials(secKey, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Name, displayName),
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.Role, authority),
        };

        var token = new JwtSecurityToken(
            issuer:             issuer,
            audience:           audience,
            claims:             claims,
            expires:            DateTime.UtcNow.AddHours(expiryHours),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public sealed record LoginRequest(string Username, string Password, string? Type = "account");

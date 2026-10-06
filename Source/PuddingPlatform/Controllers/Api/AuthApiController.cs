using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Utils;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// Admin SPA 认证 API。登录时对照数据库验证凭证，成功后签发 JWT 令牌。
/// 前端将 Token 存入 localStorage，后续请求带 Authorization: Bearer &lt;token&gt;。
/// </summary>
[ApiController]
[Route("api")]
public class AuthApiController(IConfiguration config, IAppUserRepository appUserRepo, Sm2JwtSigner sm2JwtSigner) : ControllerBase
{
    /// <summary>POST /api/login/account</summary>
    [HttpPost("login/account")]
    public async Task<IActionResult> Login(
        [FromBody] LoginRequest request,
        [FromServices] ILogger<AuthApiController> logger,
        CancellationToken ct)
    {
        var user = await appUserRepo.FindByUserIdOrEmailAsync(request.Username ?? "", ct);

        if (user is null || !user.IsEnabled || !PasswordHasher.Verify(request.Password, user.PasswordHash))
        {
            // Do not reveal whether the account exists/is enabled and never log credential shape.
            logger.LogInformation(
                "[Auth:Login] Authentication failed type={Type}",
                request.Type ?? "account");
            return Ok(new { status = "error", type = "account", currentAuthority = "guest" });
        }

        var authority = user.UserType == "admin" ? "admin" : "user";
        // 签发统一走 JwtTokenFactory：密钥与有效期只来自配置（security.json 的 jwt 段）。
        var token = JwtTokenFactory.CreateToken(
            config,
            sm2JwtSigner,
            user.UserId,
            user.DisplayName ?? user.Username,
            user.Email,
            authority);

        logger.LogInformation(
            "[Auth:Login] Authentication succeeded authority={Authority}",
            authority);

        // 同时写入 Session，兼容 SSR 页面
        HttpContext.Session.SetString("username", user.UserId);
        HttpContext.Session.SetString("authority", authority);

        return Ok(new { status = "ok", type = "account", currentAuthority = authority, token });
    }

    /// <summary>GET /api/currentUser — 优先读 JWT Claim，兼容 Session；头像读取数据库中的最新值</summary>
    [AllowAnonymous]
    [HttpGet("currentUser")]
    public async Task<IActionResult> CurrentUser(CancellationToken ct)
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

        var user = await appUserRepo.FindByIdAsync(userId, ct);
        var avatar = user?.Avatar;

        return Ok(new
        {
            success = true,
            data    = new
            {
                name      = name ?? userId,
                avatar    = string.IsNullOrEmpty(avatar) ? "/admin/assets/images/me.png" : avatar,
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

}

public sealed record LoginRequest(string Username, string Password, string? Type = "account");

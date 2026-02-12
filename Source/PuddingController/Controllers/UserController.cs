using Microsoft.AspNetCore.Mvc;

namespace PuddingController.Controllers;

/// <summary>
/// 开发用 mock 认证 API，供 PuddingPlatformAdmin 前端使用。
/// 生产环境应替换为真实 IdP 认证方案。
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class UserController : ControllerBase
{
    public sealed record LoginRequest
    {
        public string Username { get; init; } = string.Empty;
        public string Password { get; init; } = string.Empty;
    }

    /// <summary>登录（开发 mock：任意用户名/密码均成功）。</summary>
    [HttpPost("login")]
    public ActionResult Login([FromBody] LoginRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Username))
            return BadRequest(new { code = 400, message = "用户名不能为空" });

        return Ok(new { token = $"mock-token-{req.Username}-{DateTime.UtcNow.Ticks}" });
    }

    /// <summary>获取当前用户信息（开发 mock）。</summary>
    [HttpGet("info")]
    public ActionResult Info()
    {
        return Ok(new
        {
            name = "Admin",
            avatar = "",
            roles = new[] { "admin" }
        });
    }

    /// <summary>登出。</summary>
    [HttpPost("logout")]
    public ActionResult Logout() => Ok();
}

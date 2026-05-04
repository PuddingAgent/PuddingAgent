using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Data;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Utils;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// 首次启动 Bootstrap 初始化 API。
/// 当数据库中尚无 Admin 用户时，引导创建首个管理员账号。
/// </summary>
[ApiController]
[Route("api/bootstrap")]
[AllowAnonymous]
public partial class BootstrapApiController(IConfiguration config, PlatformDbContext db, BootstrapStateService stateService, Sm2JwtSigner sm2JwtSigner) : ControllerBase
{
    private IActionResult? CheckInitialized()
    {
        var initialized = config.GetValue<bool>("Bootstrap:Initialized");
        if (initialized)
            return StatusCode(403, new { status = "error", message = "系统已完成初始化" });
        return null;
    }

    /// <summary>GET /api/bootstrap/status — 检查系统是否需要初始化</summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        var lockResult = CheckInitialized();
        if (lockResult != null) return lockResult;

        var hasAdmin = await db.AppUsers.AnyAsync(u => u.UserType == UserType.Admin, ct);
        var userCount = await db.AppUsers.CountAsync(ct);

        return Ok(new
        {
            needsSetup = !hasAdmin,
            hasAdmin,
            userCount,
        });
    }

    /// <summary>POST /api/bootstrap/admin — 创建首个管理员账号</summary>
    [HttpPost("admin")]
    public async Task<IActionResult> CreateAdmin([FromBody] BootstrapAdminRequest request, CancellationToken ct)
    {
        var lockResult = CheckInitialized();
        if (lockResult != null) return lockResult;

        // 密码强度校验：≥8位，含大小写+数字（事务外提前校验）
        if (!PasswordStrengthRegex().IsMatch(request.Password))
            return BadRequest(new
            {
                status = "error",
                message = "密码必须至少8位，且包含大写字母、小写字母和数字",
            });

        // 使用 Serializable 事务防止 TOCTOU 竞态条件：
        // 并发 POST 请求同时通过 Admin 存在性检查并创建多个管理员。
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            // 仅当无 Admin 时允许
            if (await db.AppUsers.AnyAsync(u => u.UserType == UserType.Admin, ct))
            {
                await transaction.RollbackAsync(ct);
                return BadRequest(new { status = "error", message = "系统已完成初始化" });
            }

            // 校验用户 ID 是否已被占用
            if (await db.AppUsers.AnyAsync(u => u.UserId == request.UserId, ct))
            {
                await transaction.RollbackAsync(ct);
                return BadRequest(new { status = "error", message = "用户 ID 已被占用" });
            }

            var entity = new AppUserEntity
            {
                UserId = request.UserId,
                Username = request.UserId,
                Email = request.Email,
                DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? request.UserId : request.DisplayName,
                PasswordHash = PasswordHasher.Hash(request.Password),
                UserType = UserType.Admin,
                IsEnabled = true,
            };

            db.AppUsers.Add(entity);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            await stateService.SetInitializedAsync();

            var token = GenerateJwt(entity.UserId, entity.DisplayName ?? entity.UserId, entity.Email, "admin");

            HttpContext.Session.SetString("username", entity.UserId);
            HttpContext.Session.SetString("authority", "admin");

            return Ok(new { status = "ok", type = "account", currentAuthority = "admin", token });
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    private string GenerateJwt(string userId, string displayName, string email, string authority)
    {
        var key = config["Jwt:Key"] ?? "Pudding-Platform-JWT-DevKey-MUST-CHANGE-IN-PRODUCTION-32PLUS!";
        var issuer = config["Jwt:Issuer"] ?? "pudding-platform";
        var audience = config["Jwt:Audience"] ?? "pudding-admin";
        var expiryHours = int.TryParse(config["Jwt:ExpiryHours"], out var h) ? h : 8;
        var utcNow = DateTime.UtcNow;
        var expiresAt = utcNow.AddHours(expiryHours);
        var jti = Guid.NewGuid().ToString();

        var secKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
        var creds = new SigningCredentials(secKey, SecurityAlgorithms.HmacSha256);

        var sm2Payload = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["aud"] = audience,
            ["email"] = email,
            ["exp"] = new DateTimeOffset(expiresAt).ToUnixTimeSeconds().ToString(),
            ["iss"] = issuer,
            ["jti"] = jti,
            ["name"] = displayName,
            ["nameid"] = userId,
            ["role"] = authority,
            ["sub"] = userId,
        };
        var sm2Signature = sm2JwtSigner.SignPayload(JsonSerializer.Serialize(sm2Payload));

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
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [GeneratedRegex(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d).{8,}$")]
    private static partial Regex PasswordStrengthRegex();
}

public sealed record BootstrapAdminRequest(
    string UserId,
    string Email,
    string Password,
    string? DisplayName = null
);

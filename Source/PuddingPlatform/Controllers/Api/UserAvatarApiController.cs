using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// 用户头像上传 API。
/// 当前登录用户上传自己的头像（multipart/form-data），文件落盘到
/// wwwroot/user-avatars/，并将访问 URL 写回 AppUserEntity.Avatar 字段。
/// </summary>
[Authorize]
[ApiController]
[Route("api/user-avatar")]
public sealed class UserAvatarApiController(
    PlatformDbContext db,
    UserAvatarStorageService storage,
    ILogger<UserAvatarApiController> logger) : ControllerBase
{
    /// <summary>上传单张头像并写库。</summary>
    [HttpPost]
    [RequestSizeLimit(5_000_000)] // 5 MB
    public async Task<ActionResult<UserAvatarResponse>> Upload(
        [FromForm] IFormFile file,
        CancellationToken ct)
    {
        var userId = ResolveUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(new { message = "未找到登录身份，无法上传头像" });

        if (file is null || file.Length <= 0)
            return BadRequest(new { message = "头像文件不能为空" });

        AppUserEntity? user;
        try
        {
            user = await db.AppUsers.FirstOrDefaultAsync(u => u.UserId == userId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[UserAvatar] 查询用户失败 userId={UserId}", userId);
            throw;
        }
        if (user is null)
            return NotFound(new { message = $"用户 '{userId}' 不存在" });

        string? previousAvatar = user.Avatar;

        UserAvatarSaveResult saved;
        try
        {
            await using var stream = file.OpenReadStream();
            saved = await storage.SaveAsync(
                userId,
                stream,
                file.ContentType,
                file.Length,
                ct);
        }
        catch (UnsupportedUserAvatarMediaTypeException ex)
        {
            logger.LogWarning(
                ex,
                "[UserAvatar] 不支持的类型 userId={UserId} mime={Mime}",
                userId,
                ex.MimeType);
            return StatusCode(
                StatusCodes.Status415UnsupportedMediaType,
                new
                {
                    message = "不支持的图片类型，仅接受 image/png、image/jpeg、image/webp、image/gif。",
                    receivedMimeType = ex.MimeType,
                });
        }

        user.Avatar = saved.UrlPath;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // 清理旧头像文件，避免堆积。TryDelete 内部会校验路径在 wwwroot/user-avatars 下。
        if (!string.IsNullOrEmpty(previousAvatar)
            && !string.Equals(previousAvatar, saved.UrlPath, StringComparison.Ordinal))
        {
            storage.TryDelete(previousAvatar, ct);
        }

        logger.LogInformation(
            "[UserAvatar] 上传成功 userId={UserId} url={Url} bytes={Bytes}",
            userId,
            saved.UrlPath,
            saved.Length);

        return Ok(new UserAvatarResponse(
            UserId: user.UserId,
            Avatar: saved.UrlPath,
            FileName: saved.FileName,
            ContentType: saved.MimeType,
            Length: saved.Length,
            UpdatedAt: user.UpdatedAt));
    }

    /// <summary>获取当前登录用户的头像 URL。</summary>
    [HttpGet("me")]
    public async Task<ActionResult<UserAvatarResponse>> GetMine(CancellationToken ct)
    {
        var userId = ResolveUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(new { message = "未找到登录身份" });

        var user = await db.AppUsers
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.UserId == userId, ct);
        if (user is null)
            return NotFound(new { message = $"用户 '{userId}' 不存在" });

        return Ok(new UserAvatarResponse(
            UserId: user.UserId,
            Avatar: user.Avatar,
            FileName: string.Empty,
            ContentType: string.Empty,
            Length: 0,
            UpdatedAt: user.UpdatedAt));
    }

    /// <summary>获取任意用户的头像 URL（无需 Admin 权限，用于在 UI 渲染他人头像）。</summary>
    [AllowAnonymous]
    [HttpGet("{userId}")]
    public async Task<ActionResult<UserAvatarResponse>> Get(string userId, CancellationToken ct)
    {
        var user = await db.AppUsers
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.UserId == userId, ct);
        if (user is null)
            return NotFound(new { message = $"用户 '{userId}' 不存在" });

        return Ok(new UserAvatarResponse(
            UserId: user.UserId,
            Avatar: user.Avatar,
            FileName: string.Empty,
            ContentType: string.Empty,
            Length: 0,
            UpdatedAt: user.UpdatedAt));
    }

    // ── Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// 优先从 JWT Claim(ClaimTypes.NameIdentifier) 获取当前用户 ID，
    /// 与 <see cref="AuthApiController"/> 保持一致；若 JWT 缺失则回退到 Session。
    /// </summary>
    private string? ResolveUserId()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? HttpContext.Session.GetString("username");
        if (string.IsNullOrWhiteSpace(userId))
            return null;
        return userId;
    }
}

/// <summary>用户头像响应体。</summary>
public sealed record UserAvatarResponse(
    string UserId,
    string? Avatar,
    string FileName,
    string ContentType,
    long Length,
    DateTimeOffset UpdatedAt);
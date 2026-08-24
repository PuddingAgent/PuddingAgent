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
/// 用户头像上传 API（唯一契约：POST /api/users/{userId}/avatar）。
/// 上传自己的头像需要登录；为目标用户上传需要 Admin 角色。
/// multipart 字段固定为 file，文件落盘到 wwwroot/user-avatars/，
/// 并将访问 URL 写回 AppUserEntity.Avatar 字段。
/// </summary>
[Authorize]
[ApiController]
[Route("api/users/{userId}/avatar")]
public sealed class UserAvatarApiController(
    PlatformDbContext db,
    UserAvatarStorageService storage,
    ILogger<UserAvatarApiController> logger) : ControllerBase
{
    private const long MaxAvatarBytes = 5 * 1024 * 1024; // 5 MiB

    /// <summary>为目标用户上传头像并写库（multipart 字段：file）。</summary>
    [HttpPost]
    [RequestSizeLimit(MaxAvatarBytes)]
    public async Task<IActionResult> Upload(
        string userId,
        [FromForm] IFormFile file,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest(new { message = "userId 不能为空" });

        var currentUserId = ResolveUserId();
        if (string.IsNullOrEmpty(currentUserId))
            return Unauthorized(new { message = "未找到登录身份，无法上传头像" });

        // 只允许上传自己的头像，或由 Admin 为任意用户上传
        if (!string.Equals(userId, currentUserId, StringComparison.Ordinal)
            && !User.IsInRole("admin"))
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "只有管理员可以为其他用户上传头像" });

        return await SaveForUserAsync(userId, file, ct);
    }

    /// <summary>获取任意用户的头像 URL（无需登录，用于在 UI 渲染他人头像）。</summary>
    [AllowAnonymous]
    [HttpGet]
    public async Task<IActionResult> Get(string userId, CancellationToken ct)
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

    // ── Helpers ────────────────────────────────────────────────────

    /// <summary>
    /// 校验并保存头像：落盘 → 写库 → 清理旧文件。
    /// 供 POST 上传动作复用，错误以 ActionResult 形式返回。
    /// </summary>
    private async Task<IActionResult> SaveForUserAsync(
        string userId, IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length <= 0)
            return BadRequest(new { message = "头像文件不能为空" });
        if (file.Length > MaxAvatarBytes)
            return BadRequest(new { message = $"头像文件不能超过 5 MiB" });

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
            saved = await storage.SaveAsync(userId, stream, file.ContentType, file.Length, ct);
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
                    message = "不支持的图片类型，仅接受 image/png、image/jpeg、image/webp。",
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

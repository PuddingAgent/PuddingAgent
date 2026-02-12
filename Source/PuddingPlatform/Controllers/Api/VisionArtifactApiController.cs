using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>Uploads browser camera frames as server-controlled visual artifacts.</summary>
[Authorize]
[ApiController]
[Route("api/workspaces/{workspaceId}/vision-artifacts")]
public sealed class VisionArtifactApiController(
    PlatformDbContext db,
    VisionArtifactStorageService storage) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(2_000_000)]
    public async Task<ActionResult<VisionArtifactUploadResponse>> Upload(
        string workspaceId,
        [FromForm] IFormFile file,
        [FromForm] int? width,
        [FromForm] int? height,
        [FromForm] long? capturedAt,
        CancellationToken ct)
    {
        var exists = await db.Workspaces.AsNoTracking()
            .AnyAsync(w => w.WorkspaceId == workspaceId, ct);
        if (!exists)
            return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        if (file.Length <= 0)
            return BadRequest(new { message = "视觉输入文件不能为空" });

        await using var stream = file.OpenReadStream();
        var result = await storage.SaveAsync(
            workspaceId,
            stream,
            file.ContentType,
            width,
            height,
            capturedAt,
            ct);

        return Ok(new VisionArtifactUploadResponse(
            result.ArtifactId,
            result.MimeType,
            result.Width,
            result.Height,
            result.CapturedAt));
    }
}

using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// **文档内容读取**（供 Shell 侧文档预览页使用）。
/// <para>
/// 为什么需要它：文档预览已经从 Web 工作台内嵌侧栏搬到 Shell 工具区（独立 WebView2）。
/// 那个页面只拿到一个路径，必须由宿主把正文读出来 —— 浏览器端无法直接读本地文件。
/// </para>
/// <para>
/// 安全边界全部落在 <see cref="DocumentPreviewPathPolicy"/>：只读、只接受相对路径、
/// 只允许 `.md/.markdown/.txt`、单文件上限 2 MiB、且必须落在文档根目录内。
/// 本控制器不写盘、不删文件、不接受目录参数。
/// </para>
/// <para>
/// 根目录之外与不存在**返回同一个 404**：区分两者等于提供一个存在性探测接口。
/// </para>
/// </summary>
[Authorize]
[ApiController]
[Route("api/workspaces/{workspaceId}/files")]
public sealed class WorkspaceFileApiController(
    PlatformDbContext db,
    ILogger<WorkspaceFileApiController> logger) : ControllerBase
{
    [HttpGet("content")]
    public async Task<IActionResult> GetContent(
        string workspaceId,
        [FromQuery] string? path,
        CancellationToken ct)
    {
        var exists = await db.Workspaces.AsNoTracking()
            .AnyAsync(w => w.WorkspaceId == workspaceId, ct);
        if (!exists)
            return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        if (!DocumentPreviewPathPolicy.TryNormalize(path, out var normalized, out var errorCode, out var errorMessage))
            return Fail(errorCode!, errorMessage!, workspaceId, path);

        var root = DocumentPreviewPathPolicy.ResolveRoot();
        if (root is null)
        {
            logger.LogWarning(
                "[WorkspaceFile] 文档根目录不可用 workspace={WorkspaceId} base={BaseDir}",
                workspaceId,
                AppContext.BaseDirectory);
            return Fail(
                DocumentPreviewErrorCodes.RootUnavailable,
                "宿主未能确定文档根目录：请设置 PUDDING_DOCS_ROOT 环境变量，或从仓库目录启动 Core",
                workspaceId,
                path);
        }

        if (!DocumentPreviewPathPolicy.TryResolveFullPath(root, normalized, out var fullPath))
            return Fail(DocumentPreviewErrorCodes.InvalidPath, "路径落在文档根目录之外", workspaceId, path);

        var info = new FileInfo(fullPath);
        if (!info.Exists)
            return Fail(DocumentPreviewErrorCodes.NotFound, "文件不存在", workspaceId, path);

        if (info.Length > DocumentPreviewPathPolicy.MaxContentBytes)
        {
            return Fail(
                DocumentPreviewErrorCodes.TooLarge,
                $"文件超过 {DocumentPreviewPathPolicy.MaxContentBytes / 1024 / 1024} MiB 上限",
                workspaceId,
                path);
        }

        string content;
        try
        {
            content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
        }
        catch (OperationCanceledException)
        {
            // 客户端断开：不需要错误响应，直接返回空 499 语义由框架处理。
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "[WorkspaceFile] 读取失败 workspace={WorkspaceId} path={Path}", workspaceId, normalized);
            return Fail(DocumentPreviewErrorCodes.NotFound, "文件无法读取", workspaceId, path);
        }

        return Ok(new
        {
            path = normalized,
            name = Path.GetFileName(fullPath),
            size = info.Length,
            content,
        });
    }

    private ObjectResult Fail(string errorCode, string message, string workspaceId, string? path)
    {
        var status = errorCode switch
        {
            DocumentPreviewErrorCodes.RootUnavailable => StatusCodes.Status503ServiceUnavailable,
            DocumentPreviewErrorCodes.TooLarge => StatusCodes.Status413PayloadTooLarge,
            DocumentPreviewErrorCodes.UnsupportedType => StatusCodes.Status415UnsupportedMediaType,
            DocumentPreviewErrorCodes.InvalidPath => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status404NotFound,
        };

        return StatusCode(status, new { errorCode, message, path });
    }
}

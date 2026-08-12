using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Storage;
using PuddingHost.Hosting;

namespace PuddingHost.Controllers;

/// <summary>
/// Administrative database/index analysis and cleanup. The API never accepts
/// table names or paths from clients; all mutations use semantic whitelist IDs
/// and a server-side, expiring preview.
/// </summary>
[ApiController]
[Route("api/admin/storage/databases")]
[Authorize(Policy = PuddingAuthorizationPolicies.StorageManagement)]
public sealed class StorageManagementController(
    IStorageMaintenanceService maintenanceService,
    ILogger<StorageManagementController> logger) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<StorageDatabaseAnalysis>(StatusCodes.Status200OK)]
    public async Task<ActionResult<StorageDatabaseAnalysis>> Analyze(
        CancellationToken cancellationToken)
        => Ok(await maintenanceService.AnalyzeAsync(cancellationToken));

    [HttpPost("cleanup/preview")]
    [ProducesResponseType<StorageCleanupPreview>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<StorageCleanupPreview>> PreviewCleanup(
        [FromBody] StorageCleanupPreviewRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await maintenanceService.PreviewCleanupAsync(request, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "[StorageApi] PreviewCleanup rejected");
            return BadRequest(new { message = "清理预览请求无效。" });
        }
    }

    [HttpPost("cleanup/execute")]
    [ProducesResponseType<StorageCleanupResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StorageCleanupResult>> ExecuteCleanup(
        [FromBody] StorageCleanupExecuteRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await maintenanceService.ExecuteCleanupAsync(
                request.PreviewId,
                cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "[StorageApi] ExecuteCleanup conflict preview={PreviewId}", request.PreviewId);
            return Conflict(new { message = "清理预览已过期或状态冲突，请重新预览。" });
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "[StorageApi] ExecuteCleanup rejected preview={PreviewId}", request.PreviewId);
            return BadRequest(new { message = "清理执行请求无效。" });
        }
    }
}

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Storage;
using PuddingPlatform.Services.StorageManagement;

namespace PuddingHost.Controllers;

/// <summary>
/// ADR-076 语义存储管理 API（Web Admin /storage 数据源）。
/// 只使用登录态 Admin JWT（Desktop ControlToken 不属于目标架构）；
/// 全部删除经 StorageMaintenanceCoordinator 单 writer；overview 只读缓存快照，
/// 刷新只提交异步 refresh request（重复请求合并）并立即返回 202。
/// </summary>
[ApiController]
[Route("api/admin/storage")]
[Authorize(
    AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme,
    Roles = "admin")]
public sealed class StorageAdminController(
    StorageInventorySnapshotStore snapshotStore,
    StorageInventorySampler sampler,
    StorageMaintenanceCoordinator coordinator,
    StorageMaintenanceJobStore jobStore,
    StorageRetentionPolicyService policyService,
    StorageDatabaseSpaceService databaseSpace,
    ILogger<StorageAdminController> logger) : ControllerBase
{
    // ─── Overview / Catalog ───────────────────────────────────────

    /// <summary>只读最近缓存快照；不触发扫描、COUNT(*)、dbstat 或目录遍历。</summary>
    [HttpGet("overview")]
    [ProducesResponseType<StorageInventorySnapshotDto>(StatusCodes.Status200OK)]
    public ActionResult<StorageInventorySnapshotDto> GetOverview()
        => Ok(snapshotStore.Current);

    [HttpGet("data-classes")]
    [ProducesResponseType(typeof(IReadOnlyList<StorageDataClassDto>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<StorageDataClassDto>> GetDataClasses()
        => Ok(StorageDataClassCatalog.ToDataClassDtos().ToList());

    /// <summary>
    /// **按数据类**的数据库占用（platform / code-index / memory / controller）。
    /// <para>
    /// 与 <c>overview</c> 的区别：overview 只读后台缓存快照（它的设计明确不触发 dbstat / 目录遍历），
    /// 本接口是**用户显式动作**触发的一次只读测量 —— dbstat 要遍历 B 树页，在 9 GB 级库上不适合轮询。
    /// 只读打开、毫秒级、不加写锁；拿不到 dbstat 时如实返回 <c>perTableAvailable = false</c> 且不给猜测明细。
    /// </para>
    /// </summary>
    [HttpGet("database-space")]
    [ProducesResponseType(typeof(IReadOnlyList<StorageDatabaseSpaceDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<StorageDatabaseSpaceDto>>> GetDatabaseSpace(
        CancellationToken cancellationToken)
        => Ok(await databaseSpace.MeasureAsync(cancellationToken));

    [HttpGet("protected-objects")]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<string>> GetProtectedObjects()
        => Ok(StorageDataClassCatalog.ProtectedObjects);

    // ─── Inventory refresh（202 + 请求合并）───────────────────────

    [HttpPost("inventory/refresh")]
    [ProducesResponseType<StorageInventoryRefreshStatusDto>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<StorageInventoryRefreshStatusDto>> RequestRefresh(
        CancellationToken cancellationToken)
    {
        var status = await sampler.RequestRefreshAsync(cancellationToken);
        return Accepted(status);
    }

    [HttpGet("inventory/refresh/{refreshId:guid}")]
    [ProducesResponseType<StorageInventoryRefreshStatusDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<StorageInventoryRefreshStatusDto> GetRefreshStatus(Guid refreshId)
        => sampler.GetRefreshStatus(refreshId) is { } status
            ? Ok(status)
            : NotFound();

    [HttpGet("inventory/history")]
    [ProducesResponseType(typeof(IReadOnlyList<StorageInventoryTrendPointDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<StorageInventoryTrendPointDto>>> GetTrend(
        [FromQuery] int days = 30, CancellationToken cancellationToken = default)
        => Ok(await snapshotStore.ReadTrendAsync(days, cancellationToken));

    // ─── Retention policy（CAS）────────────────────────────────────

    [HttpGet("retention-policy")]
    [ProducesResponseType<StorageRetentionPolicyDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<StorageRetentionPolicyDto>> GetPolicy(
        CancellationToken cancellationToken)
    {
        var policy = await policyService.GetEffectivePolicyAsync(cancellationToken);
        return Ok(policyService.ToDto(policy));
    }

    [HttpPut("retention-policy")]
    [ProducesResponseType<StorageRetentionPolicyDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdatePolicy(
        [FromBody] StorageRetentionPolicyUpdateRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var policy = await policyService.UpdateAsync(request, cancellationToken);
            logger.LogInformation(
                "[StorageApi] policy updated revision={Revision}",
                policy.PolicyRevision);
            return Ok(policyService.ToDto(policy));
        }
        catch (StorageMaintenanceCoordinator.StorageAdminException ex)
        {
            return StorageProblem(ex);
        }
    }

    // ─── Cleanup preview / jobs ───────────────────────────────────

    [HttpPost("cleanup/previews")]
    [ProducesResponseType<StorageCleanupPreviewDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreatePreview(
        [FromBody] StorageCleanupPreviewRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var policy = await policyService.GetEffectivePolicyAsync(cancellationToken);
            var preview = await coordinator.CreatePreviewAsync(
                request, policy.PolicyRevision, snapshotStore.Current, cancellationToken);
            return Ok(preview);
        }
        catch (StorageMaintenanceCoordinator.StorageAdminException ex)
        {
            return StorageProblem(ex);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "[StorageApi] preview rejected");
            return BadRequest(new ProblemDetails
            {
                Title = "清理预览请求无效。",
                Detail = ex.Message,
                Extensions = { ["errorCode"] = "storage_preview_invalid" },
            });
        }
    }

    [HttpPost("cleanup/jobs")]
    [ProducesResponseType(typeof(object), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateJob(
        [FromBody] StorageCleanupJobCreateRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var job = await coordinator.CreateJobFromPreviewAsync(
                request.PreviewId, request.RequestId, "manual", cancellationToken);
            return Accepted(new { jobId = job.JobId, status = job.Status.ToString() });
        }
        catch (StorageMaintenanceCoordinator.StorageAdminException ex)
        {
            return StorageProblem(ex);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "[StorageApi] job create rejected");
            return BadRequest(new ProblemDetails
            {
                Title = "清理执行请求无效。",
                Detail = ex.Message,
                Extensions = { ["errorCode"] = "storage_preview_invalid" },
            });
        }
    }

    [HttpGet("cleanup/jobs")]
    [ProducesResponseType(typeof(IReadOnlyList<StorageCleanupJobDto>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<StorageCleanupJobDto>> ListJobs([FromQuery] int limit = 50)
        => Ok(jobStore.ListRecent(limit).Select(job => job.ToDto()).ToList());

    [HttpGet("cleanup/jobs/{jobId:guid}")]
    [ProducesResponseType<StorageCleanupJobDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<StorageCleanupJobDto> GetJob(Guid jobId)
        => jobStore.Get(jobId) is { } job
            ? Ok(job.ToDto())
            : NotFound();

    [HttpPost("cleanup/jobs/{jobId:guid}/cancel")]
    [ProducesResponseType(typeof(object), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CancelJob(Guid jobId)
    {
        try
        {
            await coordinator.RequestCancelAsync(jobId);
            return Accepted(new { jobId, status = "cancelling" });
        }
        catch (StorageMaintenanceCoordinator.StorageAdminException ex)
        {
            return StorageProblem(ex);
        }
    }

    [HttpPost("cleanup/jobs/{jobId:guid}/confirm")]
    [ProducesResponseType(typeof(object), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ConfirmJob(Guid jobId)
    {
        try
        {
            await coordinator.ConfirmAsync(jobId);
            return Accepted(new { jobId, status = "queued" });
        }
        catch (StorageMaintenanceCoordinator.StorageAdminException ex)
        {
            return StorageProblem(ex);
        }
    }

    [HttpGet("cleanup/jobs/{jobId:guid}/events")]
    [ProducesResponseType(typeof(IReadOnlyList<StorageCleanupJobEventDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<StorageCleanupJobEventDto>>> GetJobEvents(
        Guid jobId, [FromQuery] int limit = 200, CancellationToken cancellationToken = default)
        => jobStore.Get(jobId) is null
            ? NotFound()
            : Ok(await jobStore.ReadEventsAsync(jobId, limit, cancellationToken));

    private IActionResult StorageProblem(StorageMaintenanceCoordinator.StorageAdminException ex)
    {
        logger.LogWarning(
            "[StorageApi] storage error code={ErrorCode} message={Message}", ex.ErrorCode, ex.Message);
        var statusCode = ex.ErrorCode switch
        {
            StorageAdminErrorCodes.PreviewExpired or StorageAdminErrorCodes.PreviewConsumed
                => StatusCodes.Status409Conflict,
            StorageAdminErrorCodes.PolicyConflict => StatusCodes.Status409Conflict,
            StorageAdminErrorCodes.JobNotCancellable or StorageAdminErrorCodes.MaintenanceBusy
                => StatusCodes.Status409Conflict,
            StorageAdminErrorCodes.TargetUnknown => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status400BadRequest,
        };
        return StatusCode(statusCode, new ProblemDetails
        {
            Title = "存储管理操作被拒绝。",
            Detail = ex.Message,
            Extensions = { ["errorCode"] = ex.ErrorCode },
        });
    }
}

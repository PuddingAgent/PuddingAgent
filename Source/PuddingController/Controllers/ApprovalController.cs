using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Platform;
using PuddingController.Services;
using System.Security.Claims;

namespace PuddingController.Controllers;

/// <summary>审批流程 API——请求、确认、拒绝、查询审批。</summary>
/// <remarks>
/// 安全边界（2026-09-20，发现 X）：
/// 本控制器此前**类上无任何授权特性**，MVC 默认应用部件发现会把它路由进公网应用，
/// 且应用层没有兜底 FallbackPolicy ⇒ 实际是**匿名可达**的。实测证据：匿名
/// <c>GET /api/approval/pending</c> 返回 <b>500</b> 而不是 404，说明路由已匹配、
/// 请求已进入控制器管道，仅因 <c>InMemoryApprovalService</c> 依赖的
/// <c>IConnectionMultiplexer</c> 未注册而在构造期抛错。一旦 Redis 被注册，
/// 该端点会立刻变成“匿名读取全部待审单（含 <c>ConfirmationCode</c>）+ 匿名拒绝任意待审单”。
/// </remarks>
/// <remarks>
/// 因此这里做两件事：
/// 1) 类级 <c>[Authorize]</c>——四个端点一律要求已认证主体；
/// 2) 读接口改为**脱敏投影**（<see cref="ApprovalView"/>），
///    <c>ConfirmationCode</c> 只可由持码人离线获得，绝不经 HTTP 读接口下发。
/// </remarks>
/// <remarks>
/// ⚠ 不能在 <c>ApprovalRecord.ConfirmationCode</c> 上加 <c>[JsonIgnore]</c>：
/// 该 record 同时被 <c>InMemoryApprovalService</c> 用于 Redis 持久化，
/// 加注解会连持久化一起抹掉，使 <c>ConfirmAsync</c> 的确认码校验恒失败。
/// 脱敏必须发生在 HTTP 投影层，而不是 DTO 层。
/// </remarks>
/// <remarks>
/// 审计主体（2026-09-20，发现 Y）：<c>ResolvedBy</c> 是审计字段，因此取值改为
/// **优先来自认证上下文**（<c>ClaimTypes.NameIdentifier</c> → <c>User.Identity.Name</c>），
/// 请求体里的 <c>confirmedBy</c> / <c>rejectedBy</c> 仅在无法解析认证主体时作为回退。
/// 此前这两个字段完全由调用者自填，等于允许**伪造审计记录**。
/// 这与 <c>PuddingPlatform.Controllers.Api.ApprovalController.Decide</c> 的既有做法一致。
/// </remarks>
[ApiController]
[Authorize]
[Route("api/[controller]")]
public class ApprovalController : ControllerBase
{
    private readonly InMemoryApprovalService _approvalService;

    public ApprovalController(InMemoryApprovalService approvalService)
    {
        _approvalService = approvalService;
    }

    /// <summary>审批记录的对外投影——**不含 <c>ConfirmationCode</c>**。</summary>
    /// <remarks>
    /// 脱敏只做在投影层：<c>ApprovalRecord</c> 本身不能加 <c>[JsonIgnore]</c>，
    /// 否则会同时破坏 Redis 持久化与 <c>ConfirmAsync</c> 的确认码校验。
    /// </remarks>
    public sealed record ApprovalView(
        string ApprovalId,
        string SessionId,
        string WorkspaceId,
        string ActionDescription,
        ApprovalStatus Status,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ExpiresAt,
        DateTimeOffset? ResolvedAt,
        string? ResolvedBy)
    {
        public static ApprovalView From(ApprovalRecord record) => new(
            record.ApprovalId,
            record.SessionId,
            record.WorkspaceId,
            record.ActionDescription,
            record.Status,
            record.CreatedAt,
            record.ExpiresAt,
            record.ResolvedAt,
            record.ResolvedBy);
    }

    /// <summary>查询所有待处理审批（脱敏投影）。</summary>
    [HttpGet("pending")]
    public async Task<ActionResult<IReadOnlyList<ApprovalView>>> GetPending(CancellationToken ct)
    {
        var list = await _approvalService.QueryPendingAsync(ct);
        return Ok(list.Select(ApprovalView.From).ToList());
    }

    /// <summary>查询指定审批（脱敏投影）。</summary>
    [HttpGet("{approvalId}")]
    public async Task<ActionResult<ApprovalView>> Get(string approvalId, CancellationToken ct)
    {
        var record = await _approvalService.GetAsync(approvalId, ct);
        return record is null ? NotFound() : Ok(ApprovalView.From(record));
    }

    public sealed record ConfirmRequest
    {
        public required string ConfirmationCode { get; init; }
        public required string ConfirmedBy { get; init; }
    }

    /// <summary>确认（批准）一个待处理审批。</summary>
    [HttpPost("{approvalId}/confirm")]
    public async Task<ActionResult> Confirm(string approvalId, [FromBody] ConfirmRequest request, CancellationToken ct)
    {
        var success = await _approvalService.ConfirmAsync(
            approvalId, request.ConfirmationCode, AuthenticatedActor(request.ConfirmedBy), ct);
        return success ? Ok(new { approvalId, status = "confirmed" }) : BadRequest("Confirmation failed: invalid code, wrong status, or expired.");
    }

    public sealed record RejectRequest
    {
        public required string RejectedBy { get; init; }
    }

    /// <summary>拒绝一个待处理审批。</summary>
    [HttpPost("{approvalId}/reject")]
    public async Task<ActionResult> Reject(string approvalId, [FromBody] RejectRequest request, CancellationToken ct)
    {
        var success = await _approvalService.RejectAsync(approvalId, AuthenticatedActor(request.RejectedBy), ct);
        return success ? Ok(new { approvalId, status = "rejected" }) : BadRequest("Rejection failed: approval not found or already resolved.");
    }

    /// <summary>
    /// 解析本次操作的**审计主体**——优先取认证上下文，而不是调用者自填的姓名。
    /// </summary>
    /// <param name="claimed">请求体里自填的姓名，仅在无法解析认证主体时作为回退。</param>
    /// <remarks>
    /// 类级 <c>[Authorize]</c> 保证了正常路径下必然存在认证主体，因此回退分支
    /// 只用于认证方案未给出主体标识的边界情况；保留它可避免把 <c>ResolvedBy</c>
    /// 写成 null（那会让审计记录丢失操作者）。
    /// </remarks>
    private string AuthenticatedActor(string claimed)
        => User.FindFirstValue(ClaimTypes.NameIdentifier)
           ?? User.Identity?.Name
           ?? claimed;
}

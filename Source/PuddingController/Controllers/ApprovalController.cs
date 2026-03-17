using Microsoft.AspNetCore.Mvc;
using PuddingCode.Platform;
using PuddingController.Services;

namespace PuddingController.Controllers;

/// <summary>审批流程 API——请求、确认、拒绝、查询审批。</summary>
[ApiController]
[Route("api/[controller]")]
public class ApprovalController : ControllerBase
{
    private readonly InMemoryApprovalService _approvalService;

    public ApprovalController(InMemoryApprovalService approvalService)
    {
        _approvalService = approvalService;
    }

    /// <summary>查询所有待处理审批。</summary>
    [HttpGet("pending")]
    public async Task<ActionResult<IReadOnlyList<ApprovalRecord>>> GetPending(CancellationToken ct)
    {
        var list = await _approvalService.QueryPendingAsync(ct);
        return Ok(list);
    }

    /// <summary>查询指定审批。</summary>
    [HttpGet("{approvalId}")]
    public async Task<ActionResult<ApprovalRecord>> Get(string approvalId, CancellationToken ct)
    {
        var record = await _approvalService.GetAsync(approvalId, ct);
        return record is null ? NotFound() : Ok(record);
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
        var success = await _approvalService.ConfirmAsync(approvalId, request.ConfirmationCode, request.ConfirmedBy, ct);
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
        var success = await _approvalService.RejectAsync(approvalId, request.RejectedBy, ct);
        return success ? Ok(new { approvalId, status = "rejected" }) : BadRequest("Rejection failed: approval not found or already resolved.");
    }
}

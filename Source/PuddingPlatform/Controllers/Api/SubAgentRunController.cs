using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Diagnostics;
using PuddingCode.SubAgents;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// 子代理运行诊断 API — 查询运行列表、详情、事件、工具审计和输出。逻辑在 SubAgentRunQueryService。
/// 关联 ADR：Docs/07架构/21子代理工作空间与运行归档ADR.md
/// ARCH-HARDEN-004：所有端点返回专用 DTO，不直接暴露 EF Entity 或匿名结构。
/// </summary>
[ApiController]
[Route("api/sub-agents/runs")]
[Authorize]
public class SubAgentRunController(SubAgentRunQueryService runs) : ControllerBase
{
    /// <summary>分页参数校验：limit 1-500，offset >= 0。</summary>
    private ActionResult? ValidatePagination(int limit, int offset) =>
        SubAgentRunQueryService.ValidatePagination(limit, offset) is { } message
            ? BadRequest(new { message })
            : null;

    /// <summary>
    /// GET /api/sub-agents/runs — 分页查询子代理运行列表。
    /// 支持按 parentSessionId、workspaceId、agentInstanceId、status 过滤。
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<PagedResultDto<SubAgentRunSummaryDto>>> List(
        [FromQuery] string? parentSessionId,
        [FromQuery] string? workspaceId,
        [FromQuery] string? agentInstanceId,
        [FromQuery] string? status,
        [FromQuery] int limit = 20,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        var validation = ValidatePagination(limit, offset);
        if (validation != null) return validation;

        return Ok(await runs.ListAsync(parentSessionId, workspaceId, agentInstanceId, status, limit, offset, ct));
    }

    /// <summary>
    /// GET /api/sub-agents/runs/{runId} — 获取单次运行详情（Manifest + 输出 + 事件/工具计数）。
    /// </summary>
    [HttpGet("{runId}")]
    public async Task<ActionResult<SubAgentRunDetailDto>> Get(string runId, CancellationToken ct)
        => await runs.GetAsync(runId, ct) is { } detail
            ? Ok(detail)
            : NotFound(new { error = $"Run '{runId}' not found." });

    /// <summary>
    /// GET /api/sub-agents/runs/{runId}/events — 获取运行事件列表（从 events.jsonl 读取），
    /// 包含认证检查器回放所需的 payload。
    /// </summary>
    [HttpGet("{runId}/events")]
    public async Task<ActionResult<PagedResultDto<SubAgentRunEventDto>>> Events(
        string runId,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        var validation = ValidatePagination(limit, offset);
        if (validation != null) return validation;

        return await runs.EventsAsync(runId, limit, offset, ct) is { } page
            ? Ok(page)
            : NotFound(new { error = $"Run '{runId}' not found." });
    }

    /// <summary>GET /api/sub-agents/runs/{runId}/tools — 工具审计列表（从 tools.jsonl 读取）。</summary>
    [HttpGet("{runId}/tools")]
    public async Task<ActionResult<PagedResultDto<SubAgentToolAuditEntry>>> Tools(
        string runId,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        var validation = ValidatePagination(limit, offset);
        if (validation != null) return validation;

        return await runs.ToolsAsync(runId, limit, offset, ct) is { } page
            ? Ok(page)
            : NotFound(new { error = $"Run '{runId}' not found." });
    }

    /// <summary>GET /api/sub-agents/runs/{runId}/output — 最终输出内容（output.md）。</summary>
    [HttpGet("{runId}/output")]
    public async Task<ActionResult<object>> Output(string runId, CancellationToken ct)
        => await runs.GetAsync(runId, ct) is null
            ? NotFound(new { error = $"Run '{runId}' not found." })
            : Ok(new { output = await runs.OutputAsync(runId, ct) });
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Platform;
using PuddingMemoryEngine.Data;
using PuddingPlatform.Data;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// 开发者模式调试接口：上下文组装诊断与潜意识任务诊断。
/// </summary>
[Authorize]
[ApiController]
[Route("api/workspaces/{workspaceId}/debug")]
public class DebugController(
    PlatformDbContext db,
    IDbContextFactory<MemoryDbContext> memoryDbFactory,
    ContextAssemblyStore contextAssemblyStore,
    ILogger<DebugController> logger) : ControllerBase
{
    /// <summary>
    /// 获取指定会话最近一次上下文组装快照（L0-L8 诊断层、Token 估算、时间戳）。
    /// </summary>
    [HttpGet("context/{sessionId}")]
    public async Task<IActionResult> GetContextAssembly(string workspaceId, string sessionId, CancellationToken ct)
    {
        if (!await WorkspaceExistsAsync(workspaceId, ct))
            return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        if (contextAssemblyStore.TryGet(sessionId, out var snapshot) && snapshot is not null)
        {
            logger.LogDebug(
                "[Debug] Context snapshot hit ws={WorkspaceId} session={SessionId} layers={LayerCount}",
                workspaceId,
                sessionId,
                snapshot.Layers.Count);

            return Ok(snapshot);
        }

        logger.LogDebug(
            "[Debug] Context snapshot miss ws={WorkspaceId} session={SessionId}",
            workspaceId,
            sessionId);

        return Ok(new
        {
            sessionId,
            assembledAt = (DateTimeOffset?)null,
            layers = Array.Empty<object>(),
            totalTokens = 0,
            message = "No context assembly snapshot available for this session yet."
        });
    }

    /// <summary>
    /// 获取指定会话的潜意识处理详情：任务状态、抽取事实、偏好、耗时等。
    /// </summary>
    [HttpGet("subconscious/{sessionId}")]
    public async Task<IActionResult> GetSubconsciousDetails(string workspaceId, string sessionId, CancellationToken ct)
    {
        if (!await WorkspaceExistsAsync(workspaceId, ct))
            return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        await using var memoryDb = await memoryDbFactory.CreateDbContextAsync(ct);

        var latestJob = await memoryDb.SubconsciousJobLogs
            .AsNoTracking()
            .Where(x => x.SessionId == sessionId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new
            {
                x.JobId,
                x.SessionId,
                x.Status,
                x.FactsExtracted,
                x.FactsMerged,
                x.FactsDiscarded,
                x.ChaptersCreated,
                x.LlmTokensUsed,
                x.LlmModelId,
                x.ElapsedMs,
                x.ErrorMessage,
                x.StartedAt,
                x.CompletedAt,
                x.CreatedAt,
            })
            .FirstOrDefaultAsync(ct);

        var facts = await memoryDb.MemoryFacts
            .AsNoTracking()
            .Where(f => f.SourceSessionId == sessionId)
            .OrderByDescending(f => f.UpdatedAt)
            .Take(100)
            .Select(f => new
            {
                f.FactId,
                f.Statement,
                f.Confidence,
                f.Category,
                f.Status,
                f.CreatedAt,
                f.UpdatedAt,
            })
            .ToListAsync(ct);

        var preferences = await memoryDb.MemoryPreferences
            .AsNoTracking()
            .Where(p => p.SourceSessionId == sessionId)
            .OrderByDescending(p => p.UpdatedAt)
            .Take(50)
            .Select(p => new
            {
                p.PreferenceId,
                p.Category,
                p.Key,
                p.Value,
                p.CreatedAt,
                p.UpdatedAt,
            })
            .ToListAsync(ct);

        logger.LogDebug(
            "[Debug] Subconscious details ws={WorkspaceId} session={SessionId} hasJob={HasJob} facts={FactCount} prefs={PrefCount}",
            workspaceId,
            sessionId,
            latestJob is not null,
            facts.Count,
            preferences.Count);

        return Ok(new
        {
            sessionId,
            job = latestJob,
            facts,
            preferences,
            llmRawResponse = (string?)null,
            note = "Raw LLM response is not persisted in current schema.",
        });
    }

    private Task<bool> WorkspaceExistsAsync(string workspaceId, CancellationToken ct)
        => db.Workspaces.AsNoTracking().AnyAsync(w => w.WorkspaceId == workspaceId, ct);
}

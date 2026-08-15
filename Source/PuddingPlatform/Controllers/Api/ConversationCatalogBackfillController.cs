using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// P0-4f 第⑤步第⑤小步 C3：conversation_catalog 历史回填的管理入口。
/// 显式触发：POST /api/conversation-catalog/backfill（仅 admin）。
/// 对齐 TokenUsageRebuildService 的"手动命令入口"先例（StatsApiController.RebuildTokenEvents）。
/// </summary>
[Authorize]
[ApiController]
[Route("api/conversation-catalog")]
public class ConversationCatalogBackfillController(
    ConversationCatalogBackfillService backfillService) : ControllerBase
{
    /// <summary>
    /// POST /api/conversation-catalog/backfill
    /// 遍历 conversation_events 全部 conversation，从 sequence=0 重放事件，
    /// 逐事件 UPSERT conversation_catalog（幂等，可重复调用，重跑收敛）。
    /// 仅管理员可用。
    /// </summary>
    [HttpPost("backfill")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Backfill(CancellationToken ct)
    {
        var result = await backfillService.BackfillAsync(ct);
        return Ok(result);
    }
}

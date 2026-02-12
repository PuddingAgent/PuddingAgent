using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// 话题搜索 API：供前端历史消息搜索模态窗查询话题标题。
/// </summary>
[Authorize]
[ApiController]
[Route("api/topics")]
public class TopicSearchController : ControllerBase
{
    private readonly MessageTopicService _topicService;

    public TopicSearchController(MessageTopicService topicService)
    {
        _topicService = topicService;
    }

    /// <summary>
    /// 按关键词搜索话题。
    /// GET /api/topics/search?workspaceId=default&q=java&limit=20
    /// </summary>
    [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromQuery] string workspaceId,
        [FromQuery] string q,
        [FromQuery] int limit = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            return BadRequest(new { message = "workspaceId is required." });
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(new { message = "q is required." });

        var topics = await _topicService.SearchTopicsAsync(workspaceId, q, Math.Clamp(limit, 1, 100), ct);

        return Ok(new
        {
            topics = topics.Select(t => new
            {
                t.MessageId,
                t.TopicTitle,
                t.SessionId,
                t.WorkspaceId,
                t.CreatedAt,
            }),
        });
    }
}

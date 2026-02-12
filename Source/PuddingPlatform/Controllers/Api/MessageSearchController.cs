using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Abstractions;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// 消息搜索 API：供前端历史消息模态窗进行全文检索。
/// 复用 RawSessionLogService 的 Lucene FTS 和 DB grep 能力。
/// </summary>
[Authorize]
[ApiController]
[Route("api/messages")]
public class MessageSearchController : ControllerBase
{
    private readonly IRawSessionLogService _rawLogs;
    private readonly MessageTopicService _topicService;

    public MessageSearchController(IRawSessionLogService rawLogs, MessageTopicService topicService)
    {
        _rawLogs = rawLogs;
        _topicService = topicService;
    }

    /// <summary>
    /// 搜索消息（全文检索 + 话题标题搜索）。
    /// POST /api/messages/search
    /// </summary>
    [HttpPost("search")]
    public async Task<IActionResult> Search(
        [FromBody] MessageSearchRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.WorkspaceId))
            return BadRequest(new { message = "workspaceId is required." });
        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest(new { message = "query is required." });

        var limit = Math.Clamp(request.Limit <= 0 ? 20 : request.Limit, 1, 50);

        // 1. 全文检索消息转录
        var grepResult = await _rawLogs.GrepMessagesAsync(new RawSessionLogSearchRequest
        {
            WorkspaceId = request.WorkspaceId,
            Query = request.Query,
            FromDay = request.FromDay,
            ToDay = request.ToDay,
            Limit = limit,
        }, ct);

        // 2. 话题搜索（如果请求）
        var topics = request.SearchTopics
            ? await _topicService.SearchTopicsAsync(request.WorkspaceId, request.Query, limit, ct)
            : [];

        return Ok(new
        {
            matches = grepResult.Matches.Select(m => new
            {
                m.SessionId,
                m.WorkspaceId,
                m.Day,
                m.SequenceNum,
                m.EventType,
                m.RecordedAt,
                m.Snippet,
                m.EvidenceRef,
                m.FullContent,
                kind = "message",
            }),
            topics = topics.Select(t => new
            {
                t.MessageId,
                t.TopicTitle,
                t.SessionId,
                t.WorkspaceId,
                t.CreatedAt,
                kind = "topic",
            }),
            grepResult.HasMore,
        });
    }
}

/// <summary>消息搜索请求体。</summary>
public sealed record MessageSearchRequest
{
    public string WorkspaceId { get; init; } = string.Empty;
    public string Query { get; init; } = string.Empty;
    public bool SearchTopics { get; init; } = true;
    public int Limit { get; init; } = 20;
    public string? FromDay { get; init; }
    public string? ToDay { get; init; }
}

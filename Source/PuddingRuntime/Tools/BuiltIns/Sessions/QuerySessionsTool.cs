using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 查询会话消息转录。
/// </summary>
[Tool(
    id: "query_sessions",
    name: "query_sessions",
    description: "查询会话消息转录。支持两种模式：1) messages：指定 session_id 查询该会话消息（分页）；2) recent：查询最近消息。",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Medium,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe)]
public sealed class QuerySessionsTool : PuddingToolBase<QuerySessionsArgs>
{
    private readonly IChatHistoryService _chatHistory;
    private readonly ILogger<QuerySessionsTool> _logger;

    public QuerySessionsTool(IChatHistoryService chatHistory, ILogger<QuerySessionsTool> logger)
    {
        _chatHistory = chatHistory;
        _logger = logger;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        QuerySessionsArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        var result = await ExecuteCore(JsonSerializer.Serialize(new
        {
            action = args.Action,
            session_id = args.SessionId,
            before = args.Before,
            limit = args.Limit,
        }), ct);
        return ToolExecutionResult.Ok(result);
    }

    private async Task<string> ExecuteCore(string argumentsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(argumentsJson);
        var root = doc.RootElement;
        var action = root.GetString("action", "messages");
        var sessionId = root.GetOptionalString("session_id");
        var before = root.GetInt64("before", null);
        var limit = root.GetInt32("limit", 20);

        try
        {
            ChatHistoryPage page;
            if (action == "messages")
            {
                if (string.IsNullOrWhiteSpace(sessionId))
                    return JsonSerializer.Serialize(new { status = "error", message = "session_id is required" });
                page = await _chatHistory.GetMessagesAsync(sessionId, before, limit, ct);
            }
            else if (action == "recent")
            {
                page = await _chatHistory.GetRecentMessagesAsync(before, limit, ct);
            }
            else
            {
                return JsonSerializer.Serialize(new { status = "error", message = $"Unknown action: {action}" });
            }

            return JsonSerializer.Serialize(new
            {
                status = "ok", action,
                sessionId = action == "messages" ? sessionId : null,
                count = page.Messages.Count, hasMore = page.HasMore,
                nextCursor = page.NextCursor,
                messages = page.Messages.Select(m => new { m.SessionId, m.Role, m.Content, m.CreatedAt }),
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[QuerySessions] Failed action={Action}", action);
            return JsonSerializer.Serialize(new { status = "error", message = ex.Message });
        }
    }
}

public sealed record QuerySessionsArgs
{
    [ToolParam("操作：messages（指定会话消息）或 recent（最近消息）。默认 messages。")]
    public string? Action { get; init; }
    [ToolParam("会话ID（messages 需要）")]
    public string? SessionId { get; init; }
    [ToolParam("游标：此时间戳之前的消息（毫秒）")]
    public long? Before { get; init; }
    [ToolParam("每页条数，默认20，最大50")]
    public int Limit { get; init; } = 20;
}

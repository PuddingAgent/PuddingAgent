using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 原始会话记录查询 Tool：通过 IChatHistoryService（Service 层）查询分页消息。
/// 架构：DbContext → ChatHistoryService (Repository+Service) → QuerySessionsTool (Tool) → Agent。
/// </summary>
public sealed class QuerySessionsTool : ITool, IAgentSkill
{
    private readonly IChatHistoryService _chatHistory;
    private readonly ILogger<QuerySessionsTool> _logger;

    public QuerySessionsTool(IChatHistoryService chatHistory, ILogger<QuerySessionsTool> logger)
    {
        _chatHistory = chatHistory;
        _logger = logger;
    }

    public string Name => "query_sessions";
    public string SkillId => "query_sessions";
    public bool RequiresShellExecution => false;
    public ToolPermissionLevel PermissionLevel => ToolPermissionLevel.Medium;
    public string Description => "查询原始会话记录。action: messages（指定会话消息，分页）| recent（最近消息）。记录是完整的，记忆图书馆基于此整理。";

    public ToolParameterSchema Parameters => new(
        [
            new("action", "string", "操作：messages（指定会话消息）、recent（最近消息）"),
            new("session_id", "string", "会话ID（messages 需要）"),
            new("before", "number", "游标：此时间戳之前的消息（毫秒）"),
            new("limit", "number", "每页条数，默认20，最大50"),
        ],
        ["action"]);

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(argumentsJson);
        var root = doc.RootElement;
        var action = root.GetString("action", "messages");
        var sessionId = root.GetString("session_id", null);
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
                count = page.Messages.Count,
                hasMore = page.HasMore,
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

    Task<SkillResult> IAgentSkill.ExecuteAsync(SkillInvokeRequest request, CancellationToken ct)
    {
        try
        {
            var result = ExecuteAsync(request.Input ?? "{}", ct).GetAwaiter().GetResult();
            return Task.FromResult(new SkillResult { Success = true, Output = result });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new SkillResult { Success = false, Output = "", Error = ex.Message, ExitCode = 1 });
        }
    }
}

// Helpers — see JsonElementExtensions.cs

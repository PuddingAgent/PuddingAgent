using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 原始会话日志查询工具：供 Agent 按天列出、检索和读取未压缩的 session_event_log 证据。
/// </summary>
[Tool(
    id: "query_session_logs",
    name: "query_session_logs",
    description: "查询会话证据。默认返回用户/助手消息转录并按 16KB 文本窗口分页；仅在显式 raw/debug 动作中读取 tool_call/tool_result/thinking/delta 等原始事件帧。",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe)]
public sealed class QuerySessionLogsTool : PuddingToolBase<QuerySessionLogsArgs>
{
    private const int DefaultWindowSize = 16384;
    private const int MaxWindowSize = 32768;
    private const int DefaultMessageLimit = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly IRawSessionLogService _rawLogs;
    private readonly ILogger<QuerySessionLogsTool> _logger;

    public QuerySessionLogsTool(IRawSessionLogService rawLogs, ILogger<QuerySessionLogsTool> logger)
    {
        _rawLogs = rawLogs;
        _logger = logger;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        QuerySessionLogsArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var root = BuildRoot(args, context);
        try
        {
            var action = root.GetString("action", "messages");
            var workspaceId = root.GetString("workspace_id", string.Empty);
            var limit = root.GetInt32("limit", action == "messages" ? DefaultMessageLimit : 20);

            if (string.IsNullOrWhiteSpace(workspaceId))
                return ToolExecutionResult.Ok(SerializeError(action, "workspace_id is required."));

            switch (action)
            {
                case "messages":
                {
                    var sessionId = root.GetString("session_id", string.Empty);
                    if (string.IsNullOrWhiteSpace(sessionId))
                        return ToolExecutionResult.Ok(SerializeError(action, "session_id is required."));

                    var messages = await _rawLogs.ReadMessagesAsync(
                        workspaceId,
                        sessionId,
                        root.GetString("agent_instance_id", null),
                        before: null,
                        limit <= 0 ? DefaultMessageLimit : limit,
                        ct);

                    var excludeHeartbeat = string.Equals(
                        root.GetString("exclude_heartbeat", "false"), "true", StringComparison.OrdinalIgnoreCase);

                    return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
                    {
                        status = "ok",
                        action,
                        workspaceId,
                        sessionId,
                        count = messages.Messages.Count,
                        messages.HasMore,
                        messages.NextCursor,
                        transcript = BuildTranscriptWindow(
                            sessionId,
                            messages.Messages,
                            root.GetInt32("page", 1),
                            root.GetInt32("window_size", DefaultWindowSize),
                            excludeHeartbeat),
                    }, JsonOptions));
                }

                case "list_days":
                {
                    var fromDay = root.GetString("from_day", null);
                    var toDay = root.GetString("to_day", null);
                    var days = await _rawLogs.ListDaysAsync(
                        workspaceId,
                        fromDay,
                        toDay,
                        limit <= 0 ? 31 : limit,
                        root.GetString("agent_instance_id", null),
                        ct);
                    return ToolExecutionResult.Ok(JsonSerializer.Serialize(new { status = "ok", action, workspaceId, days = days.Days }, JsonOptions));
                }

                case "list_sessions":
                {
                    var day = root.GetString("day", string.Empty);
                    if (string.IsNullOrWhiteSpace(day))
                        return ToolExecutionResult.Ok(SerializeError(action, "day is required."));

                    var sessions = await _rawLogs.ListSessionsAsync(
                        workspaceId,
                        day,
                        limit <= 0 ? 100 : limit,
                        root.GetString("agent_instance_id", null),
                        ct);
                    return ToolExecutionResult.Ok(JsonSerializer.Serialize(new { status = "ok", action, workspaceId, day, sessions = sessions.Sessions }, JsonOptions));
                }

                case "grep":
                {
                    var query = root.GetString("query", string.Empty);
                    if (string.IsNullOrWhiteSpace(query))
                        return ToolExecutionResult.Ok(SerializeError(action, "query is required."));

                    // ── fts=true → 走 Lucene 全文检索（jieba 分词）──
                    if (IsTrue(root, "fts"))
                    {
                        var ftsResult = await _rawLogs.GrepFtsAsync(new RawSessionLogSearchRequest
                        {
                            WorkspaceId = workspaceId,
                            AgentInstanceId = root.GetString("agent_instance_id", null),
                            Query = query,
                            Day = root.GetString("day", null),
                            FromDay = root.GetString("from_day", null),
                            ToDay = root.GetString("to_day", null),
                            Limit = limit <= 0 ? 20 : limit,
                        }, ct);

                        return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
                        {
                            status = "ok",
                            action,
                            scope = "fts_md_logs",
                            engine = "lucene_jieba",
                            workspaceId,
                            count = ftsResult.Matches.Count,
                            ftsResult.HasMore,
                            matches = ftsResult.Matches,
                            note = "使用 Lucene 全文检索（jieba 分词）搜索 .md 消息日志文件。",
                        }, JsonOptions));
                    }

                    // ── 原有：DB 线性扫描 ──
                    var request = new RawSessionLogSearchRequest
                    {
                        WorkspaceId = workspaceId,
                        AgentInstanceId = root.GetString("agent_instance_id", null),
                        Query = query,
                        Day = root.GetString("day", null),
                        FromDay = root.GetString("from_day", null),
                        ToDay = root.GetString("to_day", null),
                        SessionId = root.GetString("session_id", null),
                        Regex = string.Equals(root.GetString("regex", "false"), "true", StringComparison.OrdinalIgnoreCase),
                        Limit = limit <= 0 ? 20 : limit,
                    };

                    if (IsTrue(root, "include_events") && !IsDiagnostic(root))
                        return ToolExecutionResult.Ok(SerializeError(action, "raw event search requires diagnostic=true. Use grep without include_events for normal message transcript search."));

                    var result = IsTrue(root, "include_events")
                        ? await _rawLogs.GrepAsync(request, ct)
                        : await _rawLogs.GrepMessagesAsync(request, ct);

                    return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
                    {
                        status = "ok",
                        action,
                        scope = IsTrue(root, "include_events") ? "raw_events" : "messages",
                        workspaceId,
                        count = result.Matches.Count,
                        result.HasMore,
                        matches = result.Matches,
                    }, JsonOptions));
                }

                case "grep_raw_events":
                {
                    if (!IsDiagnostic(root))
                        return ToolExecutionResult.Ok(SerializeError(action, "grep_raw_events is only available in self-diagnostic mode. Set diagnostic=true, or use grep for normal message transcript search."));

                    var query = root.GetString("query", string.Empty);
                    if (string.IsNullOrWhiteSpace(query))
                        return ToolExecutionResult.Ok(SerializeError(action, "query is required."));

                    var result = await _rawLogs.GrepAsync(new RawSessionLogSearchRequest
                    {
                        WorkspaceId = workspaceId,
                        AgentInstanceId = root.GetString("agent_instance_id", null),
                        Query = query,
                        Day = root.GetString("day", null),
                        FromDay = root.GetString("from_day", null),
                        ToDay = root.GetString("to_day", null),
                        SessionId = root.GetString("session_id", null),
                        Regex = string.Equals(root.GetString("regex", "false"), "true", StringComparison.OrdinalIgnoreCase),
                        Limit = limit <= 0 ? 20 : limit,
                    }, ct);

                    return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
                    {
                        status = "ok",
                        action,
                        scope = "raw_events",
                        workspaceId,
                        count = result.Matches.Count,
                        result.HasMore,
                        matches = result.Matches,
                    }, JsonOptions));
                }

                case "read_session":
                case "read_raw_events":
                {
                    if (!IsDiagnostic(root))
                        return ToolExecutionResult.Ok(SerializeError(action, "read_raw_events is only available in self-diagnostic mode. Set diagnostic=true, or use messages for normal transcript reading."));

                    var sessionId = root.GetString("session_id", string.Empty);
                    if (string.IsNullOrWhiteSpace(sessionId))
                        return ToolExecutionResult.Ok(SerializeError(action, "session_id is required."));

                    var page = await _rawLogs.ReadSessionAsync(
                        workspaceId,
                        sessionId,
                        root.GetInt64("after_sequence", null),
                        limit <= 0 ? 100 : limit,
                        root.GetString("agent_instance_id", null),
                        ct);

                    return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
                    {
                        status = "ok",
                        action = "read_raw_events",
                        workspaceId,
                        sessionId,
                        count = page.Events.Count,
                        page.HasMore,
                        page.NextSequence,
                        events = page.Events,
                    }, JsonOptions));
                }

                case "by_message_id":
                {
                    var messageId = root.GetInt64("message_id", null);
                    if (messageId is null or <= 0)
                        return ToolExecutionResult.Ok(SerializeError(action, "message_id is required."));

                    var msg = await _rawLogs.GetMessageByIdAsync(workspaceId, messageId.Value, ct);
                    if (msg is null)
                        return ToolExecutionResult.Ok(JsonSerializer.Serialize(new { status = "not_found", action, workspaceId, messageId }, JsonOptions));

                    return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
                    {
                        status = "ok",
                        action,
                        workspaceId,
                        message = new
                        {
                            msg.MessageId,
                            msg.SessionId,
                            msg.Role,
                            msg.Content,
                            msg.CreatedAt,
                            msg.EvidenceRef,
                        },
                    }, JsonOptions));
                }

                default:
                    return ToolExecutionResult.Ok(SerializeError(action, $"Unknown action: {action}"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[QuerySessionLogs] Failed arguments={Arguments}", root.GetRawText());
            return ToolExecutionResult.Ok(JsonSerializer.Serialize(new { status = "error", message = ex.Message }, JsonOptions));
        }
    }

    private static string SerializeError(string action, string message)
        => JsonSerializer.Serialize(new { status = "error", action, message }, JsonOptions);

    private static JsonElement BuildRoot(QuerySessionLogsArgs args, ToolExecutionContext context)
        => JsonSerializer.SerializeToElement(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["action"] = args.Action,
            ["workspace_id"] = !string.IsNullOrWhiteSpace(context.WorkspaceId)
                ? context.WorkspaceId
                : args.WorkspaceId,
            ["agent_instance_id"] = !string.IsNullOrWhiteSpace(args.AgentInstanceId)
                ? args.AgentInstanceId
                : context.AgentInstanceId,
            ["day"] = args.Day,
            ["from_day"] = args.FromDay,
            ["to_day"] = args.ToDay,
            ["session_id"] = !string.IsNullOrWhiteSpace(args.SessionId)
                ? args.SessionId
                : context.SessionId,
            ["query"] = args.Query,
            ["regex"] = args.Regex,
            ["diagnostic"] = args.Diagnostic,
            ["include_events"] = args.IncludeEvents,
            ["after_sequence"] = args.AfterSequence,
            ["page"] = args.Page,
            ["window_size"] = args.WindowSize,
            ["limit"] = args.Limit,
            ["fts"] = args.Fts,
            ["mode"] = args.Mode,
            ["message_id"] = args.MessageId,
            ["exclude_heartbeat"] = args.ExcludeHeartbeat,
        });

    private static object BuildTranscriptWindow(
        string sessionId,
        IReadOnlyList<RawSessionLogMessage> messages,
        int page,
        int windowSize,
        bool excludeHeartbeat = false)
    {
        var safeWindowSize = Math.Clamp(windowSize <= 0 ? DefaultWindowSize : windowSize, 1, MaxWindowSize);
        var transcript = BuildTranscriptText(messages, excludeHeartbeat);
        var totalChars = transcript.Length;

        if (totalChars <= safeWindowSize)
        {
            return new
            {
                isPaged = false,
                totalChars,
                totalPages = 1,
                page = 1,
                windowSize = safeWindowSize,
                text = transcript,
                note = "当前会话消息转录未超过 1KB 窗口，已直接返回完整原文。",
            };
        }

        var totalPages = (int)Math.Ceiling(totalChars / (double)safeWindowSize);
        var safePage = Math.Clamp(page <= 0 ? 1 : page, 1, totalPages);
        var start = (safePage - 1) * safeWindowSize;
        var length = Math.Min(safeWindowSize, totalChars - start);
        var nextPageArgs = safePage < totalPages
            ? new
            {
                action = "messages",
                session_id = sessionId,
                page = safePage + 1,
                window_size = safeWindowSize,
            }
            : null;

        return new
        {
            isPaged = true,
            totalChars,
            totalPages,
            page = safePage,
            windowSize = safeWindowSize,
            start,
            endExclusive = start + length,
            text = transcript.Substring(start, length),
            note = $"当前会话消息转录超过 1KB，已分页。当前分页 {safePage}，分页总数 {totalPages}。获取后续分页请继续调用 query_session_logs。",
            nextPageArgs,
            nextPageExample = nextPageArgs is null
                ? null
                : JsonSerializer.Serialize(nextPageArgs, JsonOptions),
        };
    }

    private static string BuildTranscriptText(IReadOnlyList<RawSessionLogMessage> messages, bool excludeHeartbeat = false)
    {
        if (messages.Count == 0)
            return string.Empty;

        var filtered = excludeHeartbeat
            ? messages.Where(m => !IsHeartbeatMessage(m)).ToList()
            : messages.ToList();

        if (filtered.Count == 0)
            return string.Empty;

        var lines = filtered
            .OrderBy(m => m.CreatedAt)
            .Select(m => $"[{m.Role} @ {m.CreatedAt}] {m.Content}");

        return string.Join("\n\n", lines);
    }

    private static bool IsHeartbeatMessage(RawSessionLogMessage m)
    {
        // Filter by role
        if (string.Equals(m.Role, "heartbeat", StringComparison.OrdinalIgnoreCase))
            return true;

        // Filter pudding-message JSON heartbeats
        var content = m.Content ?? string.Empty;
        if (content.Contains("\"type\":\"heartbeat\"", StringComparison.OrdinalIgnoreCase))
            return true;

        // Filter system heartbeat markers
        if (content.Contains("── 系统心跳 ──", StringComparison.Ordinal))
            return true;

        return false;
    }

    private static bool IsTrue(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop))
            return false;

        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => string.Equals(prop.GetString(), "true", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static bool IsDiagnostic(JsonElement root)
        => IsTrue(root, "diagnostic")
           || string.Equals(root.GetString("mode", string.Empty), "debug", StringComparison.OrdinalIgnoreCase)
           || string.Equals(root.GetString("mode", string.Empty), "diagnostic", StringComparison.OrdinalIgnoreCase);
}

public sealed record QuerySessionLogsArgs
{
    [ToolParam("Operation: messages / list_days / list_sessions / grep / grep_raw_events / read_raw_events.")]
    public string? Action { get; init; }

    [ToolParam("Workspace id. Runtime injects the active workspace when omitted.")]
    public string? WorkspaceId { get; init; }

    [ToolParam("Agent instance id. Runtime injects the active agent when omitted.")]
    public string? AgentInstanceId { get; init; }

    [ToolParam("Day in yyyy-MM-dd format.")]
    public string? Day { get; init; }

    [ToolParam("Start day in yyyy-MM-dd format.")]
    public string? FromDay { get; init; }

    [ToolParam("End day in yyyy-MM-dd format.")]
    public string? ToDay { get; init; }

    [ToolParam("Session id. Required for messages and read_raw_events.")]
    public string? SessionId { get; init; }

    [ToolParam("Text or regex query.")]
    public string? Query { get; init; }

    [ToolParam("true to use .NET regular expressions.")]
    public string? Regex { get; init; }

    [ToolParam("true to enable raw/debug actions.")]
    public string? Diagnostic { get; init; }

    [ToolParam("true to include raw event frames in grep; requires diagnostic=true.")]
    public string? IncludeEvents { get; init; }

    [ToolParam("Raw event pagination cursor.")]
    public long? AfterSequence { get; init; }

    [ToolParam("Messages transcript page, starting from 1.")]
    public int? Page { get; init; }

    [ToolParam("Transcript window size, default 16384, max 32768.")]
    public int? WindowSize { get; init; }

    [ToolParam("Maximum rows to return.")]
    public int? Limit { get; init; }

    [ToolParam("true to use Lucene full-text search for grep when available.")]
    public string? Fts { get; init; }

    [ToolParam("Mode hint: debug or diagnostic enables raw diagnostic actions.")]
    public string? Mode { get; init; }

    [ToolParam("Message id for by_message_id.")]
    public long? MessageId { get; init; }

    [ToolParam("Exclude heartbeat/system messages from transcript. Default: false.")]
    public string? ExcludeHeartbeat { get; init; }
}

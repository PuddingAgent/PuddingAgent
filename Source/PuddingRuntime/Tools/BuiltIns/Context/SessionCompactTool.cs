using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 会话上下文手动压缩工具 —— 让 Agent 自主控制压缩时机。
/// 推荐闭环：先用 agent_diagnostics（compaction_stats / token_breakdown）诊断上下文水位，
/// 再用本工具触发压缩。压缩将较早消息归并为摘要、保留最近消息，
/// 并在压缩前执行预冲洗事实提取（与自动压缩路径同源）。
/// 仅作用于当前工具调用所属会话。
/// </summary>
[Tool(
    id: "compact_session",
    name: "Compact session context",
    description: "手动触发当前会话的上下文压缩（compaction）：旧消息被汇总为紧凑摘要，近期消息保持完整。先用 agent_diagnostics（compaction_stats 或 token_breakdown）检查上下文占用。在占用率高时、开始大型新任务前或主题切换后使用。返回压缩前后 token、被压缩消息数与摘要预览。Manually trigger context compaction for the current session",
    category: ToolCategory.General,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.None)]
public sealed class SessionCompactTool : PuddingToolBase<SessionCompactArgs>
{
    private const int FlushMessageLimit = 80;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IContextCompactionService _compactionService;
    private readonly ILogger<SessionCompactTool> _logger;
    private readonly IPreCompactionFlushService? _flushService;
    private readonly ICompactionChatMessageStore? _messageStore;
    private readonly ISessionCompactionEventEmitter? _eventEmitter;

    public SessionCompactTool(
        IContextCompactionService compactionService,
        ILogger<SessionCompactTool> logger,
        IPreCompactionFlushService? flushService = null,
        ICompactionChatMessageStore? messageStore = null,
        ISessionCompactionEventEmitter? eventEmitter = null)
    {
        _compactionService = compactionService;
        _logger = logger;
        _flushService = flushService;
        _messageStore = messageStore;
        _eventEmitter = eventEmitter;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        SessionCompactArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        var sessionId = context.SessionId;
        var workspaceId = context.WorkspaceId;

        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(workspaceId))
        {
            return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
            {
                error = "当前执行上下文缺少会话/工作区信息，无法执行压缩。",
            }, JsonOptions));
        }

        var compactionId = Guid.NewGuid().ToString("N");
        var reason = string.IsNullOrWhiteSpace(args.Reason)
            ? "agent_manual_compaction"
            : $"agent_manual_compaction: {args.Reason}";

        await EmitAsync(sessionId, workspaceId, SseEventTypes.ContextCompactionStarted, new
        {
            compactionId,
            sessionId,
            mode = "Manual",
            level = "Full",
            reason,
            agentId = context.AgentInstanceId,
        }, ct);

        try
        {
            // ── 压缩前冲洗：提取关键事实，防止信息丢失（失败不阻塞压缩）──
            IReadOnlyList<string> preCompactionFacts = [];
            if (_flushService is not null && _messageStore is not null)
            {
                try
                {
                    var rows = await _messageStore.GetRecentForSessionAsync(sessionId, FlushMessageLimit, ct);
                    var messages = rows
                        .Where(r => !string.IsNullOrWhiteSpace(r.Content)
                            && (r.Role.Equals("user", StringComparison.OrdinalIgnoreCase)
                                || r.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase)))
                        .Select((r, i) => new ContextCompactionMessage(
                            MessageId: r.Id.ToString(),
                            Sequence: i,
                            Role: r.Role.ToLowerInvariant(),
                            Content: r.Content))
                        .ToList();

                    var flushResult = await _flushService.FlushAsync(
                        new PreCompactionFlushRequest(workspaceId, sessionId, context.AgentInstanceId, messages, reason)
                        {
                            AgentTemplateId = context.AgentTemplateId,
                            AgentWorkSummary = args.WorkSummary,
                        },
                        ct);

                    if (flushResult.Success)
                        preCompactionFacts = flushResult.Facts ?? [];

                    _logger.LogInformation(
                        "[SessionCompact] pre-flush session={Session} facts={Count}",
                        sessionId, preCompactionFacts.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "[SessionCompact] pre-compaction flush failed session={Session}; continuing without facts",
                        sessionId);
                }
            }

            var result = await _compactionService.CompactAsync(
                new ContextCompactionRequest(
                    workspaceId,
                    sessionId,
                    context.AgentInstanceId,
                    ContextCompactionMode.Manual,
                    ContextCompactionLevel.Full,
                    reason,
                    AgentWorkSummary: args.WorkSummary,
                    CompactionId: compactionId,
                    AgentTemplateId: context.AgentTemplateId,
                    PreCompactionFacts: preCompactionFacts),
                ct);

            await EmitAsync(sessionId, workspaceId, SseEventTypes.ContextCompactionCompleted, new
            {
                compactionId,
                sessionId,
                mode = "Manual",
                beforeTokens = result.BeforeTokens,
                afterTokens = result.AfterTokens,
                compactedMessageCount = result.CompactedMessageCount,
            }, ct);

            _logger.LogInformation(
                "[SessionCompact] completed session={Session} before={Before} after={After} messages={Count} skipped={Skipped}",
                sessionId,
                result.BeforeTokens,
                result.AfterTokens,
                result.CompactedMessageCount,
                result.SkippedDueToTokenIncrease);

            var status = result.SkippedDueToTokenIncrease
                ? "skipped"
                : result.CompactedMessageCount == 0 ? "nothing_to_compact" : "compacted";

            return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
            {
                status,
                session_id = sessionId,
                compaction_id = compactionId,
                before_tokens = result.BeforeTokens,
                after_tokens = result.AfterTokens,
                saved_tokens = result.BeforeTokens - result.AfterTokens,
                compacted_message_count = result.CompactedMessageCount,
                pre_compaction_facts = preCompactionFacts.Count,
                skipped_due_to_token_increase = result.SkippedDueToTokenIncrease,
                summary_preview = result.SummaryPreview,
            }, JsonOptions));
        }
        catch (Exception ex)
        {
            await EmitAsync(sessionId, workspaceId, SseEventTypes.ContextCompactionFailed, new
            {
                compactionId,
                sessionId,
                error = ex.Message,
            }, ct);

            _logger.LogError(ex, "[SessionCompact] failed session={Session}", sessionId);

            return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
            {
                status = "failed",
                session_id = sessionId,
                compaction_id = compactionId,
                error = ex.Message,
            }, JsonOptions));
        }
    }

    private async Task EmitAsync(
        string sessionId, string workspaceId, string eventType, object payload, CancellationToken ct)
    {
        if (_eventEmitter is null)
            return;

        try
        {
            await _eventEmitter.EmitAsync(sessionId, workspaceId, eventType, payload, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[SessionCompact] SSE emit failed event={Event}", eventType);
        }
    }
}

/// <summary>compact_session 工具参数。</summary>
public sealed record SessionCompactArgs
{
    [ToolParam("Why compaction is triggered now; recorded in the compaction event and passed to summary generation. Recommended.")]
    public string? Reason { get; init; }

    [ToolParam("Optional summary of what this agent has accomplished so far; injected into the compaction summary to preserve key outcomes.")]
    public string? WorkSummary { get; init; }
}

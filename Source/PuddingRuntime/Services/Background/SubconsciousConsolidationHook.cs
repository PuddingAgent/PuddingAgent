using System.Threading.Channels;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntime.Services.Background;

/// <summary>
/// 潜意识整合触发 Hook：在主对话完成后按模板策略投递后台整合任务。
/// </summary>
public sealed class SubconsciousConsolidationHook : IAgentLoopHook
{
    private readonly Channel<ConsolidationJob> _channel;
    private readonly ILogger<SubconsciousConsolidationHook> _logger;

    public SubconsciousConsolidationHook(
        Channel<ConsolidationJob> channel,
        ILogger<SubconsciousConsolidationHook> logger)
    {
        _channel = channel;
        _logger = logger;
    }

    /// <summary>
    /// 任务成功结束后触发：读取模板 MemorySearchMode，按策略入队。
    /// </summary>
    public Task OnCompletedAsync(
        AgentLoopContext context,
        string finalMessage,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(context.AgentTemplateId))
        {
            _logger.LogDebug(
                "[SubconsciousHook] Skip enqueue because AgentTemplateId is empty session={SessionId}",
                context.SessionId);
            return Task.CompletedTask;
        }

        var job = new ConsolidationJob
        {
            SessionId = context.SessionId,
            WorkspaceId = context.WorkspaceId,
            AgentId = context.AgentInstanceId,
            AgentTemplateId = context.AgentTemplateId,
            LastUserMessage = context.UserMessage,
            LastAssistantReply = finalMessage,
        };

        if (_channel.Writer.TryWrite(job))
        {
            _logger.LogInformation(
                "[SubconsciousHook] Enqueued consolidation job session={SessionId} workspace={WorkspaceId}",
                context.SessionId,
                context.WorkspaceId);
            return Task.CompletedTask;
        }

        _logger.LogWarning(
            "[SubconsciousHook] Failed to enqueue consolidation job session={SessionId} workspace={WorkspaceId}",
            context.SessionId,
            context.WorkspaceId);

        return Task.CompletedTask;
    }
}

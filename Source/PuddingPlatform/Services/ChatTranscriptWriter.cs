using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// 聊天转录物化写入器。
/// <para>
/// ADR-031：`session_event_log` 是执行事实源，`ChatMessages` 是面向 UI 历史、分页与检索的
/// 聊天转录物化视图。该服务只负责物化视图写入，不改变事件日志权威性。
/// </para>
/// </summary>
public sealed class ChatTranscriptWriter(
    IServiceScopeFactory scopeFactory,
    ILogger<ChatTranscriptWriter> logger,
    MessageTopicService? messageTopicService = null,
    AgentConversationLogService? agentConversationLogService = null)
{
    /// <summary>
    /// 幂等写入一条聊天转录消息。返回生成的 messageId，重复消息返回 null。
    /// </summary>
    public async Task<long?> PersistMessageAsync(
        string sessionId,
        string role,
        string? content,
        long createdAt,
        string? thinkingJson = null,
        string? usageJson = null,
        CancellationToken ct = default)
        => await PersistMessageAsync(
            sessionId,
            role,
            content,
            createdAt,
            thinkingJson,
            usageJson,
            workspaceId: null,
            agentInstanceId: null,
            agentTemplateId: null,
            ct);

    /// <summary>
    /// 幂等写入一条携带 Agent 身份的聊天转录消息。返回生成的 messageId，重复消息返回 null。
    /// </summary>
    public async Task<long?> PersistMessageAsync(
        string sessionId,
        string role,
        string? content,
        long createdAt,
        string? thinkingJson = null,
        string? usageJson = null,
        string? workspaceId = null,
        string? agentInstanceId = null,
        string? agentTemplateId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(content))
            return null;

        try
        {
            long? messageId = null;

            if (agentConversationLogService is not null)
            {
                messageId = await agentConversationLogService.PersistMessageAsync(
                    new AgentConversationLogWriteRequest(
                        workspaceId ?? string.Empty,
                        agentInstanceId ?? string.Empty,
                        agentTemplateId ?? string.Empty,
                        sessionId,
                        role,
                        content,
                        createdAt,
                        thinkingJson,
                        usageJson),
                    ct);

                // 话题检测：仅对用户消息且未持久化通过 agentConversationLogService 的情况
                // 如果 agentConversationLogService 返回了 ID，这里统一检测
            }

            if (messageId is null)
            {

                using var scope = scopeFactory.CreateScope();
                var transcriptDb = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                var windowStart = createdAt - 2_000;
                var windowEnd = createdAt + 2_000;

                var exists = await transcriptDb.ChatMessages
                    .AsNoTracking()
                    .AnyAsync(m => m.SessionId == sessionId
                        && m.Role == role
                        && m.Content == content
                        && m.CreatedAt >= windowStart
                        && m.CreatedAt <= windowEnd, ct);

                if (exists)
                {
                    logger.LogDebug(
                        "[Chat:Transcript] Skip duplicate transcript session={Session} role={Role} createdAt={CreatedAt}",
                        sessionId, role, createdAt);
                    return null;
                }

                var entity = new ChatMessageEntity
                {
                    SessionId = sessionId,
                    Role = role,
                    Content = content,
                    ThinkingJson = thinkingJson,
                    UsageJson = usageJson,
                    CreatedAt = createdAt,
                };
                transcriptDb.ChatMessages.Add(entity);
                await transcriptDb.SaveChangesAsync(ct);
                messageId = entity.Id;

                logger.LogInformation(
                    "[Chat:Transcript] Persisted transcript session={Session} role={Role} contentLen={ContentLen}",
                    sessionId, role, content.Length);
            }

            return messageId;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[Chat:Transcript] Failed to persist transcript session={Session} role={Role}",
                sessionId, role);
            return null;
        }
    }
}

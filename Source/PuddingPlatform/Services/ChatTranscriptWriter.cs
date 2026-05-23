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
    ILogger<ChatTranscriptWriter> logger)
{
    /// <summary>
    /// 幂等写入一条聊天转录消息。
    /// <para>
    /// 当前表结构尚无 MessageId/TurnId，短期以 SessionId + Role + Content + CreatedAt 窗口降低
    /// 后台重试导致的重复写入风险；中期应通过迁移新增 ExternalMessageId/TurnId 提升幂等精度。
    /// </para>
    /// </summary>
    public async Task PersistMessageAsync(
        string sessionId,
        string role,
        string? content,
        long createdAt,
        string? thinkingJson = null,
        string? usageJson = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(content))
            return;

        try
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
                return;
            }

            transcriptDb.ChatMessages.Add(new ChatMessageEntity
            {
                SessionId = sessionId,
                Role = role,
                Content = content,
                ThinkingJson = thinkingJson,
                UsageJson = usageJson,
                CreatedAt = createdAt,
            });
            await transcriptDb.SaveChangesAsync(ct);

            logger.LogInformation(
                "[Chat:Transcript] Persisted transcript session={Session} role={Role} contentLen={ContentLen}",
                sessionId, role, content.Length);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[Chat:Transcript] Failed to persist transcript session={Session} role={Role}",
                sessionId, role);
        }
    }
}

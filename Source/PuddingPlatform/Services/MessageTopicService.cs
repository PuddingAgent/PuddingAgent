using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// 消息话题索引服务：管理 #话题 的存储和查询。
/// </summary>
public sealed class MessageTopicService
{
    private readonly IDbContextFactory<PlatformDbContext> _dbFactory;

    public MessageTopicService(IDbContextFactory<PlatformDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// 从用户消息内容中检测话题。若首行以 # 开头，返回话题标题（去掉 # 和首尾空白）。
    /// 否则返回 null。
    /// </summary>
    public static string? DetectTopic(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var trimmed = content.TrimStart();
        if (!trimmed.StartsWith('#'))
            return null;

        // 提取第一行
        var newlineIdx = trimmed.IndexOfAny(['\n', '\r']);
        var firstLine = newlineIdx > 0 ? trimmed[..newlineIdx] : trimmed;

        // 去掉 # 前缀
        var topic = firstLine[1..].Trim();
        return topic.Length > 0 ? topic : null;
    }

    /// <summary>存储话题索引。若 messageId 为 0 则通过 session+content 反查。</summary>
    public async Task SaveTopicAsync(
        long messageId,
        string topicTitle,
        string sessionId,
        string workspaceId,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // 若 messageId 未指定，按 session+content 反查（后台任务场景）
        if (messageId <= 0)
        {
            var msg = await db.ChatMessages
                .AsNoTracking()
                .Where(m => m.SessionId == sessionId && m.Role == "user")
                .OrderByDescending(m => m.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (msg is null)
                return;
            messageId = msg.Id;
        }

        // 幂等：如果已存在则跳过
        var exists = await db.MessageTopics.AnyAsync(t => t.MessageId == messageId, ct);
        if (exists)
            return;

        db.MessageTopics.Add(new MessageTopicEntity
        {
            MessageId = messageId,
            TopicTitle = topicTitle,
            SessionId = sessionId,
            WorkspaceId = workspaceId,
            CreatedAt = DateTime.UtcNow.ToString("O"),
        });

        await db.SaveChangesAsync(ct);
    }

    /// <summary>按关键词搜索话题。</summary>
    public async Task<IReadOnlyList<MessageTopicEntity>> SearchTopicsAsync(
        string workspaceId,
        string query,
        int limit = 20,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        return await db.MessageTopics
            .AsNoTracking()
            .Where(t => t.WorkspaceId == workspaceId
                && t.TopicTitle.Contains(query))
            .OrderByDescending(t => t.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);
    }
}

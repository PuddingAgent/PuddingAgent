using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Runtime;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;

namespace PuddingRuntime.Services;

public sealed class ContextCompactionService : IContextCompactionService
{
    private const int RecentMessagesToKeep = 6;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDbContextFactory<MemoryDbContext> _dbFactory;
    private readonly IContextCompactionSummaryGenerator _summaryGenerator;
    private readonly ILogger<ContextCompactionService> _logger;

    public ContextCompactionService(
        IDbContextFactory<MemoryDbContext> dbFactory,
        IContextCompactionSummaryGenerator summaryGenerator,
        ILogger<ContextCompactionService> logger)
    {
        _dbFactory = dbFactory;
        _summaryGenerator = summaryGenerator;
        _logger = logger;
    }

    public async Task<ContextHealthSnapshot> GetHealthAsync(string sessionId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var messages = await LoadActiveMessagesAsync(db, sessionId, ct);
        var usedTokens = EstimateMessages(messages);
        return new ContextHealthEvaluator().Evaluate(
            sessionId,
            usedTokens,
            contextWindowTokens: 8_192,
            maxOutputTokens: 2_048);
    }

    public async Task<ContextCompactionResult> CompactAsync(
        ContextCompactionRequest request,
        CancellationToken ct = default)
    {
        if (request.Level != ContextCompactionLevel.Full)
            throw new NotSupportedException($"Context compaction level '{request.Level}' is not implemented yet.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var activeMessages = await LoadActiveMessagesAsync(db, request.SessionId, ct);
        var candidates = activeMessages
            .Where(m => string.Equals(m.ContentType, "text", StringComparison.OrdinalIgnoreCase))
            .Where(m => !string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.Sequence)
            .ToList();

        var messagesToCompact = candidates
            .Take(Math.Max(0, candidates.Count - RecentMessagesToKeep))
            .ToList();

        if (messagesToCompact.Count == 0)
        {
            return new ContextCompactionResult(
                request.SessionId,
                SummaryMessageId: string.Empty,
                request.Mode,
                request.Level,
                BeforeTokens: EstimateMessages(activeMessages),
                AfterTokens: EstimateMessages(activeMessages),
                CompactedMessageCount: 0,
                SummaryPreview: string.Empty);
        }

        var summary = await _summaryGenerator.GenerateSummaryAsync(
            new ContextCompactionSummaryRequest(
                request.WorkspaceId,
                request.SessionId,
                request.AgentId,
                messagesToCompact
                    .Select(m => new ContextCompactionMessage(
                        m.MessageId,
                        m.Sequence,
                        m.Role,
                        m.Content ?? string.Empty))
                    .ToList(),
                request.Reason),
            ct);

        if (string.IsNullOrWhiteSpace(summary))
            throw new InvalidOperationException("Context compaction summary generator returned an empty summary.");

        var beforeTokens = EstimateMessages(activeMessages);
        var summaryMessage = new MessageEntity
        {
            MessageId = Guid.NewGuid().ToString("N"),
            SessionId = request.SessionId,
            Sequence = activeMessages.Max(m => m.Sequence) + 1,
            Role = "system",
            ContentType = "compact_summary",
            Content = summary,
            Source = "context_compaction",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Metadata = JsonSerializer.Serialize(new
            {
                mode = request.Mode.ToString(),
                level = request.Level.ToString(),
                reason = request.Reason,
                compactedMessageCount = messagesToCompact.Count,
                beforeTokens,
            }, JsonOptions),
        };

        db.Messages.Add(summaryMessage);
        foreach (var message in messagesToCompact)
            message.CompactedBy = summaryMessage.MessageId;

        await db.SaveChangesAsync(ct);

        var afterMessages = await LoadActiveMessagesAsync(db, request.SessionId, ct);
        var afterTokens = EstimateMessages(afterMessages);

        _logger.LogInformation(
            "[ContextCompaction] Full compact completed session={SessionId} compacted={Count} before={BeforeTokens} after={AfterTokens}",
            request.SessionId, messagesToCompact.Count, beforeTokens, afterTokens);

        return new ContextCompactionResult(
            request.SessionId,
            summaryMessage.MessageId,
            request.Mode,
            request.Level,
            beforeTokens,
            afterTokens,
            messagesToCompact.Count,
            BuildPreview(summary));
    }

    private static Task<List<MessageEntity>> LoadActiveMessagesAsync(
        MemoryDbContext db,
        string sessionId,
        CancellationToken ct) =>
        db.Messages
            .Where(m => m.SessionId == sessionId && m.CompactedBy == null)
            .OrderBy(m => m.Sequence)
            .ToListAsync(ct);

    private static int EstimateMessages(IReadOnlyList<MessageEntity> messages) =>
        messages.Sum(m => EstimateTokens(m.Content ?? string.Empty));

    private static int EstimateTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;
        var chineseChars = text.Count(c => c >= 0x4E00 && c <= 0x9FFF);
        var otherChars = text.Length - chineseChars;
        return Math.Max(1, (int)Math.Ceiling(chineseChars / 1.5 + otherChars / 4.0));
    }

    private static string BuildPreview(string text)
    {
        var normalized = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return normalized.Length <= 160 ? normalized : normalized[..160];
    }
}

public sealed class ExtractiveContextCompactionSummaryGenerator : IContextCompactionSummaryGenerator
{
    public Task<string> GenerateSummaryAsync(
        ContextCompactionSummaryRequest request,
        CancellationToken ct = default)
    {
        var userMessages = request.Messages.Count(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));
        var agentMessages = request.Messages.Count(m => string.Equals(m.Role, "agent", StringComparison.OrdinalIgnoreCase)
            || string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase));
        var snippets = request.Messages
            .Where(m => !string.IsNullOrWhiteSpace(m.Content))
            .TakeLast(8)
            .Select(m => $"- [{m.Role} #{m.Sequence}] {Trim(m.Content, 240)}");

        var summary = $"""
<compact_summary>
## 用户目标
根据会话早期内容继续协助用户完成当前任务。

## 已完成事项
已压缩 {request.Messages.Count} 条早期消息，其中用户消息 {userMessages} 条，Agent 消息 {agentMessages} 条。

## 关键决策
第一阶段使用抽取式摘要保留最近的早期上下文片段。

## 涉及文件和代码位置
未从压缩内容中检测结构化文件列表。

## 工具调用与重要输出
未从压缩内容中检测结构化工具输出。

## 错误、阻塞与修复
未从压缩内容中检测结构化错误。

## 当前工作状态
后续上下文应结合最近未压缩消息继续执行。

## 明确的下一步
继续根据用户最新请求推进。

## 保留的用户偏好和约束
{string.Join(Environment.NewLine, snippets)}
</compact_summary>
""";
        return Task.FromResult(summary);
    }

    private static string Trim(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars] + "...";
}

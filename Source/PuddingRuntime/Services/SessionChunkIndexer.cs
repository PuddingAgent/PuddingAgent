using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;
using PuddingMemoryEngine.Infrastructure.Text;

namespace PuddingRuntime.Services;

/// <summary>
/// 会话块索引服务——消息落库后异步切块 + embedding + 写入 SessionChunkVectors（WP-L2b 写入侧）。
/// 与 EmbeddingGenerationHook 同目录同风格：fire-and-forget 调用方负责触发，本服务自身幂等。
/// 幂等由 (MessageId, ChunkSeq) 唯一索引 UX_SessionChunkVectors_Message_Seq 兜底：
/// 同一消息二次索引时捕获 DbUpdateException 视为"已索引过"，记 Info 日志后正常返回。
/// </summary>
public sealed class SessionChunkIndexer : ISessionChunkIndexer
{
    private readonly IEmbeddingService _embeddingService;
    private readonly IDbContextFactory<MemoryLibraryDbContext> _dbFactory;
    private readonly ILogger<SessionChunkIndexer> _logger;

    public SessionChunkIndexer(
        IEmbeddingService embeddingService,
        IDbContextFactory<MemoryLibraryDbContext> dbFactory,
        ILogger<SessionChunkIndexer> logger)
    {
        _embeddingService = embeddingService;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <summary>
    /// 为一条已落库消息切块、批量生成向量并入库。整体 try/catch + LogError 健康自证；
    /// DbUpdateException 视为幂等命中，不向调用方抛异常。
    /// </summary>
    public async Task IndexMessageAsync(
        string workspaceId,
        string sessionId,
        string messageId,
        string role,
        string? content,
        CancellationToken ct = default)
    {
        try
        {
            // 过滤：仅索引 user / assistant 角色。
            if (role is not ("user" or "assistant"))
                return;

            // 过滤：空白或过短内容（<20 字符）不具检索价值。
            if (string.IsNullOrWhiteSpace(content) || content.Length < 20)
                return;

            var chunks = TextChunker.Chunk(content);
            if (chunks.Count == 0)
                return;

            _logger.LogInformation(
                "[SessionChunkIndexer] IndexMessage start messageId={MessageId} role={Role} chunkCount={ChunkCount}",
                messageId, role, chunks.Count);

            var embeddings = await _embeddingService.GenerateEmbeddingsAsync(chunks, ct);
            if (embeddings.Length != chunks.Count)
            {
                _logger.LogWarning(
                    "[SessionChunkIndexer] Embedding count mismatch messageId={MessageId} expected={Expected} actual={Actual}",
                    messageId, chunks.Count, embeddings.Length);
                return;
            }

            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var vectors = new List<SessionChunkVectorEntity>(chunks.Count);
            for (var seq = 0; seq < chunks.Count; seq++)
            {
                var embedding = embeddings[seq];
                if (embedding.Length == 0)
                    continue;

                vectors.Add(new SessionChunkVectorEntity
                {
                    WorkspaceId = workspaceId,
                    SessionId = sessionId,
                    MessageId = messageId,
                    ChunkSeq = seq,
                    Role = role,
                    SourceText = chunks[seq],
                    Embedding = VectorSimilarity.FloatsToBytes(embedding),
                    CreatedAt = now,
                });
            }

            if (vectors.Count == 0)
                return;

            db.SessionChunkVectors.AddRange(vectors);
            await db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "[SessionChunkIndexer] Indexed messageId={MessageId} chunks={ChunkCount}",
                messageId, vectors.Count);
        }
        catch (DbUpdateException ex)
        {
            // 幂等兜底：同一消息二次索引时 (MessageId, ChunkSeq) 唯一索引冲突，视为已索引过。
            _logger.LogInformation(ex,
                "[SessionChunkIndexer] Message already indexed (idempotent) messageId={MessageId}",
                messageId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[SessionChunkIndexer] IndexMessage failed messageId={MessageId} role={Role}",
                messageId, role);
        }
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingMemoryEngine.Data;
using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntime.Services;

/// <summary>
/// 嵌入向量生成 Hook——在 Agent Loop 每轮完成后异步为 Chapter 生成 Embedding。
/// 查询 workspace 下 Embedding 为空且 Content 非空的 Chapter，调用 Embedding API 回写。
/// Fire-and-forget 模式，不阻塞主执行链。
/// </summary>
public sealed class EmbeddingGenerationHook : IAgentLoopHook
{
    private readonly IEmbeddingService _embeddingService;
    private readonly IMemoryLibrary _memoryLibrary;
    private readonly IDbContextFactory<MemoryLibraryDbContext> _dbFactory;
    private readonly ILogger<EmbeddingGenerationHook> _logger;

    public EmbeddingGenerationHook(
        IEmbeddingService embeddingService,
        IMemoryLibrary memoryLibrary,
        IDbContextFactory<MemoryLibraryDbContext> dbFactory,
        ILogger<EmbeddingGenerationHook> logger)
    {
        _embeddingService = embeddingService;
        _memoryLibrary = memoryLibrary;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <summary>
    /// 每轮完成后，异步为 workspace 下尚未生成 Embedding 的 Chapter 生成向量。
    /// 每次最多处理 5 个 Chapter，避免一次性消耗过多 API 配额。
    /// </summary>
    public Task OnRoundCompleteAsync(
        AgentLoopContext context,
        int round,
        AgentLoopResponse response,
        CancellationToken ct = default)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct);

                // 查询 workspace 下 Embedding 为空且 Content 非空的 Chapter，取最早创建的 5 个
                var chapters = await db.Chapters
                    .AsNoTracking()
                    .Where(c => c.Embedding == null
                                && c.Content != null
                                && c.Content != ""
                                && c.BookId != null)
                    .OrderBy(c => c.CreatedAt)
                    .Take(5)
                    .Select(c => new { c.ChapterId, c.Content })
                    .ToListAsync(ct);

                foreach (var ch in chapters)
                {
                    try
                    {
                        var embedding = await _embeddingService.GenerateEmbeddingAsync(ch.Content!, ct);
                        if (embedding.Length == 0) continue;

                        await _memoryLibrary.UpdateChapterEmbeddingAsync(
                            ch.ChapterId,
                            VectorSimilarity.FloatsToBytes(embedding),
                            ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "[EmbeddingHook] Chapter={ChapterId} embedding generation failed",
                            ch.ChapterId);
                    }
                }

                if (chapters.Count > 0)
                {
                    _logger.LogInformation(
                        "[EmbeddingHook] Generated embeddings for {Count} chapters (round {Round})",
                        chapters.Count, round);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[EmbeddingHook] Async embedding batch failed");
            }
        }, ct);

        return Task.CompletedTask;
    }
}

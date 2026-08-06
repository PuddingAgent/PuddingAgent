using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingMemoryEngine.Data;
using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntime.Services;

/// <summary>
/// 嵌入向量生成 Hook——在 Agent Loop 生命周期节点异步为 Chapter 生成 Embedding。
/// 查询 workspace 下 Embedding 为空且 Content 非空的 Chapter，调用 Embedding API 回写。
/// Fire-and-forget 模式，不阻塞主执行链；通过 OnRoundCompleteAsync / OnLoopCompleteAsync
/// 及补充触发点覆盖 Buffered 与 Streaming 两条执行路径，确保每次 Agent Loop 结束至少触发一次。
/// </summary>
public sealed class EmbeddingGenerationHook : IAgentLoopHook
{
    private readonly IEmbeddingService _embeddingService;
    private readonly IMemoryLibrary _memoryLibrary;
    private readonly IDbContextFactory<MemoryLibraryDbContext> _dbFactory;
    private readonly ILogger<EmbeddingGenerationHook> _logger;

    /// <summary>并发守卫：同一时刻只允许一个 EmbedPendingChaptersAsync 实例在跑。</summary>
    private int _running;

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
        _ = Task.Run(() => EmbedPendingChaptersAsync(ct), ct);
        return Task.CompletedTask;
    }

    /// <summary>Loop 结束（任意原因）后触发，覆盖 Buffered/Streaming 两条路径的收口节点。</summary>
    public Task OnLoopCompleteAsync(
        AgentLoopContext context,
        string finalMessage,
        AgentLoopStopReason stopReason,
        CancellationToken ct = default)
    {
        _ = Task.Run(() => EmbedPendingChaptersAsync(ct), ct);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 取消路径的补充触发点：Streaming 路径在取消时只 fire OnCancelledAsync（不 fire OnLoopCompleteAsync），
    /// Buffered 路径在取消检查点/OperationCanceledException 时也先 fire 本方法。
    /// </summary>
    public Task OnCancelledAsync(
        AgentLoopContext context,
        CancellationToken ct = default)
    {
        _ = Task.Run(() => EmbedPendingChaptersAsync(ct), ct);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 失败路径的补充触发点：Buffered 路径遇未捕获异常会提前 return，走不到末尾的 OnLoopCompleteAsync，
    /// 此时由 OnFailedAsync（连同 OnLoopErrorAsync）兜底触发。
    /// </summary>
    public Task OnFailedAsync(
        AgentLoopContext context,
        string reason,
        Exception? exception,
        CancellationToken ct = default)
    {
        _ = Task.Run(() => EmbedPendingChaptersAsync(ct), ct);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 查询 workspace 下 Embedding 为空且 Content 非空的 Chapter（取最早创建的 5 个），
    /// 逐个调用 Embedding API 生成向量并回写。带并发守卫与整体异常捕获，失败以 Error 级别可见。
    /// </summary>
    private async Task EmbedPendingChaptersAsync(CancellationToken ct)
    {
        // 并发守卫：已有实例在跑则本次直接返回，避免多触发点并发重复执行。
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return;

        try
        {
            _logger.LogInformation("EmbedPendingChapters: start");

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

            _logger.LogInformation("EmbedPendingChapters: found {Count} pending chapters", chapters.Count);

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
                    "[EmbeddingHook] Generated embeddings for {Count} chapters",
                    chapters.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EmbedPendingChapters failed");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}

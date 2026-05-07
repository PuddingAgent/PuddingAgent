using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntime.Services;

/// <summary>
/// 嵌入向量生成 Hook——在 Agent Loop 每轮完成后异步为 Chapter 生成 Embedding。
/// Phase 4: 标记 TODO，先完成基础设施（接口 + 向量检索），后续补全完整逻辑。
/// </summary>
public sealed class EmbeddingGenerationHook : IAgentLoopHook
{
    private readonly IEmbeddingService _embeddingService;
    private readonly IMemoryLibrary _memoryLibrary;
    private readonly ILogger<EmbeddingGenerationHook> _logger;

    public EmbeddingGenerationHook(
        IEmbeddingService embeddingService,
        IMemoryLibrary memoryLibrary,
        ILogger<EmbeddingGenerationHook> logger)
    {
        _embeddingService = embeddingService;
        _memoryLibrary = memoryLibrary;
        _logger = logger;
    }

    /// <summary>
    /// 每轮完成后，异步为 Journal 中产生的 Chapter 生成 Embedding。
    /// Fire-and-forget 模式，不阻塞主执行链。
    /// </summary>
    public Task OnRoundCompleteAsync(
        AgentLoopContext context,
        int round,
        AgentLoopResponse response,
        CancellationToken ct = default)
    {
        // TODO(Phase 4): 从 ExecutionJournal 中提取本轮新增的 Chapter，
        // 异步调用 _embeddingService.GenerateEmbeddingAsync 生成向量，
        // 再通过 _memoryLibrary.UpdateChapterEmbeddingAsync 回写。

        _ = Task.Run(async () =>
        {
            try
            {
                // TODO(Phase 4): 实现 Chapter 提取逻辑
                // var journal = ...; // 从 DI 获取 ExecutionJournal
                // var newChapters = journal.GetNewChaptersForRound(round);
                // foreach (var ch in newChapters)
                // {
                //     var embedding = await _embeddingService.GenerateEmbeddingAsync(ch.Content, ct);
                //     await _memoryLibrary.UpdateChapterEmbeddingAsync(
                //         ch.ChapterId, VectorSimilarity.FloatsToBytes(embedding), ct);
                // }
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[EmbeddingHook] 异步生成 Embedding 失败");
            }
        }, ct);

        return Task.CompletedTask;
    }
}

namespace PuddingCode.Abstractions;

/// <summary>
/// 会话块索引服务——消息落库后异步切块 + embedding + 写入 SessionChunkVectors（WP-L2b 写入侧）。
/// 调用方以 fire-and-forget 方式触发；实现必须幂等（(MessageId, ChunkSeq) 唯一索引兜底）。
/// 接口放 PuddingCore.Abstractions（仿 IEmbeddingService 惯例），使 PuddingPlatform 的落库点
/// 可注入而不依赖 PuddingRuntime（避免 PuddingRuntime → PuddingPlatform 循环引用）。
/// </summary>
public interface ISessionChunkIndexer
{
    /// <summary>为一条已落库消息切块、生成向量并入库。</summary>
    Task IndexMessageAsync(
        string workspaceId,
        string sessionId,
        string messageId,
        string role,
        string? content,
        CancellationToken ct = default);
}

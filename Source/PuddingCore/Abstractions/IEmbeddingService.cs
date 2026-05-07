namespace PuddingCode.Abstractions;

/// <summary>
/// 嵌入向量生成服务——将文本转换为 float32 向量。
/// 用于记忆图书馆的语义检索（Phase 4 向量检索）。
/// </summary>
public interface IEmbeddingService
{
    /// <summary>生成文本的嵌入向量，返回 float32 数组。</summary>
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default);

    /// <summary>批量生成嵌入向量。</summary>
    Task<float[][]> GenerateEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}

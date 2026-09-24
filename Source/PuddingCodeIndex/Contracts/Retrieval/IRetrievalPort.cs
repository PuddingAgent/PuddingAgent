namespace PuddingCodeIndex.Contracts.Retrieval;

/// <summary>
/// 结构化检索端口（ADR-089 §2；<b>本刀只冻结签名，无实现</b>）。
/// <para>
/// 真实检索 / 融合 / 去重 / 过载判定引擎属 U4-2c、U4-3；本刀交付的是"意图 + 结果 + 过滤面"的合同，
/// 让上层实现有可被契约测试钉住的形状。
/// </para>
/// </summary>
public interface IRetrievalPort
{
    /// <summary>按单次请求检索（单次调用、多路融合、一次交付 —— 见 §2.1 第 1 条）。</summary>
    Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken);
}

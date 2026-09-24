namespace PuddingCodeIndex.Contracts.Retrieval;

/// <summary>
/// 结果预算（ADR-089 §2.2 / §8.7）：用户第六轮裁定原文 ——
/// "如果返回的大量的命中的，需要分页或者输出到一个临时文件，避免将一大堆噪音输出到上下文中"。
/// <para>
/// 双预算（条数 + 字节/token），<b>token 优先</b>：噪音的真实成本是 token，不是条数。
/// </para>
/// <para>
/// ⚠️ 本类的字节/token 换算是 <b>估算</b>（不是实测）：UTF-16 字符按 2 字节、每 token 按 4 字节。
/// 它只用于在**构件层**给出"有界"的机械判据（超预算即拒绝构造，见 <see cref="RetrievalResult"/>），
/// 不作为计量口径；真实 token 化归引擎（U4-2c）与 tokenizer 所有。
/// </para>
/// </summary>
public static class RetrievalBudget
{
    /// <summary>默认页大小（条数）。</summary>
    public const int DefaultPageSize = 50;

    /// <summary>默认字节预算。</summary>
    public const int DefaultMaxBytes = 32768;

    /// <summary>默认 token 预算（token 预算优先）。</summary>
    public const int DefaultMaxTokens = 8192;

    /// <summary>页大小上限：单次返回必须有界，不允许"给我全部"。</summary>
    public const int MaxPageSize = 1000;

    /// <summary>估算用的每 token 字节数（英文近似值；中文更差，故只用于上界判据）。</summary>
    public const int BytesPerToken = 4;

    /// <summary>每条命中的固定开销估算（结构体以外的键、分隔符等）。</summary>
    public const int PerHitOverheadBytes = 32;

    /// <summary>把一组文本载荷折算成估算字节数（UTF-16 ⇒ 2 字节/字符 + 固定开销）。</summary>
    public static int EstimatePayloadBytes(params string?[] parts)
    {
        var chars = 0;
        foreach (var part in parts)
            chars += part?.Length ?? 0;

        return PerHitOverheadBytes + (chars * 2);
    }

    /// <summary>把估算字节数折算成估算 token 数（向上取整）。</summary>
    public static int EstimateTokens(int bytes) =>
        bytes <= 0 ? 0 : ((bytes + BytesPerToken - 1) / BytesPerToken);
}

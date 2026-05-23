using PuddingCode.Models;

namespace PuddingPlatform.Services;

/// <summary>
/// Token 使用统计归一化计算服务（ADR-043）。
/// 统一所有入口的缓存命中/未命中口径和成本计算。
///
/// 归一化规则：
///   cacheHitTokens  = usage.PromptCacheHitTokens ?? 0
///   cacheMissTokens = usage.PromptCacheMissTokens ?? max(usage.PromptTokens - cacheHitTokens, 0)
///   cacheEligibleTokens = cacheHitTokens + cacheMissTokens
///   cacheHitRate = cacheEligibleTokens > 0 ? cacheHitTokens / cacheEligibleTokens : null
///   billableInputTokens = cacheMissTokens
///
/// 成本计算：
///   cost = (cacheHitTokens / 1_000_000 × cacheHitPrice)
///        + (cacheMissTokens / 1_000_000 × inputPrice)
///        + (completionTokens / 1_000_000 × outputPrice)
/// </summary>
public class TokenUsageNormalizer
{
    /// <summary>归一化计算结果</summary>
    public sealed record NormalizedUsage
    {
        public long PromptTokens { get; init; }
        public long CompletionTokens { get; init; }
        public long TotalTokens { get; init; }
        public long CacheHitTokens { get; init; }
        public long CacheMissTokens { get; init; }
        public long CacheEligibleTokens { get; init; }
        public double? CacheHitRate { get; init; }

        /// <summary>计费输入 tokens = cacheMissTokens</summary>
        public long BillableInputTokens { get; init; }

        /// <summary>输入 token 成本（未命中部分）</summary>
        public decimal InputCost { get; init; }

        /// <summary>输出 token 成本</summary>
        public decimal OutputCost { get; init; }

        /// <summary>缓存命中 token 成本</summary>
        public decimal CacheHitCost { get; init; }

        /// <summary>总成本</summary>
        public decimal TotalCost { get; init; }
    }

    /// <summary>归一化计算"实际"未命中 tokens</summary>
    public int ResolveMissTokens(TokenUsageDto usage)
    {
        var promptTokens = usage.PromptTokens ?? 0;
        var cacheHitTokens = usage.PromptCacheHitTokens ?? 0;

        if (usage.PromptCacheMissTokens.HasValue && usage.PromptCacheMissTokens.Value > 0)
            return usage.PromptCacheMissTokens.Value;

        // fallback：从 prompt - hit 派生
        return cacheHitTokens > 0 ? Math.Max(promptTokens - cacheHitTokens, 0) : promptTokens;
    }

    /// <summary>归一化计算成本明细</summary>
    public (decimal inputCost, decimal outputCost, decimal cacheHitCost, decimal totalCost) CalculateCost(
        TokenUsageDto usage,
        decimal inputPrice,
        decimal outputPrice,
        decimal cacheHitPrice)
    {
        var prompt = (decimal)(usage.PromptTokens ?? 0);
        var completion = (decimal)(usage.CompletionTokens ?? 0);
        var cacheHit = (decimal)(usage.PromptCacheHitTokens ?? 0);
        var cacheMiss = (decimal)ResolveMissTokens(usage);

        var inputCost = cacheMiss / 1_000_000m * inputPrice;
        var outputCost = completion / 1_000_000m * outputPrice;
        var cacheHitCost = cacheHit / 1_000_000m * cacheHitPrice;
        var totalCost = inputCost + outputCost + cacheHitCost;

        return (Math.Round(inputCost, 10), Math.Round(outputCost, 10), Math.Round(cacheHitCost, 10), Math.Round(totalCost, 10));
    }

    /// <summary>完整归一化计算</summary>
    public NormalizedUsage Normalize(TokenUsageDto usage, decimal inputPrice = 0, decimal outputPrice = 0, decimal cacheHitPrice = 0)
    {
        var promptTokens = (long)(usage.PromptTokens ?? 0);
        var completionTokens = (long)(usage.CompletionTokens ?? 0);
        var totalTokens = (long)(usage.TotalTokens ?? (promptTokens + completionTokens));
        var cacheHitTokens = (long)(usage.PromptCacheHitTokens ?? 0);
        var cacheMissTokens = (long)ResolveMissTokens(usage);
        var cacheEligibleTokens = cacheHitTokens + cacheMissTokens;
        double? cacheHitRate = cacheEligibleTokens > 0
            ? Math.Round((double)cacheHitTokens / cacheEligibleTokens, 6)
            : null;

        var (inputCost, outputCost, cacheHitCost, totalCost) = CalculateCost(usage, inputPrice, outputPrice, cacheHitPrice);

        return new NormalizedUsage
        {
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens = totalTokens,
            CacheHitTokens = cacheHitTokens,
            CacheMissTokens = cacheMissTokens,
            CacheEligibleTokens = cacheEligibleTokens,
            CacheHitRate = cacheHitRate,
            BillableInputTokens = cacheMissTokens,
            InputCost = inputCost,
            OutputCost = outputCost,
            CacheHitCost = cacheHitCost,
            TotalCost = totalCost,
        };
    }
}

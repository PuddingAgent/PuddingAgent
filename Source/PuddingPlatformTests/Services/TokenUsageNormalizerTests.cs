using PuddingCode.Models;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class TokenUsageNormalizerTests
{
    private readonly TokenUsageNormalizer _normalizer = new();

    // ── ResolveMissTokens ─────────────────────────────────────

    [TestMethod]
    public void ResolveMissTokens_ExplicitMiss_ReturnsExplicit()
    {
        var usage = new TokenUsageDto
        {
            PromptTokens = 1000,
            PromptCacheHitTokens = 800,
            PromptCacheMissTokens = 200,
        };

        var miss = _normalizer.ResolveMissTokens(usage);
        Assert.AreEqual(200, miss);
    }

    [TestMethod]
    public void ResolveMissTokens_NoExplicitMiss_HitPresent_DerivesFromPrompt()
    {
        var usage = new TokenUsageDto
        {
            PromptTokens = 1000,
            PromptCacheHitTokens = 700,
            PromptCacheMissTokens = null,
        };

        var miss = _normalizer.ResolveMissTokens(usage);
        Assert.AreEqual(300, miss);
    }

    [TestMethod]
    public void ResolveMissTokens_NoHitNorMiss_ReturnsFullPrompt()
    {
        var usage = new TokenUsageDto
        {
            PromptTokens = 500,
            PromptCacheHitTokens = null,
            PromptCacheMissTokens = null,
        };

        var miss = _normalizer.ResolveMissTokens(usage);
        Assert.AreEqual(500, miss);
    }

    [TestMethod]
    public void ResolveMissTokens_HitExceedsPrompt_ReturnsZero()
    {
        var usage = new TokenUsageDto
        {
            PromptTokens = 500,
            PromptCacheHitTokens = 600,
            PromptCacheMissTokens = null,
        };

        var miss = _normalizer.ResolveMissTokens(usage);
        Assert.AreEqual(0, miss);
    }

    // ── Normalize ──────────────────────────────────────────────

    [TestMethod]
    public void Normalize_DeepSeekFormat_CalculatesCorrectly()
    {
        var usage = new TokenUsageDto
        {
            PromptTokens = 15000,
            CompletionTokens = 800,
            TotalTokens = 15800,
            PromptCacheHitTokens = 12000,
            PromptCacheMissTokens = 3000,
        };

        var result = _normalizer.Normalize(usage);

        Assert.AreEqual(15000, result.PromptTokens);
        Assert.AreEqual(800, result.CompletionTokens);
        Assert.AreEqual(15800, result.TotalTokens);
        Assert.AreEqual(12000, result.CacheHitTokens);
        Assert.AreEqual(3000, result.CacheMissTokens);
        Assert.AreEqual(15000, result.CacheEligibleTokens);
        Assert.AreEqual(0.8, result.CacheHitRate!.Value, 0.0001);
        Assert.AreEqual(3000, result.BillableInputTokens);
    }

    [TestMethod]
    public void Normalize_NoCache_AllPromptAsMiss()
    {
        var usage = new TokenUsageDto
        {
            PromptTokens = 5000,
            CompletionTokens = 300,
            TotalTokens = 5300,
            PromptCacheHitTokens = null,
            PromptCacheMissTokens = null,
        };

        var result = _normalizer.Normalize(usage);

        Assert.AreEqual(0, result.CacheHitTokens);
        Assert.AreEqual(5000, result.CacheMissTokens);
        Assert.IsNull(result.CacheHitRate);
        Assert.AreEqual(5000, result.BillableInputTokens);
    }

    [TestMethod]
    public void Normalize_OpenAIFormat_HitDerivesMiss()
    {
        // OpenAI 返回 prompt_tokens_details.cached_tokens = hit
        // 我们通过 gateway 转为 PromptCacheHitTokens，miss = prompt - hit
        var usage = new TokenUsageDto
        {
            PromptTokens = 10000,
            CompletionTokens = 500,
            TotalTokens = 10500,
            PromptCacheHitTokens = 8000,
            PromptCacheMissTokens = null, // OpenAI 不返回
        };

        var result = _normalizer.Normalize(usage);

        Assert.AreEqual(8000, result.CacheHitTokens);
        Assert.AreEqual(2000, result.CacheMissTokens);
        Assert.AreEqual(0.8, result.CacheHitRate!.Value, 0.0001);
    }

    [TestMethod]
    public void Normalize_ZeroTokens_HandlesGracefully()
    {
        var usage = new TokenUsageDto();
        var result = _normalizer.Normalize(usage);

        Assert.AreEqual(0, result.PromptTokens);
        Assert.AreEqual(0, result.CompletionTokens);
        Assert.AreEqual(0, result.CacheHitTokens);
        Assert.AreEqual(0, result.CacheMissTokens);
        Assert.AreEqual(0, result.BillableInputTokens);
        Assert.IsNull(result.CacheHitRate);
    }

    // ── CalculateCost ──────────────────────────────────────────

    [TestMethod]
    public void CalculateCost_DeepSeekModel_MatchesExpected()
    {
        // DeepSeek-chat 典型价格：input=$0.27/M, output=$1.10/M, cacheHit=$0.07/M
        var usage = new TokenUsageDto
        {
            PromptTokens = 10000,
            CompletionTokens = 2000,
            PromptCacheHitTokens = 8000,
            PromptCacheMissTokens = 2000,
        };

        var (inputCost, outputCost, cacheHitCost, totalCost) =
            _normalizer.CalculateCost(usage, 0.27m, 1.10m, 0.07m);

        // input: 2000/1M * 0.27 = 0.00054
        Assert.AreEqual(0.00054m, inputCost, 0.00001m);
        // output: 2000/1M * 1.10 = 0.0022
        Assert.AreEqual(0.0022m, outputCost, 0.00001m);
        // cacheHit: 8000/1M * 0.07 = 0.00056
        Assert.AreEqual(0.00056m, cacheHitCost, 0.00001m);
        // total: 0.00054 + 0.0022 + 0.00056 = 0.0033
        Assert.AreEqual(0.0033m, totalCost, 0.00001m);
    }

    [TestMethod]
    public void CalculateCost_NoCacheHitPrice_FallsBackToInputPrice()
    {
        var usage = new TokenUsageDto
        {
            PromptTokens = 10000,
            CompletionTokens = 1000,
            PromptCacheHitTokens = 8000,
            PromptCacheMissTokens = 2000,
        };

        // cacheHitPrice=0 → 使用 inputPrice 作为 fallback
        var (_, _, cacheHitCost, totalCost) =
            _normalizer.CalculateCost(usage, 1.0m, 2.0m, 0m);

        // cacheHit: 8000/1M * 1.0 = 0.008
        Assert.AreEqual(0.008m, cacheHitCost, 0.00001m);
    }
}

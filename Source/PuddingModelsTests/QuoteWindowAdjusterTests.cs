using PuddingModels;
using PuddingModels.Contracts;

namespace PuddingModelsTests;

/// <summary>
/// PA-06C cross-window dispatch: a frozen quote is kept while usable; otherwise a refreshed quote atomically adjusts
/// the reservation, an insufficient budget blocks dispatch (never reuses the old price), and unknown/changed prices
/// block instead of silently becoming zero.
/// </summary>
[TestClass]
public sealed class QuoteWindowAdjusterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void UsableFrozenQuoteIsKept()
    {
        var frozen = Quote(Now.AddHours(-1), Now.AddHours(1), input: 2m, output: 3m);
        var adjustment = QuoteWindowAdjuster.Adjust(frozen, Now, null, 1000, 100, 0, 0.01m, 1m);
        Assert.AreEqual(QuoteDispatchDecision.Keep, adjustment.Decision);
        Assert.AreEqual(frozen, adjustment.Quote);
        Assert.AreEqual(0m, adjustment.AdditionalReserve);
    }

    [TestMethod]
    public void ExpiredQuoteRequiresARefresh()
    {
        var expired = Quote(Now.AddHours(-2), Now.AddHours(-1), input: 1m, output: 1m);
        var refreshed = Quote(Now.AddHours(-1), Now.AddHours(1), input: 2m, output: 3m);
        var adjustment = QuoteWindowAdjuster.Adjust(expired, Now, refreshed, 1_000_000, 1_000_000, 0, 1m, 10m);
        Assert.AreEqual(QuoteDispatchDecision.Requote, adjustment.Decision);
        Assert.AreEqual(refreshed, adjustment.Quote);
        Assert.IsTrue(adjustment.AdditionalReserve > 1m, "The refreshed (higher) price must increase the reservation.");
    }

    [TestMethod]
    public void MissingRefreshOrUnknownPriceBlocks()
    {
        var expired = Quote(Now.AddHours(-2), Now.AddHours(-1));
        Assert.AreEqual(ModelErrorCodes.PriceUnknown,
            QuoteWindowAdjuster.Adjust(expired, Now, null, 100, 100, 0, 0.1m, 10m).ReasonCode);

        var unknown = Quote(Now.AddHours(-1), Now.AddHours(1)) with { InputPerMillion = null };
        Assert.AreEqual(ModelErrorCodes.PriceUnknown,
            QuoteWindowAdjuster.Adjust(expired, Now, unknown, 100, 100, 0, 0.1m, 10m).ReasonCode);
    }

    [TestMethod]
    public void InsufficientBudgetBlocksWithoutReusingTheOldPrice()
    {
        var expired = Quote(Now.AddHours(-2), Now.AddHours(-1), input: 1m, output: 1m);
        var refreshed = Quote(Now.AddHours(-1), Now.AddHours(1), input: 5m, output: 5m);
        var adjustment = QuoteWindowAdjuster.Adjust(expired, Now, refreshed, 1_000_000, 1_000_000, 0, 0.5m, 1m);
        Assert.AreEqual(QuoteDispatchDecision.Block, adjustment.Decision);
        Assert.IsNull(adjustment.Quote);
        Assert.AreEqual(ModelErrorCodes.QuoteCapExceeded, adjustment.ReasonCode);
    }

    [TestMethod]
    public void CheaperRefreshReleasesTheDifference()
    {
        var expired = Quote(Now.AddHours(-2), Now.AddHours(-1), input: 5m, output: 5m);
        var refreshed = Quote(Now.AddHours(-1), Now.AddHours(1), input: 1m, output: 1m);
        var adjustment = QuoteWindowAdjuster.Adjust(expired, Now, refreshed, 1_000_000, 0, 0, 5m, 0m);
        Assert.AreEqual(QuoteDispatchDecision.Requote, adjustment.Decision);
        Assert.IsTrue(adjustment.AdditionalReserve < 0m, "A cheaper refresh must reduce the reservation.");
    }

    [TestMethod]
    public void CostUsesCacheHitPriceAndBaseUnit()
    {
        var quote = Quote(Now.AddHours(-1), Now.AddHours(1), input: 2m, output: 4m) with { CacheHitPerMillion = 0.5m };
        var cost = QuoteWindowAdjuster.Cost(quote, inputTokens: 1_000_000, outputTokens: 1_000_000,
            cacheHitTokens: 500_000);
        Assert.AreEqual(0.5m * 0.5m + 0.5m * 2m + 1m * 4m, cost);
    }

    private static ModelQuoteSnapshot Quote(DateTimeOffset from, DateTimeOffset to, decimal? input = 2m,
        decimal? output = 3m) => new("quote-1", "model-a", "rev-1", "usd", "per-1m-tokens", input, input, output,
        1_000_000, from, to, "utc", "declared", false, 10m, "pricing-v1", Now);
}

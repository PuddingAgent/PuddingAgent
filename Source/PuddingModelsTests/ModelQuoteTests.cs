using PuddingModels;
using PuddingModels.Contracts;

namespace PuddingModelsTests;

[TestClass]
public sealed class ModelQuoteTests
{
    [TestMethod]
    public async Task ExplicitFree_IsUsable_AndSettlesAsKnownZero()
    {
        var source = new FakeQuoteSource { Quote = ModelFixtures.Quote(explicitFree: true, input: null, cacheHit: null, output: null) };
        var service = new ModelQuoteService(source);
        var quote = await service.FreezeAsync("model-1", "revision-1", ModelFixtures.Now, "usd");
        Assert.IsTrue(quote.ExplicitFree);
        var settlement = ModelQuoteService.Settle(quote, ModelFixtures.Usage(100, 50));
        Assert.IsTrue(settlement.ReviewKnown);
        Assert.AreEqual(0m, settlement.Cost);
    }

    [TestMethod]
    public async Task UndeclaredZeroPrice_IsNotFree()
    {
        var source = new FakeQuoteSource
        {
            Quote = ModelFixtures.Quote(input: 0m, cacheHit: 0m, output: 0m, priceSource: "unknown"),
        };
        var service = new ModelQuoteService(source);
        var failure = await Assert.ThrowsExactlyAsync<ModelException>(async () =>
            await service.FreezeAsync("model-1", "revision-1", ModelFixtures.Now, "usd"));
        Assert.AreEqual(ModelErrorCodes.PriceUnknown, failure.Code, "Zero price without ExplicitFree is not free.");
    }

    [TestMethod]
    public async Task MissingPriceComponents_ArePriceUnknown()
    {
        var source = new FakeQuoteSource { Quote = ModelFixtures.Quote(input: null) };
        var service = new ModelQuoteService(source);
        var failure = await Assert.ThrowsExactlyAsync<ModelException>(async () =>
            await service.FreezeAsync("model-1", "revision-1", ModelFixtures.Now, "usd"));
        Assert.AreEqual(ModelErrorCodes.PriceUnknown, failure.Code);
    }

    [TestMethod]
    public async Task CurrencyMismatch_IsRejected()
    {
        var source = new FakeQuoteSource { Quote = ModelFixtures.Quote(currency: "eur") };
        var service = new ModelQuoteService(source);
        var failure = await Assert.ThrowsExactlyAsync<ModelException>(async () =>
            await service.FreezeAsync("model-1", "revision-1", ModelFixtures.Now, "usd"));
        Assert.AreEqual(ModelErrorCodes.CurrencyMismatch, failure.Code);
    }

    [TestMethod]
    public async Task QuoteWindow_IsHalfOpen_OnTheActualDispatchInstant()
    {
        var source = new FakeQuoteSource
        {
            Quote = ModelFixtures.Quote(from: ModelFixtures.Now.AddHours(-1), to: ModelFixtures.Now),
        };
        var service = new ModelQuoteService(source);
        var missed = await Assert.ThrowsExactlyAsync<ModelException>(async () =>
            await service.FreezeAsync("model-1", "revision-1", ModelFixtures.Now, "usd"));
        Assert.AreEqual(ModelErrorCodes.QuoteWindowMissed, missed.Code);

        source.Quote = ModelFixtures.Quote(from: ModelFixtures.Now, to: ModelFixtures.Now.AddHours(1));
        var quote = await service.FreezeAsync("model-1", "revision-1", ModelFixtures.Now, "usd");
        Assert.AreEqual(ModelFixtures.Now, quote.FrozenAt, "The price is frozen at the actual dispatch instant.");
    }

    [TestMethod]
    public async Task CacheHitTokens_AreChargedOnce_AtTheCacheRate()
    {
        var quote = ModelFixtures.Quote(input: 2m, cacheHit: 0.5m, output: 4m);
        var settlement = ModelQuoteService.Settle(quote, ModelFixtures.Usage(input: 1000, output: 200, cacheHit: 400));
        // (600*2 + 400*0.5 + 200*4) / 1_000_000
        Assert.AreEqual(0.0022m, settlement.Cost);
        Assert.IsTrue(settlement.ReviewKnown);
    }

    [TestMethod]
    public async Task UnknownUsage_StaysUnknown_InsteadOfAFabricatedCharge()
    {
        var quote = ModelFixtures.Quote();
        var settlement = ModelQuoteService.Settle(quote, usage: null);
        Assert.IsFalse(settlement.ReviewKnown);
        Assert.AreEqual(0m, settlement.Cost);
    }

    [TestMethod]
    public async Task ChargeAboveTheDeclaredCap_IsRejected()
    {
        var quote = ModelFixtures.Quote(maxChargeable: 0.001m);
        var failure = Assert.ThrowsExactly<ModelException>(() =>
            ModelQuoteService.Settle(quote, ModelFixtures.Usage(input: 100_000, output: 1_000)));
        Assert.AreEqual(ModelErrorCodes.QuoteCapExceeded, failure.Code);
    }

    [TestMethod]
    public async Task InvalidUsage_IsTypedAsUnknown()
    {
        var quote = ModelFixtures.Quote();
        var failure = Assert.ThrowsExactly<ModelException>(() => ModelQuoteService.Settle(quote, ModelFixtures.Usage(5, 1, cacheHit: 6)));
        Assert.AreEqual(ModelErrorCodes.UsageUnknown, failure.Code);
    }
}

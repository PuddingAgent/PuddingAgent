using System.Collections.Immutable;
using PuddingModels.Contracts;
using PuddingPlatform.Services.Models;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-06C quote adapter: only exact revision/currency/window/known-price quotes are produced; missing price or
/// mismatch returns "no quote" (never zero); explicit-free is the only zero quote; cache-hit falls back to input.
/// </summary>
[TestClass]
public sealed class CatalogModelQuoteSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task QuotesAValidWindowWithKnownPrices()
    {
        var source = new FakeCatalog(Capability());
        var quote = await new CatalogModelQuoteSource(source, new FixedClock(Now)).QuoteAsync(
            Request(Now));
        Assert.IsNotNull(quote);
        Assert.AreEqual("usd", quote.Currency);
        Assert.AreEqual(2m, quote.InputPerMillion);
        Assert.AreEqual(3m, quote.OutputPerMillion);
        Assert.AreEqual(2m, quote.CacheHitPerMillion);
        Assert.AreEqual(1_000_000, quote.BaseUnit);
        Assert.IsTrue(quote.IsUsableAt(Now));
    }

    [TestMethod]
    public async Task MissingPriceOrRevisionOrCurrencyProducesNoQuote()
    {
        var noPrice = new FakeCatalog(Capability(input: null, output: null, explicitFree: false));
        Assert.IsNull(await new CatalogModelQuoteSource(noPrice, new FixedClock(Now)).QuoteAsync(Request(Now)));

        var otherRevision = new FakeCatalog(Capability());
        Assert.IsNull(await new CatalogModelQuoteSource(otherRevision, new FixedClock(Now)).QuoteAsync(
            Request(Now) with { Revision = "rev-other" }));

        var otherCurrency = new FakeCatalog(Capability());
        Assert.IsNull(await new CatalogModelQuoteSource(otherCurrency, new FixedClock(Now)).QuoteAsync(
            Request(Now) with { AccountCurrency = "eur" }));

        var missing = new FakeCatalog(null);
        Assert.IsNull(await new CatalogModelQuoteSource(missing, new FixedClock(Now)).QuoteAsync(Request(Now)));
    }

    [TestMethod]
    public async Task OutsideTheWindowProducesNoQuote()
    {
        var source = new FakeCatalog(Capability());
        Assert.IsNull(await new CatalogModelQuoteSource(source, new FixedClock(Now)).QuoteAsync(
            Request(Now.AddHours(-2))));
        Assert.IsNull(await new CatalogModelQuoteSource(source, new FixedClock(Now)).QuoteAsync(
            Request(Now.AddHours(2))));
    }

    [TestMethod]
    public async Task ExplicitFreeIsTheOnlyZeroQuote()
    {
        var source = new FakeCatalog(Capability(input: null, output: null, explicitFree: true, currency: "usd"));
        var quote = await new CatalogModelQuoteSource(source, new FixedClock(Now)).QuoteAsync(Request(Now));
        Assert.IsNotNull(quote);
        Assert.IsTrue(quote!.ExplicitFree);
        Assert.AreEqual(0m, quote.InputPerMillion);
        Assert.AreEqual(0m, quote.OutputPerMillion);
    }

    [TestMethod]
    public async Task InvalidRequestIsTyped()
    {
        var source = new FakeCatalog(Capability());
        var failure = await Assert.ThrowsExactlyAsync<ModelException>(async () =>
            await new CatalogModelQuoteSource(source, new FixedClock(Now)).QuoteAsync(Request(Now) with { ModelId = "" }));
        Assert.AreEqual(ModelErrorCodes.RequestInvalid, failure.Code);
    }

    private static ModelQuoteRequest Request(DateTimeOffset dispatch) => new("model-a", "rev-1", dispatch, "usd");

    private static ModelCapabilityRecord Capability(decimal? input = 2m, decimal? output = 3m,
        bool explicitFree = false, string? currency = "usd") => new("model-a", "rev-1", 128_000, 8_192, ["text"],
        true, true, "available", InputPerMillion: input, CacheHitPerMillion: null, OutputPerMillion: output,
        Currency: currency, PriceSource: "declared", ExplicitFree: explicitFree,
        QuoteValidFrom: Now.AddHours(-1), QuoteValidTo: Now.AddHours(1), TimeZoneId: "utc",
        MaxChargeable: 10m, PricingRuleVersion: "pricing-v1");

    private sealed class FakeCatalog(ModelCapabilityRecord? record) : IModelCatalogSource
    {
        public ValueTask<ModelCapabilityRecord?> GetAsync(string modelId, CancellationToken ct = default)
            => ValueTask.FromResult(record);

        public ValueTask<ImmutableArray<ModelCapabilityRecord>> ListAsync(string workspaceId, string? cursor,
            int maxCount, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

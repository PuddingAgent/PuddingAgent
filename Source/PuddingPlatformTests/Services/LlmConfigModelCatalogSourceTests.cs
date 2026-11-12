using System.Collections.Immutable;
using PuddingModels.Contracts;
using PuddingPlatform.Services.Models;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-06B catalog over the configured LLM registry: deprecated models stay visible but unavailable, zero configured
/// prices become "unknown" (never explicit free), and listing is bounded/ordered with a cursor.
/// </summary>
[TestClass]
public sealed class LlmConfigModelCatalogSourceTests
{
    [TestMethod]
    public async Task MapsConfiguredModels_WithUnknownZeroPrice()
    {
        var source = new LlmConfigModelCatalogSource(new View(
        [
            new("model-a", "provider-1", 128_000, 120_000, 8_192, 1m, 2m, 0.5m, "pricing-v9", false, false),
            new("model-free", "provider-1", 32_000, 32_000, 4_096, 0m, 0m, 0m, null, false, false),
        ]));
        var model = await source.GetAsync("model-a");
        Assert.IsNotNull(model);
        Assert.AreEqual("pricing-v9", model.Revision);
        Assert.AreEqual(1m, model.InputPerMillion);
        Assert.AreEqual(2m, model.OutputPerMillion);
        var free = await source.GetAsync("model-free");
        Assert.IsNull(free!.InputPerMillion);
        Assert.IsFalse(free.ExplicitFree, "A zero configured price is unknown, not an explicit free model.");
    }

    [TestMethod]
    public async Task Listing_IsOrderedBoundedAndHidesEmbeddings()
    {
        var source = new LlmConfigModelCatalogSource(new View(
        [
            new("model-b", "p", 1, 1, 1, 1m, 1m, 1m, null, false, false),
            new("model-a", "p", 1, 1, 1, 1m, 1m, 1m, null, false, false),
            new("embedding-1", "p", 1, 1, 1, 0m, 0m, 0m, null, false, true),
        ]));
        var page = await source.ListAsync("ws-1", null, 1);
        Assert.AreEqual(1, page.Length);
        Assert.AreEqual("model-a", page[0].ModelId);
        var second = await source.ListAsync("ws-1", "model-a", 10);
        Assert.AreEqual("model-b", second[0].ModelId);
        var failure = await Assert.ThrowsExactlyAsync<ModelException>(async () =>
            await source.ListAsync("ws-1", null, 0));
        Assert.AreEqual(ModelErrorCodes.RequestInvalid, failure.Code);
    }

    private sealed class View(ImmutableArray<LlmCatalogModel> models) : ILlmModelCatalogView
    {
        public IReadOnlyList<LlmCatalogModel> List() => models;
    }
}

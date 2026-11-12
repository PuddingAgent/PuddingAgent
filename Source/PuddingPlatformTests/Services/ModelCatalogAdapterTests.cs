using System.Collections.Immutable;
using Pudding.Api.Contracts;
using PuddingModels.Contracts;
using PuddingPlatform.Services.Models;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-06B catalog adapter: only authorized identity/capability/price-source fields are exposed, queries are bounded
/// and validated before the source, source failures are typed, and model selection returns a typed scope error until
/// the host binds the acting user (never borrowing the agent id as the actor).
/// </summary>
[TestClass]
public sealed class ModelCatalogAdapterTests
{
    [TestMethod]
    public async Task List_MapsOnlyAuthorizedFields_AndSortsById()
    {
        var source = new FakeSource
        {
            Records =
            [
                Capability("model-b", ExplicitFree: false, currency: "usd", input: 1m),
                Capability("model-a", ExplicitFree: true),
            ],
        };
        var page = await new ModelCatalogAdapter(source).ListAsync(new("ws-1", null, 10));
        CollectionAssert.AreEqual(new[] { "model-a", "model-b" }, page.Models.Select(m => m.ModelId).ToArray());
        Assert.IsTrue(page.Models[0].ExplicitFree);
        Assert.IsTrue(page.Complete);
        Assert.AreEqual(1, page.Models[1].InputPerMillion);
        Assert.AreEqual("usd", page.Models[1].Currency);
    }

    [TestMethod]
    public async Task List_InvalidBoundsFailBeforeTheSource()
    {
        var source = new FakeSource();
        foreach (var query in new[]
                 {
                     new ModelQuery("", null, 10), new ModelQuery("ws-1", null, 0), new ModelQuery("ws-1", null, 101),
                     new ModelQuery("ws-1", new string('c', 129), 10),
                 })
        {
            var failure = await Assert.ThrowsExactlyAsync<ModelCatalogException>(async () =>
                await new ModelCatalogAdapter(source).ListAsync(query));
            Assert.AreEqual(ModelErrorCodes.RequestInvalid, failure.Code);
        }
        Assert.AreEqual(0, source.Calls);
    }

    [TestMethod]
    public async Task List_SourceFailureIsTyped_NotAnEmptyCatalog()
    {
        var source = new FakeSource { Failure = new InvalidOperationException("registry down") };
        var failure = await Assert.ThrowsExactlyAsync<ModelCatalogException>(async () =>
            await new ModelCatalogAdapter(source).ListAsync(new("ws-1")));
        Assert.AreEqual(ModelErrorCodes.Unavailable, failure.Code);

        source.Failure = new ModelException(ModelErrorCodes.CapabilityMissing, "missing");
        var typed = await Assert.ThrowsExactlyAsync<ModelCatalogException>(async () =>
            await new ModelCatalogAdapter(source).ListAsync(new("ws-1")));
        Assert.AreEqual(ModelErrorCodes.CapabilityMissing, typed.Code);
    }

    [TestMethod]
    public async Task Resolve_IsTypedScopeForbidden_UntilTheHostBindsTheActor()
    {
        var failure = await Assert.ThrowsExactlyAsync<ModelCatalogException>(async () =>
            await new ModelCatalogAdapter(new FakeSource()).ResolveAsync(
                new(new("ws-1", "agent-1"), "Review", ["read"])));
        Assert.AreEqual(ModelErrorCodes.ScopeForbidden, failure.Code);
    }

    private static ModelCapabilityRecord Capability(string modelId, bool ExplicitFree = false, string? currency = null,
        decimal? input = null) => new(modelId, "rev-1", 128_000, 8_192, ["text"], true, true, "available",
        InputPerMillion: input, CacheHitPerMillion: null, OutputPerMillion: 2m, Currency: currency,
        PriceSource: "declared", ExplicitFree: ExplicitFree, QuoteValidFrom: DateTimeOffset.UtcNow.AddDays(-1),
        QuoteValidTo: DateTimeOffset.UtcNow.AddDays(1), TimeZoneId: "utc", MaxChargeable: 10m,
        PricingRuleVersion: "pricing-v1");

    private sealed class FakeSource : IModelCatalogSource
    {
        public ImmutableArray<ModelCapabilityRecord> Records = [];
        public Exception? Failure;
        public int Calls;

        public ValueTask<ModelCapabilityRecord?> GetAsync(string modelId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask<ImmutableArray<ModelCapabilityRecord>> ListAsync(string workspaceId, string? cursor,
            int maxCount, CancellationToken ct = default)
        {
            Calls++;
            if (Failure is not null) throw Failure;
            return ValueTask.FromResult(Records);
        }
    }
}

using System.Collections.Immutable;
using Pudding.Api.Contracts;
using Pudding.Api.Sdk;

namespace Pudding.Api.SdkTests;

[TestClass]
public sealed class ModelCatalogClientTests
{
    [TestMethod]
    public async Task ForwardsQueryAndSelection_WithoutExposingSecrets()
    {
        var api = new Fake();
        var client = new ModelCatalogClient(api);
        var query = new ModelQuery("ws-1", null, 20);
        var page = await client.ListAsync(query);
        Assert.AreEqual(query, api.Query);
        Assert.AreEqual("model-1", page.Models[0].ModelId);

        var selection = await client.ResolveAsync(new(new("ws-1", "agent-1"), "Review", ["read"]));
        Assert.AreEqual("model-1", selection.ModelId);
        Assert.AreEqual("policy-v1", api.Request!.Purpose is "Review" ? "policy-v1" : "mismatch");
    }

    [TestMethod]
    public async Task TypedFailuresAreNotSwallowed()
    {
        var api = new Fake { Failure = new ModelCatalogException("model.policy_missing", "no review model") };
        var client = new ModelCatalogClient(api);
        var failure = await Assert.ThrowsExactlyAsync<ModelCatalogException>(() =>
            client.ResolveAsync(new(new("ws-1", "agent-1"), "Review", [])));
        Assert.AreEqual("model.policy_missing", failure.Code);
    }

    private sealed class Fake : IModelCatalogApi
    {
        public ModelQuery? Query;
        public ModelSelectionRequest? Request;
        public Exception? Failure;

        public Task<ModelCatalogPage> ListAsync(ModelQuery query, CancellationToken ct = default)
        {
            if (Failure is not null) return Task.FromException<ModelCatalogPage>(Failure);
            Query = query;
            return Task.FromResult(new ModelCatalogPage(
                [new("model-1", "rev-1", 128_000, 8_192, ["text"], true, true, "available", "declared", false, "usd", 2, 0.5m, 4)],
                null, true));
        }

        public Task<ModelSelection> ResolveAsync(ModelSelectionRequest request, CancellationToken ct = default)
        {
            if (Failure is not null) return Task.FromException<ModelSelection>(Failure);
            Request = request;
            return Task.FromResult(new ModelSelection("model-1", "rev-1", "explicit", false));
        }
    }
}

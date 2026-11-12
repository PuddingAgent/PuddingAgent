using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingExecutionResources.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Reviews;
using PuddingReviews.Contracts;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-03 lease source: the trusted catalog binding becomes a frozen leased model (limits/prices/revision) and the host
/// provider is invoked exactly once with the bounded request; invalid bindings fail before any provider call.
/// </summary>
[TestClass]
public sealed class ReviewModelLeaseSourceTests
{
    [TestMethod]
    public async Task Binding_BecomesAFrozenLease_AndTheProviderIsInvokedOnce()
    {
        var provider = new FakeProvider();
        var source = new ReviewModelLeaseSource(provider);
        var binding = Binding();
        var model = await source.AcquireAsync(binding);
        Assert.AreEqual("model-review", model.Descriptor.ModelId);
        Assert.AreEqual("rev-42", model.Descriptor.Revision);
        Assert.AreEqual(128_000, model.Descriptor.MaxInputTokens);
        Assert.AreEqual(8_192, model.Descriptor.MaxOutputTokens);
        Assert.AreEqual(1m, model.Descriptor.Price.InputPerMillion);

        var dispatched = 0;
        var completion = await model.InvokeAsync(new ModelResourceRequest("call-1", "instruction", "input", 512),
            () => dispatched++, CancellationToken.None);
        Assert.AreEqual(1, dispatched);
        Assert.AreEqual(1, provider.Calls);
        Assert.AreEqual("accept", completion.Text);
        Assert.AreEqual(100L, completion.Usage!.InputTokens);
    }

    [TestMethod]
    public async Task InvalidBinding_FailsBeforeTheProvider()
    {
        var provider = new FakeProvider();
        var source = new ReviewModelLeaseSource(provider);
        var failure = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await source.AcquireAsync(Binding() with { MaxInputTokens = 0 }));
        Assert.AreEqual("model.configuration_invalid", failure.Code);
        Assert.AreEqual(0, provider.Calls);
    }

    private static ReviewModelBinding Binding() => new("model-review", "rev-42", "pricing-v9", "available", "usd",
        false, new("budget-1"), new(BudgetAccountKind.GoalRun, "goal-1", "ws-1", "agent-1", "session-1"),
        128_000, 8_192, 1m, 0.5m, 2m, "usd");

    private sealed class FakeProvider : IReviewModelProvider
    {
        public int Calls { get; private set; }

        public ValueTask<ModelCompletion> CompleteAsync(ReviewModelBinding binding, ModelResourceRequest request,
            CancellationToken ct = default)
        {
            Calls++;
            return ValueTask.FromResult(new ModelCompletion("accept", new ModelUsage(100, 20), "stop"));
        }
    }
}

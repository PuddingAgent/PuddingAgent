using PuddingExecutionResources.Contracts;
using PuddingModels.Contracts;
using PuddingPlatform.Services.Models;
using PuddingPlatform.Services.Reviews;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-03 provider adapter: the frozen model resolves to its configured provider in the LLM catalog, exactly one bounded
/// chat call is made, usage mapping is conservative, and an unresolvable model fails before any gateway call.
/// </summary>
[TestClass]
public sealed class PlatformReviewModelProviderTests
{
    [TestMethod]
    public async Task CompleteAsync_ResolvesConfiguredProvider_AndMapsUsage()
    {
        var client = new FakeClient();
        var provider = new PlatformReviewModelProvider(client, new FakeView());
        var completion = await provider.CompleteAsync(Binding(), new ModelResourceRequest("call-1", "instruction",
            "input", 512));
        Assert.AreEqual("accept", completion.Text);
        Assert.AreEqual(100L, completion.Usage!.InputTokens);
        Assert.AreEqual(20L, completion.Usage.OutputTokens);
        Assert.AreEqual(5L, completion.Usage.CacheHitTokens);
        Assert.AreEqual("stop", completion.FinishReason);
        Assert.AreEqual("provider-1", client.Last!.ProviderId);
        Assert.AreEqual("model-review", client.Last.ModelId);
        Assert.AreEqual("instruction", client.Last.SystemInstruction);
        Assert.AreEqual(1, client.Calls);
    }

    [TestMethod]
    public async Task UnknownModel_FailsBeforeTheClient()
    {
        var client = new FakeClient();
        var provider = new PlatformReviewModelProvider(client, new FakeView());
        var failure = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await provider.CompleteAsync(Binding() with { ModelId = "unknown" },
                new ModelResourceRequest("call-1", "i", "x", 8)));
        Assert.AreEqual("model.configuration_invalid", failure.Code);
        Assert.AreEqual(0, client.Calls);
    }

    [TestMethod]
    public async Task MissingUsage_LeavesTheCompletionUnknownForTheBudgetLayer()
    {
        var client = new FakeClient { NoUsage = true };
        var provider = new PlatformReviewModelProvider(client, new FakeView());
        var completion = await provider.CompleteAsync(Binding(), new ModelResourceRequest("call-1", "i", "x", 8));
        Assert.IsNull(completion.Usage);
    }

    private static ReviewModelBinding Binding() => new("model-review", "rev-42", "pricing-v9", "available", "usd",
        false, new("budget-1"), new(BudgetAccountKind.GoalRun, "goal-1", "ws-1", "agent-1", "session-1"));

    private sealed class FakeView : ILlmModelCatalogView
    {
        public IReadOnlyList<LlmCatalogModel> List() =>
        [
            new("model-review", "provider-1", 128_000, 120_000, 8_192, 1m, 2m, 0.5m, "pricing-v9", false, false),
        ];
    }

    private sealed class FakeClient : IRuntimeLlmChatClient
    {
        public bool NoUsage { get; init; }
        public int Calls { get; private set; }
        public RuntimeLlmChatRequest? Last { get; private set; }

        public ValueTask<RuntimeLlmChatResult> ChatAsync(RuntimeLlmChatRequest request, CancellationToken ct = default)
        {
            Calls++;
            Last = request;
            return ValueTask.FromResult(NoUsage
                ? new RuntimeLlmChatResult("accept", null, null, null, "stop")
                : new RuntimeLlmChatResult("accept", 100, 20, 5, "stop"));
        }
    }
}

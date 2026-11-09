using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingModels;
using PuddingModels.Contracts;
using PuddingPlatform.Services.Models;

namespace PuddingPlatformTests.Services;

/// <summary>
/// CORE-I1-06 T4: the host concurrency port must hand out one real slot per attempt, queue a busy model until a release,
/// honor parent cancellation while waiting, and reject dispatch once the generation is revoked.
/// </summary>
[TestClass]
public sealed class ModelLeaseConcurrencyPortTests
{
    [TestMethod]
    public async Task BusyModel_QueuesUntilThePreviousAttemptReleases()
    {
        var registry = new ModelLeaseRegistry();
        var port = new ModelLeaseConcurrencyPort(registry, new FakeConfigs(concurrency: 1));

        var first = await port.AcquireAsync("model-a");
        var secondTask = port.AcquireAsync("model-a").AsTask();
        var winner = await Task.WhenAny(secondTask, Task.Delay(300));
        Assert.AreNotSame(secondTask, winner, "A busy model must wait for the held slot.");

        await first.DisposeAsync();
        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(1, registry.ActiveCount("model-a"));
        await second.DisposeAsync();
        Assert.AreEqual(0, registry.ActiveCount("model-a"));
    }

    [TestMethod]
    public async Task ParentCancellation_WhileQueued_ReleasesNothingExtra()
    {
        var registry = new ModelLeaseRegistry();
        var port = new ModelLeaseConcurrencyPort(registry, new FakeConfigs(concurrency: 1));
        var held = await port.AcquireAsync("model-a");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await port.AcquireAsync("model-a", cts.Token));
        Assert.AreEqual(1, registry.ActiveCount("model-a"), "Only the held slot may remain.");

        await held.DisposeAsync();
        Assert.AreEqual(0, registry.ActiveCount("model-a"));
    }

    [TestMethod]
    public async Task RevokedGeneration_RejectsNewDispatch()
    {
        var registry = new ModelLeaseRegistry();
        var port = new ModelLeaseConcurrencyPort(registry, new FakeConfigs(concurrency: 1));
        var generation = port.EnsureGeneration();
        await registry.RevokeGenerationAsync(generation);

        var failure = await Assert.ThrowsExactlyAsync<ModelException>(async () =>
            await port.AcquireAsync("model-a"));
        Assert.AreEqual(ModelErrorCodes.LeaseGenerationRevoked, failure.Code);
    }

    [TestMethod]
    public async Task SlotDisposedTwice_ReleasesOnlyOnce()
    {
        var registry = new ModelLeaseRegistry();
        var port = new ModelLeaseConcurrencyPort(registry, new FakeConfigs(concurrency: 1));
        var slot = await port.AcquireAsync("model-a");

        await slot.DisposeAsync();
        await slot.DisposeAsync();

        Assert.AreEqual(0, registry.ActiveCount("model-a"));
    }

    private sealed class FakeConfigs(int concurrency) : ILlmConfigService
    {
        public IReadOnlyList<LlmProviderInfo> GetEnabledProviders() => [];
        public IReadOnlyList<LlmModelInfo> GetAllModels() =>
        [
            new()
            {
                ModelId = "model-a", ProviderId = "provider-a", Name = "model-a", Protocol = "openai",
                MaxContextTokens = 128_000, MaxOutputTokens = 8_192, MaxConcurrentRequests = concurrency,
            },
        ];
        public LlmConfig? Resolve(string providerId, string modelId) => null;
        public LlmProfileInfo? ResolveProfile(string profileId) => null;
        public LlmConfig? GetMemoryConfig() => null;
        public LlmConfig? GetEmbeddingConfig() => null;
        public LlmProviderStrategy? GetProviderStrategy(string providerId) => null;
        public LlmProviderStrategy? GetModelStrategy(string providerId, string modelId) => null;
        public void Reload(object config) { }
    }
}

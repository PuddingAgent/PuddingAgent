using PuddingCode.Platform;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class FrozenVisionContextAccessorTests
{
    [TestMethod]
    public async Task EachMoveNext_RebindsFrozenRouteAfterOuterAndInnerYields()
    {
        var context = new FrozenVisionContextAccessor();
        var route = new LlmRouteSnapshot("provider", "vision-model", "responses", ["vision"]);
        var observed = new List<LlmRouteSnapshot?>();

        async IAsyncEnumerable<int> ProviderStream()
        {
            observed.Add(context.Current);
            await Task.Yield();
            observed.Add(context.Current);
            yield return 1;
            observed.Add(context.Current);
            yield return 2;
        }

        async IAsyncEnumerable<int> AgentStream()
        {
            using var entryScope = context.Push(route);
            yield return 0; // SSE before the first LLM request reproduces the incident.
            await using var llm = ProviderStream().GetAsyncEnumerator();
            while (await context.MoveNextAsync(llm, route))
                yield return llm.Current;
        }

        var values = new List<int>();
        await foreach (var value in AgentStream())
        {
            values.Add(value);
            Assert.IsNull(context.Current, "Agent capability must not leak into its caller.");
        }
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, values);
        Assert.AreEqual(3, observed.Count);
        Assert.IsTrue(observed.All(value => ReferenceEquals(route, value)));
    }

    [TestMethod]
    public async Task InterleavedStreams_KeepTheirOwnCapabilitiesAndRestoreAfterFailure()
    {
        var context = new FrozenVisionContextAccessor();
        var vision = new LlmRouteSnapshot("provider", "vision-model", "responses", ["vision"]);
        var text = new LlmRouteSnapshot("provider", "text-model", "responses", []);
        async IAsyncEnumerable<bool> ReadCapability()
        {
            await Task.Yield();
            yield return context.Current?.SupportsVision == true;
            yield return context.Current?.SupportsVision == true;
            throw new OperationCanceledException();
        }
        using var outer = context.Push(text);
        await using var a = ReadCapability().GetAsyncEnumerator();
        await using var b = ReadCapability().GetAsyncEnumerator();
        Assert.IsTrue(await context.MoveNextAsync(a, vision));
        Assert.IsTrue(a.Current);
        Assert.IsTrue(await context.MoveNextAsync(b, null));
        Assert.IsFalse(b.Current);
        Assert.IsTrue(await context.MoveNextAsync(a, vision));
        Assert.IsTrue(a.Current);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await context.MoveNextAsync(a, vision));
        Assert.AreSame(text, context.Current);
    }
}

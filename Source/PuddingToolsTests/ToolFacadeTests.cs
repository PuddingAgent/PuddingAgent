using PuddingTools;
using PuddingTools.Contracts;

namespace PuddingToolsTests;

[TestClass]
public sealed class ToolFacadeTests
{
    private static ToolFacade Facade(FakeToolSource source) => new(source);

    [TestMethod]
    public async Task Search_HidesInvisibleTools_AndOrdersDeterministically()
    {
        var page = await Facade(ToolFixtures.Source()).SearchAsync(new(null, null, null, null, 100));
        CollectionAssert.AreEqual(new[] { "alpha-read", "beta-query", "zeta-write" },
            page.Tools.Select(t => t.ToolId).ToArray());
        Assert.IsFalse(page.Tools.Any(t => t.ToolId == "hidden-tool"), "An invisible tool must never be listed.");
    }

    [TestMethod]
    public async Task Search_FiltersAndCapsResultSize()
    {
        var source = ToolFixtures.Source();
        var facade = Facade(source);
        var byText = await facade.SearchAsync(new("write", null, null, null, 10));
        CollectionAssert.AreEqual(new[] { "zeta-write" }, byText.Tools.Select(t => t.ToolId).ToArray());

        var byCategory = await facade.SearchAsync(new(null, "filesystem", null, null, 10));
        CollectionAssert.AreEqual(new[] { "alpha-read", "zeta-write" }, byCategory.Tools.Select(t => t.ToolId).ToArray());

        var byEffect = await facade.SearchAsync(new(null, null, ToolSideEffect.Read, null, 10));
        CollectionAssert.AreEqual(new[] { "alpha-read" }, byEffect.Tools.Select(t => t.ToolId).ToArray());

        source.Tools = [ToolFixtures.Tool("a"), ToolFixtures.Tool("b"), ToolFixtures.Tool("c")];
        var capped = await facade.SearchAsync(new(null, null, null, null, 2));
        Assert.AreEqual(2, capped.Tools.Length);
        Assert.IsFalse(capped.Complete, "A truncated page must not claim completeness.");
    }

    [TestMethod]
    public async Task Search_InvalidBoundsFailBeforeQueryingTheSource()
    {
        var source = ToolFixtures.Source();
        foreach (var query in new ToolQuery?[]
                 {
                     new(null, null, null, null, 0),
                     new(null, null, null, null, 101),
                     new(new string('q', 257), null, null, null, 10),
                     new(null, new string('c', 129), null, null, 10),
                 })
        {
            var failure = await Assert.ThrowsExactlyAsync<ToolFacadeException>(async () =>
                await Facade(source).SearchAsync(query!));
            Assert.AreEqual(ToolErrorCodes.QueryInvalid, failure.Code);
        }
        Assert.AreEqual(0, source.Searches, "Invalid bounds must not reach the source.");
    }

    [TestMethod]
    public async Task Inspect_ReturnsVisibleSchema_ButNeverLeaksHiddenTools()
    {
        var facade = Facade(ToolFixtures.Source());
        var schema = await facade.InspectAsync("alpha-read");
        Assert.AreEqual(3L, schema.Revision);
        Assert.AreEqual("Read help", schema.Help);

        foreach (var id in new[] { "hidden-tool", "missing-tool" })
        {
            var failure = await Assert.ThrowsExactlyAsync<ToolFacadeException>(async () => await facade.InspectAsync(id));
            Assert.AreEqual(ToolErrorCodes.NotFound, failure.Code, id);
        }
    }

    [TestMethod]
    public async Task State_PreservesUnknownAvailability_AndHidesUnknownTools()
    {
        var facade = Facade(ToolFixtures.Source());
        var state = await facade.GetStateAsync("alpha-read");
        Assert.AreEqual("unknown", state.Availability);
        Assert.AreEqual("upstream-not-configured", state.MissingReason);

        var failure = await Assert.ThrowsExactlyAsync<ToolFacadeException>(async () =>
            await facade.GetStateAsync("hidden-tool"));
        Assert.AreEqual(ToolErrorCodes.NotFound, failure.Code);
    }

    [TestMethod]
    public async Task SourceFailure_IsTyped_NotAnEmptySuccess()
    {
        var source = new FakeToolSource { Failure = new InvalidOperationException("db down") };
        foreach (var call in new Func<Task>[]
                 {
                     () => Facade(source).SearchAsync(new(null, null, null, null, 10)).AsTask(),
                     () => Facade(source).InspectAsync("alpha-read").AsTask(),
                     () => Facade(source).GetStateAsync("alpha-read").AsTask(),
                 })
        {
            var failure = await Assert.ThrowsExactlyAsync<ToolFacadeException>(async () => await call());
            Assert.AreEqual(ToolErrorCodes.SourceUnavailable, failure.Code);
        }
    }

    [TestMethod]
    public async Task InvalidToolId_IsRejectedBeforeTheSource()
    {
        var source = ToolFixtures.Source();
        foreach (var id in new[] { "", "a/b", "a\\b", "a:b" })
        {
            var failure = await Assert.ThrowsExactlyAsync<ToolFacadeException>(async () =>
                await Facade(source).InspectAsync(id));
            Assert.AreEqual(ToolErrorCodes.ToolInvalid, failure.Code, id);
        }
        Assert.AreEqual(0, source.Searches);
    }
}

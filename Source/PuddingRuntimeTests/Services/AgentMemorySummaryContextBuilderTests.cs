using PuddingCode.Configuration;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class AgentMemorySummaryContextBuilderTests
{
    [TestMethod]
    public async Task BuildAsync_ShouldInjectOncePerSession_NotOncePerAgent()
    {
        using var temp = new TempDataRoot();
        var agentId = "agent-compact";
        var store = new SessionSummaryStore(temp.Paths);
        await store.SaveAsync(
            agentId,
            "source-session",
            "<compact_summary>用户正在诊断 compact 后的历史上下文。</compact_summary>",
            ["- [user]: 上一次我问了什么问题"],
            compressedAt: DateTimeOffset.Now,
            ct: CancellationToken.None);

        var builder = new AgentMemorySummaryContextBuilder(temp.Paths, sessionSummaryStore: store);

        var firstSessionContext = await builder.BuildAsync(
            "session-after-compact-1",
            agentId,
            isFirstMessage: true,
            CancellationToken.None);
        var sameSessionAgain = await builder.BuildAsync(
            "session-after-compact-1",
            agentId,
            isFirstMessage: true,
            CancellationToken.None);
        var secondSessionContext = await builder.BuildAsync(
            "session-after-compact-2",
            agentId,
            isFirstMessage: true,
            CancellationToken.None);

        StringAssert.Contains(firstSessionContext, "--- LAYER: HISTORICAL CONTEXT ---");
        StringAssert.Contains(firstSessionContext, "用户正在诊断 compact 后的历史上下文");
        Assert.AreEqual(string.Empty, sameSessionAgain);
        StringAssert.Contains(secondSessionContext, "--- LAYER: HISTORICAL CONTEXT ---");
        StringAssert.Contains(secondSessionContext, "上一次我问了什么问题");
    }

    [TestMethod]
    public async Task BuildAsync_ShouldSkipNonFirstMessage()
    {
        using var temp = new TempDataRoot();
        var agentId = "agent-compact";
        var store = new SessionSummaryStore(temp.Paths);
        await store.SaveAsync(
            agentId,
            "source-session",
            "<compact_summary>历史摘要。</compact_summary>",
            compressedAt: DateTimeOffset.Now,
            ct: CancellationToken.None);

        var builder = new AgentMemorySummaryContextBuilder(temp.Paths, sessionSummaryStore: store);

        var context = await builder.BuildAsync(
            "session-after-compact",
            agentId,
            isFirstMessage: false,
            CancellationToken.None);

        Assert.AreEqual(string.Empty, context);
    }

    private sealed class TempDataRoot : IDisposable
    {
        public TempDataRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "pudding-agent-memory-summary-tests", Guid.NewGuid().ToString("N"));
            Paths = PuddingDataPaths.FromRoot(Root);
        }

        public string Root { get; }
        public PuddingDataPaths Paths { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}

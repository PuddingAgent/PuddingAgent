using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class BackgroundCompactionCandidateTests
{
    [TestMethod]
    public async Task PreparationCannotModifyLiveHistoryUntilForegroundReleasesBoundary()
    {
        var history = new List<string> { "old" };
        await using var candidate = new BackgroundCompactionCandidate<List<string>, int>(async (gate, ct) =>
        {
            var live = await gate(ct);
            live[0] = "summary";
            return live.Count;
        }, CancellationToken.None);
        await candidate.WaitUntilReadyAsync(CancellationToken.None);
        Assert.AreEqual("old", history[0]);
        Assert.IsFalse(candidate.Completion.IsCompleted);
        history.Add("new user message");
        Assert.AreEqual(2, await candidate.CommitAtBoundaryAsync(history));
        CollectionAssert.AreEqual(new[] { "summary", "new user message" }, history);
    }

    [TestMethod]
    public async Task PreparationFailureWakesHardWaitWithoutOpeningCommitGate()
    {
        await using var candidate = new BackgroundCompactionCandidate<int, int>((_, _) => Task.FromResult(-1), CancellationToken.None);
        await candidate.WaitUntilReadyAsync(CancellationToken.None);
        Assert.AreEqual(-1, await candidate.Completion);
        Assert.IsFalse(candidate.IsReady);
    }

    [TestMethod]
    public async Task TurnCancellationJoinsCandidateAndNeverCommits()
    {
        var committed = false;
        var candidate = new BackgroundCompactionCandidate<int, int>(async (gate, ct) =>
        {
            await gate(ct);
            committed = true;
            return 1;
        }, CancellationToken.None);
        await candidate.WaitUntilReadyAsync(CancellationToken.None);
        await candidate.DisposeAsync();
        Assert.IsFalse(committed);
        Assert.IsTrue(candidate.Completion.IsCompleted);
    }
}

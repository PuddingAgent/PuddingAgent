using PuddingRuntime.Services.AgentLoop;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class FailedToolCallTrackerTests
{
    [TestMethod]
    public void Observe_SecondUnchangedFailure_ReturnsExecutionStalledAndBlocksNextAttempt()
    {
        var tracker = new FailedToolCallTracker();
        var failure = Failed("hunk did not match");

        var first = tracker.Observe("file_patch|same-args", failure);
        var second = tracker.Observe("file_patch|same-args", failure);
        var blocked = tracker.TryCreateBlockedResult("file_patch|same-args", out var third);

        Assert.DoesNotContain("execution_stalled", first.Error!);
        StringAssert.StartsWith(second.Error, "execution_stalled:");
        Assert.AreEqual("execution_stalled", second.Metadata!["runtime_status"]);
        Assert.IsTrue(blocked);
        StringAssert.StartsWith(third.Error, "execution_stalled:");
    }

    [TestMethod]
    public void Observe_ChangedFailureOrSuccess_ResetsStalledState()
    {
        var tracker = new FailedToolCallTracker();

        tracker.Observe("shell|same-args", Failed("first failure"));
        var changed = tracker.Observe("shell|same-args", Failed("different failure"));
        tracker.Observe("shell|same-args", Succeeded());

        Assert.DoesNotContain("execution_stalled", changed.Error!);
        Assert.IsFalse(tracker.TryCreateBlockedResult("shell|same-args", out _));
    }

    private static SkillResult Failed(string error) => new()
    {
        Success = false,
        Output = string.Empty,
        Error = error,
        ExitCode = 1,
    };

    private static SkillResult Succeeded() => new()
    {
        Success = true,
        Output = "ok",
        ExitCode = 0,
    };
}

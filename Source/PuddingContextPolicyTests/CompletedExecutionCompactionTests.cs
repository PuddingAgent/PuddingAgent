using PuddingContextPolicy;

namespace PuddingContextPolicyTests;

[TestClass]
public sealed class CompletedExecutionCompactionTests
{
    private static CandidateSelectionResult Select(IReadOnlyList<ContextMessageShape> messages, int tail = 8,
        long fixedBytes = 90_000, int tokenLimit = 605_760) => ContextCandidateSelector.Select(messages, 20_000,
            ContextCapacityArithmetic.Resolve(new(tokenLimit)), new(tail), "deepseek", "tools", 2, "revision",
            new ContextRequestBudget(fixedBytes, 524_288, 100, 700));

    private static List<ContextMessageShape> LongTurn()
    {
        var messages = new List<ContextMessageShape>
        {
            new("system", ContextMessageRole.System, 1000, 4000),
            new("old-checkpoint", ContextMessageRole.Assistant, 5496, 22_000),
            new("current-input", ContextMessageRole.User, 20, 100),
        };
        for (var i = 0; i < 35; i++)
        {
            messages.Add(new($"call-{i}", ContextMessageRole.Assistant, 100, 500, [$"tool-{i}"]));
            messages.Add(new($"result-{i}", ContextMessageRole.Tool, 2700, 12_000, ToolResultId: $"tool-{i}"));
        }
        return messages;
    }

    [TestMethod]
    public void BytePreparationThresholdCreatesCandidateBeforeHardUploadLimit()
    {
        var source = LongTurn().Take(43).ToArray();
        var result = ContextCandidateSelector.Select(source, 20_000,
            ContextCapacityArithmetic.Resolve(new(605_760)), new(TextTriggerRatio: .60),
            "deepseek", "tools", 2, "revision", new ContextRequestBudget(90_000, 524_288, 100, 700));
        Assert.IsTrue(source.Sum(m => m.TextBytes) + 90_000 < 524_288);
        Assert.IsTrue(result.Created);
    }

    [TestMethod]
    public void LongCurrentTurn_SelectsCompletedExecutionInsteadOfResummarizingTinyOldCheckpoint()
    {
        var source = LongTurn();
        var result = Select(source);
        Assert.IsTrue(result.Created);
        Assert.AreEqual(3, result.RemovedFromIndex);
        Assert.IsGreaterThan(30, result.RemovedCount);
        Assert.IsFalse(result.RemovedMessages.Any(m => m.Identity == "current-input"));
        CollectionAssert.AreEqual(source.TakeLast(8).ToArray(), result.RetainedMessages.TakeLast(8).ToArray());
        var allowance = result.SummaryAllowance!;
        Assert.IsGreaterThan(0, allowance.Tokens);
        Assert.IsTrue(allowance.RetainedTextBytes + allowance.TextBytes + 700 <= 524_288);
        Assert.IsTrue(result.ProjectedUsedTokensExcludingSummary + allowance.Tokens + 100 <= 605_760);
    }

    [TestMethod]
    public void IncompleteOrMismatchedBatch_IsABarrier_EvenOutsideRecentTail()
    {
        var source = LongTurn();
        source[18] = source[18] with { ToolResultId = "wrong-id" };
        source[31] = source[31] with { ToolCallIds = ["tool-14", "pending-second-call"] };
        var result = Select(source);
        Assert.IsTrue(result.Created);
        Assert.IsTrue(result.RetainedMessages.Contains(source[17]));
        Assert.IsTrue(result.RetainedMessages.Contains(source[18]));
        Assert.IsTrue(result.RetainedMessages.Contains(source[31]));
        Assert.IsTrue(result.RetainedMessages.Contains(source[32]));
        foreach (var call in result.RemovedMessages.Where(m => m.ToolCallIds is { Count: > 0 }))
            foreach (var id in call.ToolCallIds!)
                Assert.IsTrue(result.RemovedMessages.Any(m => m.ToolResultId == id));
    }

    [TestMethod]
    public void TailBoundaryInsideParallelBatch_ProtectsEntireBatch()
    {
        var source = LongTurn();
        source[^2] = source[^2] with { ToolCallIds = ["tool-34", "parallel"] };
        source.Add(new("parallel-result", ContextMessageRole.Tool, 1000, 4000, ToolResultId: "parallel"));
        var result = Select(source, tail: 1);
        Assert.IsTrue(result.Created);
        Assert.IsTrue(source.TakeLast(3).All(m => result.RetainedMessages.Contains(m)));
    }

    [TestMethod]
    public void FixedFloorCannotFit_DoesNotGrantSummaryBudget()
    {
        var result = Select(LongTurn(), fixedBytes: 530_000);
        Assert.IsTrue(result.Created);
        Assert.AreEqual(0, result.SummaryAllowance!.Tokens);
        Assert.AreEqual(0L, result.SummaryAllowance.TextBytes);
    }

    [TestMethod]
    public void RecentCountPreference_YieldsToByteBudget_WithoutSplittingNewestUnit()
    {
        var source = new List<ContextMessageShape> { new("system", ContextMessageRole.System, 100, 400) };
        for (var i = 0; i < 6; i++)
        {
            source.Add(new($"user-{i}", ContextMessageRole.User, 1000, 70_000));
            source.Add(new($"assistant-{i}", ContextMessageRole.Assistant, 1000, 70_000));
        }
        source.Add(new("current", ContextMessageRole.User, 20, 100));
        var result = Select(source);
        Assert.IsTrue(result.Created);
        Assert.IsGreaterThan(0, result.SummaryAllowance!.Tokens);
        Assert.IsTrue(result.RetainedMessages.Contains(source[^1]));
        Assert.IsTrue(result.SummaryAllowance.RetainedTextBytes + result.SummaryAllowance.TextBytes + 700 <= 524_288);
    }

    [TestMethod]
    public void RepeatedIdentities_ValidateAtFrozenPosition()
    {
        var frozen = new FrozenCandidateDescriptor("f", 1, "r", "m", "t", ContextPolicyVersion.Current,
            ["repeat"], ["tail"], RemovedFromIndex: 3);
        var current = new CurrentContextDescriptor(1, "r", "m", "t", ContextPolicyVersion.Current,
            ["sys", "repeat", "user", "repeat", "tail"]);
        Assert.IsTrue(ContextCandidateApplicability.Validate(frozen, current).IsApplicable);
        Assert.IsFalse(ContextCandidateApplicability.Validate(frozen with { RemovedFromIndex = 2 }, current).IsApplicable);
    }
}

using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingRuntime.Services;
using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntimeTests.Services.TaskE2E;

[TestClass]
public sealed class WorkUnitInputCapacityE2ETests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TwoActualRoundsCanExceedCumulativeInputCapacity(bool streaming)
    {
        using var harness = new TaskE2EHarness();
        await harness.InitializeAsync();
        var task = await harness.SeedAssignedTaskAsync("ws-1", "capacity-task", "capacity-assignment", "agent-1");
        var usage = new TokenUsageDto { PromptTokens = 80_000, CompletionTokens = 50, PromptCacheHitTokens = 70_000 };
        harness.Llm.EnqueueResponse(new LlmResponse(null,
            [new ToolCall("capacity-claim", "task_claim",
                """{"task_id":"capacity-task","assignment_id":"capacity-assignment","expected_version":1}""")],
            Usage: usage));
        harness.Llm.EnqueueResponse(new LlmResponse("""{"status":"DONE","message":"finished"}""", null, Usage: usage));
        var request = harness.CreateDispatchRequest($"capacity-{streaming}", task) with
        {
            LlmConfig = new LlmConfig { ModelId = "test-model", MaxContextTokens = 200_000 },
            UsageBudget = new ExecutionUsageBudget { MaxInputTokens = 100_000 },
        };
        if (streaming)
        {
            var frames = new List<ServerSentEventFrame>();
            await foreach (var frame in harness.ExecutionService.ExecuteStreamAsync(request))
                frames.Add(frame);
            Assert.IsFalse(frames.Any(f => f.Event == "error" || f.Data.Contains("\"isError\":true")),
                string.Join('\n', frames.Select(f => f.Data)));
        }
        else
        {
            var result = await harness.ExecutionService.ExecuteAsync(request);
            Assert.AreEqual(AgentExecutionState.Completed, result.ExecutionState, result.ErrorMessage);
            Assert.AreEqual(160_000, result.Usage!.PromptTokens);
        }
        Assert.AreEqual(2, harness.Llm.CallCount);
        Assert.AreEqual(1, harness.Tools.Captured.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OversizedProtectedRequestNeverInvokesProvider(bool streaming)
    {
        using var harness = new TaskE2EHarness(useContextBudgetGuard: true);
        await harness.InitializeAsync();
        harness.Llm.Enqueue("""{"status":"DONE","message":"must not run"}""");
        var request = harness.CreateDispatchRequest($"capacity-guard-{streaming}", null) with
        {
            LlmConfig = new LlmConfig { ModelId = "test-model", MaxContextTokens = 200_000 },
            UsageBudget = new ExecutionUsageBudget { MaxInputTokens = 1 },
        };
        if (streaming)
        {
            // The streaming executor propagates preflight rejection to its
            // canonical Turn adapter; it never starts a provider stream.
            await Assert.ThrowsExactlyAsync<LlmInputBudgetExceededException>(async () =>
            {
                await foreach (var _ in harness.ExecutionService.ExecuteStreamAsync(request)) { }
            });
        }
        else
        {
            var result = await harness.ExecutionService.ExecuteAsync(request);
            Assert.IsFalse(result.IsSuccess);
        }
        Assert.AreEqual(0, harness.Llm.CallCount);
        Assert.AreEqual(0, harness.Tools.Captured.Count);
    }
}

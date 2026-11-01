using System.Text.Json;
using PuddingCode.Platform;
using PuddingRuntime.Services.AgentLoop;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntimeTests.Services.AgentLoop;

[TestClass]
public sealed class ContextHandoffHookTests
{
    private static AgentLoopContext Context(string session = "s", int? limit = 10000) => new()
    {
        SessionId = session, WorkspaceId = "w", AgentInstanceId = "a", AgentTemplateId = "t",
        UserMessage = "continue", MaxRounds = 20, EffectiveInputLimit = limit,
    };
    private static SkillResult Result(string text = "output", bool success = true) => new()
    {
        Success = success, Output = text, ExitCode = 17, Metadata = new() { ["pid"] = "p1" },
    };
    private static ContextUsageSnapshotStore Usage(int tokens = 5000)
    {
        var store = new ContextUsageSnapshotStore();
        store.Set(new ContextUsageSnapshot { SessionId = "s", UsedTokens = tokens });
        return store;
    }
    private sealed class HandoffTestClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [TestMethod]
    public async Task PressureReminder_PreservesToolResultAndDoesNotRepeatAcrossTurns()
    {
        var clock = new HandoffTestClock();
        var hook = new ContextHandoffHook(Usage(), clock);
        var original = Result();
        var first = await hook.OnToolResultTransformingAsync(Context(), 0, "file_read", "{}", original);
        StringAssert.Contains(first.Output, "context_handoff");
        StringAssert.StartsWith(first.Output, original.Output);
        Assert.AreEqual(original.Success, first.Success);
        Assert.AreEqual(original.ExitCode, first.ExitCode);
        Assert.AreSame(original.Metadata, first.Metadata);
        Assert.AreSame(original, await hook.OnToolResultTransformingAsync(Context(), 0, "file_read", "{}", original));
        clock.Now = clock.Now.AddMinutes(30);
        Assert.AreNotSame(original, await hook.OnToolResultTransformingAsync(Context(), 0, "file_read", "{}", original));
    }

    [TestMethod]
    public async Task JsonObservation_RemainsParseableWithExistingFieldsIntact()
    {
        var hook = new ContextHandoffHook(Usage());
        var original = Result("""{"pid":"p1","lines":["alpha","beta"],"exitCode":0}""");
        var result = await hook.OnToolResultTransformingAsync(Context(), 0, "terminal_wait", "{}", original);
        var json = JsonDocument.Parse(result.Output).RootElement;
        Assert.AreEqual("p1", json.GetProperty("pid").GetString());
        Assert.AreEqual(2, json.GetProperty("lines").GetArrayLength());
        Assert.AreEqual(0, json.GetProperty("exitCode").GetInt32());
        StringAssert.Contains(json.GetProperty("_context_handoff_advice").GetString()!, "context_handoff");
    }

    [TestMethod]
    public async Task LowUnknownOrFailedUsage_DoesNotInjectAdvice()
    {
        var original = Result();
        Assert.AreSame(original, await new ContextHandoffHook(Usage(4999)).OnToolResultTransformingAsync(Context(), 0, "file_read", "{}", original));
        Assert.AreSame(original, await new ContextHandoffHook(Usage()).OnToolResultTransformingAsync(Context(limit: null), 0, "file_read", "{}", original));
        Assert.AreSame(original, await new ContextHandoffHook(Usage()).OnToolResultTransformingAsync(Context("other"), 0, "file_read", "{}", original));
        var failed = Result(success: false);
        Assert.AreSame(failed, await new ContextHandoffHook(Usage()).OnToolResultTransformingAsync(Context(), 0, "file_read", "{}", failed));
    }

    [TestMethod]
    [DataRow("search_tools", "{\"tools\":[]}")]
    [DataRow("terminal_execute", "process-id")]
    [DataRow("goal_update", "updated")]
    [DataRow("file_read", "[1,2,3]")]
    public async Task ProtocolAndNonObjectResults_AreNotRewritten(string tool, string output)
    {
        var original = Result(output);
        Assert.AreSame(original, await new ContextHandoffHook(Usage()).OnToolResultTransformingAsync(Context(), 0, tool, "{}", original));
    }

    [TestMethod]
    public async Task LargeOutput_IsNotReparsedOrExpanded_AndDoesNotConsumeReminder()
    {
        var hook = new ContextHandoffHook(Usage());
        var large = Result(new string('x', 20_000));
        Assert.AreSame(large, await hook.OnToolResultTransformingAsync(Context(), 0, "file_read", "{}", large));
        var small = Result();
        Assert.AreNotSame(small, await hook.OnToolResultTransformingAsync(Context(), 1, "file_read", "{}", small));
    }

    [TestMethod]
    public async Task SuccessfulSaveSuppressesReminder_ButFailedSaveDoesNot()
    {
        var hook = new ContextHandoffHook(Usage());
        var original = Result();
        await hook.OnToolResultTransformingAsync(Context(), 0, "context_handoff", "{}", Result("failed", false));
        Assert.AreNotSame(original, await hook.OnToolResultTransformingAsync(Context(), 1, "file_read", "{}", original));
        var newHook = new ContextHandoffHook(Usage());
        await newHook.OnToolResultTransformingAsync(Context(), 0, "context_handoff", "{}", Result("{ \"status\": \"saved\" }"));
        Assert.AreSame(original, await newHook.OnToolResultTransformingAsync(Context(), 1, "file_read", "{}", original));
    }
}

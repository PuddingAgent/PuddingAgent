using PuddingCode.Runtime;

namespace PuddingCoreTests.Runtime;

[TestClass]
public sealed class ExecutionProgressRegistryTests
{
    [TestMethod]
    public void ChildProgress_UpdatesConversationRoot_AndDuplicateMeaningfulFingerprintDoesNotRenew()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero));
        var registry = new ExecutionProgressRegistry(clock);
        registry.RegisterRoot("root-run", "conversation-1");

        clock.Advance(TimeSpan.FromMinutes(5));
        registry.Report(Signal(
            runId: "child-run",
            parentRunId: "root-run",
            ExecutionProgressKind.Meaningful,
            stage: "tool.completed:file_read",
            fingerprint: "same-result"));

        var first = registry.GetSnapshot("root-run")!;
        Assert.AreEqual(clock.GetUtcNow(), first.LastMeaningfulProgressAtUtc);
        Assert.AreEqual(1L, first.MeaningfulSequence);

        clock.Advance(TimeSpan.FromMinutes(5));
        registry.Report(Signal(
            runId: "child-run",
            parentRunId: "root-run",
            ExecutionProgressKind.Meaningful,
            stage: "tool.completed:file_read",
            fingerprint: "same-result"));

        var duplicate = registry.GetSnapshot("root-run")!;
        Assert.AreEqual(first.LastMeaningfulProgressAtUtc, duplicate.LastMeaningfulProgressAtUtc);
        Assert.AreEqual(1L, duplicate.MeaningfulSequence);
        Assert.AreEqual(clock.GetUtcNow(), duplicate.LastLivenessAtUtc);
        Assert.AreEqual(2L, duplicate.LivenessSequence);

        clock.Advance(TimeSpan.FromMinutes(1));
        registry.Report(Signal(
            runId: "child-run",
            parentRunId: "root-run",
            ExecutionProgressKind.Meaningful,
            stage: "tool.completed:file_read",
            fingerprint: "new-result"));

        var advanced = registry.GetSnapshot("root-run")!;
        Assert.AreEqual(clock.GetUtcNow(), advanced.LastMeaningfulProgressAtUtc);
        Assert.AreEqual(2L, advanced.MeaningfulSequence);
    }

    [TestMethod]
    public void Liveness_DoesNotRenewMeaningfulProgress_AndUnregisterRemovesRoot()
    {
        var startedAt = new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(startedAt);
        var registry = new ExecutionProgressRegistry(clock);
        registry.RegisterRoot("root-run", "conversation-1");

        clock.Advance(TimeSpan.FromMinutes(10));
        registry.Report(Signal(
            runId: "root-run",
            parentRunId: null,
            ExecutionProgressKind.Liveness,
            stage: "llm.streaming",
            fingerprint: null));

        var snapshot = registry.GetSnapshot("root-run")!;
        Assert.AreEqual(startedAt, snapshot.LastMeaningfulProgressAtUtc);
        Assert.AreEqual(clock.GetUtcNow(), snapshot.LastLivenessAtUtc);

        registry.UnregisterRoot("root-run");
        Assert.IsNull(registry.GetSnapshot("root-run"));
    }

    private static ExecutionProgressSignal Signal(
        string runId,
        string? parentRunId,
        ExecutionProgressKind kind,
        string stage,
        string? fingerprint)
        => new()
        {
            Identity = new RuntimeExecutionIdentity
            {
                Kind = parentRunId is null
                    ? RuntimeExecutionKind.ConversationTurn
                    : RuntimeExecutionKind.SubAgent,
                ConversationId = "conversation-1",
                RunId = runId,
                ParentRunId = parentRunId,
            },
            Kind = kind,
            Stage = stage,
            Fingerprint = fingerprint,
        };

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }
}

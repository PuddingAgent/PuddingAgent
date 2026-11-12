using Pudding.ToolPlugins.AgentSdk;
using SamplePlugin = Pudding.Samples.Plugins.ReviewAudit.ReviewAuditPlugin;

[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.MethodLevel)]

namespace ReviewAuditPlugin.Tests;

[TestClass]
public sealed class ReviewAuditPluginTests
{
    private static readonly DateTimeOffset OccurredAt = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task HandleAsync_ReviewCompleted_MapsSixFieldsVerbatim()
    {
        var payload = Payload(reviewId: "评审-甲", finding: "发现\"引号\"/反斜杠\\",
            evidenceLevel: "Verified", reasonCode: "Goal.Custom.Reason-中文");
        var envelope = Envelope(payload: payload);
        var audit = new FakePluginAuditApi();

        await new SamplePlugin().HandleAsync(envelope, new FakePluginEventContext(audit));

        Assert.AreEqual(1, audit.RecordCalls);
        var record = audit.Records.Single();
        Assert.AreEqual(envelope.EventId, record.OperationId, "OperationId must be the event id.");
        Assert.AreEqual(envelope.EventId, record.EventId);
        Assert.AreEqual(payload.ReviewId, record.ReviewId);
        Assert.AreEqual(payload.Finding, record.Finding);
        Assert.AreEqual(payload.EvidenceLevel, record.EvidenceLevel);
        Assert.AreEqual(payload.ReasonCode, record.ReasonCode);
        Assert.AreEqual(
            "review.completed event=event-1 review=评审-甲 finding=发现\"引号\"/反斜杠\\ evidence=Verified reason=Goal.Custom.Reason-中文",
            SamplePlugin.FormatForTest(envelope));
    }

    [TestMethod]
    public async Task HandleAsync_BlankReasonCode_FormatsNone_AndKeepsRecordValue()
    {
        foreach (var reasonCode in new string?[] { null, "", "   " })
        {
            var payload = Payload(reasonCode: reasonCode);
            var envelope = Envelope(payload: payload);
            var audit = new FakePluginAuditApi();

            await new SamplePlugin().HandleAsync(envelope, new FakePluginEventContext(audit));

            Assert.AreEqual(1, audit.RecordCalls);
            Assert.AreEqual(reasonCode, audit.Records.Single().ReasonCode,
                "The record maps ReasonCode verbatim; only the audit text substitutes 无.");
            Assert.AreEqual(
                "review.completed event=event-1 review=review-1 finding=Finding evidence=Verified reason=无",
                SamplePlugin.FormatForTest(envelope));
        }
    }

    [TestMethod]
    public async Task HandleAsync_NonReviewEvent_DoesNotWrite()
    {
        foreach (var kind in new[] { "review.started", "tool.completed", "Review.completed", "review.completed " })
        {
            var envelope = Envelope(kind: kind);
            var audit = new FakePluginAuditApi();

            await new SamplePlugin().HandleAsync(envelope, new FakePluginEventContext(audit));

            Assert.AreEqual(0, audit.RecordCalls, $"Kind '{kind}' must not produce an audit write.");
        }
    }

    [TestMethod]
    public async Task HandleAsync_MissingReviewCompletedPayload_DoesNotWrite()
    {
        var envelope = new PluginEventEnvelope("event-1", "stream-1", 7, "review.completed", 1, OccurredAt,
            "workspace-1", "payload://ref", "hash-1", "producer-1");
        var audit = new FakePluginAuditApi();

        await new SamplePlugin().HandleAsync(envelope, new FakePluginEventContext(audit));

        Assert.AreEqual(0, audit.RecordCalls);
    }

    [TestMethod]
    public async Task HandleAsync_RepeatedEvent_UsesSameOperationId()
    {
        var envelope = Envelope();
        var audit = new FakePluginAuditApi();
        var plugin = new SamplePlugin();
        var context = new FakePluginEventContext(audit);

        await plugin.HandleAsync(envelope, context);
        await plugin.HandleAsync(envelope, context);

        Assert.AreEqual(2, audit.RecordCalls, "The plugin itself does not dedupe; the host does by OperationId.");
        CollectionAssert.AreEqual(new[] { envelope.EventId, envelope.EventId },
            audit.Records.Select(record => record.OperationId).ToArray());
        Assert.IsFalse(audit.Records.Any(record => record.OperationId != envelope.EventId),
            "A replayed event must never get a fresh GUID.");
    }

    [TestMethod]
    public async Task HandleAsync_PreCancelled_DoesNotWrite()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var envelope = Envelope();
        var audit = new FakePluginAuditApi();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            new SamplePlugin().HandleAsync(envelope, new FakePluginEventContext(audit), cancellation.Token));
        Assert.AreEqual(0, audit.RecordCalls, "A pre-cancelled call must not write an audit record.");
    }

    [TestMethod]
    public async Task HandleAsync_RecordCancellation_IsNotSwallowed()
    {
        var audit = new FakePluginAuditApi { Failure = new OperationCanceledException("record cancelled") };
        var envelope = Envelope();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            new SamplePlugin().HandleAsync(envelope, new FakePluginEventContext(audit), CancellationToken.None));
        Assert.AreEqual(1, audit.RecordCalls);
    }

    [TestMethod]
    public async Task HandleAsync_RecordFailure_IsNotSwallowedOrConvertedToSuccess()
    {
        var audit = new FakePluginAuditApi
        {
            Failure = new PluginEventsException("audit.write.denied", "denied by host")
        };
        var envelope = Envelope();

        var exception = await Assert.ThrowsExactlyAsync<PluginEventsException>(() =>
            new SamplePlugin().HandleAsync(envelope, new FakePluginEventContext(audit), CancellationToken.None));
        Assert.AreEqual("audit.write.denied", exception.Code);
        Assert.AreEqual(1, audit.RecordCalls, "A failed audit write must not be retried with a new operation id.");
    }

    [TestMethod]
    public async Task HandleAsync_UsesOnlyTheAuditSink()
    {
        var audit = new FakePluginAuditApi();
        var context = new FakePluginEventContext(audit);

        await new SamplePlugin().HandleAsync(Envelope(), context);

        Assert.AreEqual(1, context.AuditReads, "The plugin must only use the bound audit sink.");
        Assert.AreEqual(0, context.PluginIdReads);
        Assert.AreEqual(0, context.GenerationReads);
        Assert.AreEqual(0, context.WorkspaceIdReads);
        Assert.AreEqual(1, audit.RecordCalls);
    }

    [TestMethod]
    public void PluginAssembly_ReferencesOnlyTheAgentSdkSurface()
    {
        var referencedPuddingAssemblies = typeof(SamplePlugin).Assembly.GetReferencedAssemblies()
            .Select(assembly => assembly.Name ?? string.Empty)
            .Where(name => name.StartsWith("Pudding", StringComparison.Ordinal))
            .ToArray();

        CollectionAssert.AreEquivalent(new[] { "Pudding.ToolPlugins.AgentSdk" }, referencedPuddingAssemblies,
            "The sample must not depend on Host/Runtime/Platform/Messaging assemblies.");
    }

    [TestMethod]
    public void FormatForTest_NonReviewEvent_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => SamplePlugin.FormatForTest(Envelope(kind: "review.started")));
        Assert.ThrowsExactly<ArgumentException>(() => SamplePlugin.FormatForTest(new PluginEventEnvelope(
            "event-1", "stream-1", 7, "review.completed", 1, OccurredAt, "workspace-1", "payload://ref", "hash-1",
            "producer-1")));
    }

    [TestMethod]
    public async Task HandleAsync_NullArguments_ThrowArgumentNullException()
    {
        var audit = new FakePluginAuditApi();
        var context = new FakePluginEventContext(audit);

        var envelopeException = await Assert.ThrowsExactlyAsync<ArgumentNullException>(() =>
            new SamplePlugin().HandleAsync(null!, context));
        Assert.AreEqual("envelope", envelopeException.ParamName);

        var contextException = await Assert.ThrowsExactlyAsync<ArgumentNullException>(() =>
            new SamplePlugin().HandleAsync(Envelope(), null!));
        Assert.AreEqual("context", contextException.ParamName);

        Assert.AreEqual(0, audit.RecordCalls);
    }

    private static PluginEventEnvelope Envelope(
        string kind = "review.completed",
        string eventId = "event-1",
        ReviewCompletedEventPayload? payload = null) =>
        new(eventId, "stream-1", 7, kind, 1, OccurredAt, "workspace-1", "payload://ref", "hash-1", "producer-1",
            payload ?? Payload());

    private static ReviewCompletedEventPayload Payload(
        string reviewId = "review-1",
        string finding = "Finding",
        string evidenceLevel = "Verified",
        string? reasonCode = "goal.reason") =>
        new(reviewId, "review-job-1", finding, evidenceLevel, reasonCode, 3);

    private sealed class FakePluginEventContext(IPluginAuditApi audit) : IPluginEventContext
    {
        public int PluginIdReads { get; private set; }
        public int GenerationReads { get; private set; }
        public int WorkspaceIdReads { get; private set; }
        public int AuditReads { get; private set; }

        public string PluginId
        {
            get
            {
                PluginIdReads++;
                return "review-audit";
            }
        }

        public long Generation
        {
            get
            {
                GenerationReads++;
                return 3;
            }
        }

        public string WorkspaceId
        {
            get
            {
                WorkspaceIdReads++;
                return "workspace-1";
            }
        }

        public IPluginAuditApi Audit
        {
            get
            {
                AuditReads++;
                return audit;
            }
        }
    }

    private sealed class FakePluginAuditApi : IPluginAuditApi
    {
        public int RecordCalls { get; private set; }
        public List<PluginAuditRecord> Records { get; } = [];
        public Exception? Failure { get; init; }

        public Task<PluginAuditReceipt> RecordAsync(PluginAuditRecord record, CancellationToken ct = default)
        {
            RecordCalls++;
            Records.Add(record);
            ct.ThrowIfCancellationRequested();
            return Failure is null
                ? Task.FromResult(new PluginAuditReceipt("audit-1", record.OperationId, false))
                : Task.FromException<PluginAuditReceipt>(Failure);
        }
    }
}

using Pudding.ToolPlugins.AgentSdk;

namespace Pudding.ToolPlugins.AgentSdkTests;

/// <summary>PA-07A frozen plugin event/audit contract: envelope validity and the OperationId=EventId audit rule.</summary>
[TestClass]
public sealed class PluginEventObserverTests
{
    [TestMethod]
    public void Envelope_RequiresReviewPayloadOfACommittedReviewEvent()
    {
        var envelope = new PluginEventEnvelope("event-1", "ws-1", 3, "review.completed", 1, DateTimeOffset.UtcNow,
            "ws-1", "ref://review", "sha256-review", "canonical",
            new ReviewCompletedEventPayload("review-1", "job-1", "Satisfied", "CheckVerified", null, 4));
        Assert.IsTrue(envelope.IsValid);
        var invalid = envelope with { EventId = "" };
        Assert.IsFalse(invalid.IsValid);
        var missingPayload = envelope with { ReviewCompleted = null };
        Assert.IsTrue(missingPayload.IsValid, "A generic event may carry no review payload; the plugin only filters review.completed.");
    }

    [TestMethod]
    public void AuditRecord_UsesTheEventIdAsTheStableOperationId()
    {
        var envelope = new PluginEventEnvelope("event-1", "ws-1", 3, "review.completed", 1, DateTimeOffset.UtcNow,
            "ws-1", "ref://review", "sha256-review", "canonical",
            new ReviewCompletedEventPayload("review-1", "job-1", "Satisfied", "CheckVerified", null, 4));
        var record = new PluginAuditRecord(envelope.EventId, envelope.EventId, envelope.ReviewCompleted!.ReviewId,
            envelope.ReviewCompleted.Finding, envelope.ReviewCompleted.EvidenceLevel,
            envelope.ReviewCompleted.ReasonCode);
        Assert.IsTrue(record.IsValid);
        Assert.AreEqual(envelope.EventId, record.OperationId);
        Assert.IsFalse((record with { OperationId = "" }).IsValid, "The host-bound OperationId cannot be blank.");
    }

    [TestMethod]
    public void AuditReceipt_ReportsReplayWithoutChangingTheLogicalAudit()
    {
        var receipt = new PluginAuditReceipt("audit-1", "event-1", Replayed: true);
        Assert.IsTrue(receipt.Replayed);
        Assert.AreEqual("event-1", receipt.OperationId);
    }
}

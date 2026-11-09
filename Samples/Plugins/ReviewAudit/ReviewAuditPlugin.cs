using Pudding.ToolPlugins.AgentSdk;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ReviewAuditPlugin.Tests")]

namespace Pudding.Samples.Plugins.ReviewAudit;

/// <summary>
/// Offline, read-only observer sample: it formats one already-authorized <c>review.completed</c> event into a
/// single scoped audit record and writes it through the host-bound audit sink. It never sends messages,
/// invokes tools, executes MCP, starts a Review/Goal, subscribes to its own audit output, keeps state/cache/
/// timers or reads any host service. The stable <c>OperationId</c> is always the event id, so replaying the
/// same event produces one logical record on the host side.
/// </summary>
public sealed class ReviewAuditPlugin : IPluginEventObserver
{
    private const string ReviewCompletedKind = "review.completed";
    private const string MissingReasonCode = "无";

    public async Task HandleAsync(PluginEventEnvelope envelope, IPluginEventContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(context);

        // Anything that is not a review.completed event with a payload is ignored: no record, no log, no throw.
        if (envelope.Kind != ReviewCompletedKind || envelope.ReviewCompleted is not { } payload) return;

        // A caller that cancelled before the write must not produce an audit record.
        ct.ThrowIfCancellationRequested();

        // The host dedupes by OperationId, so the event id is the stable operation id, never a fresh GUID.
        var record = new PluginAuditRecord(
            envelope.EventId,
            envelope.EventId,
            payload.ReviewId,
            payload.Finding,
            payload.EvidenceLevel,
            payload.ReasonCode);
        await context.Audit.RecordAsync(record, ct).ConfigureAwait(false);
    }

    /// <summary>Formatting helper used by tests to assert the frozen one-line audit text.</summary>
    internal static string FormatForTest(PluginEventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Kind != ReviewCompletedKind || envelope.ReviewCompleted is not { } payload)
        {
            throw new ArgumentException("Only a review.completed envelope with a payload has an audit text.",
                nameof(envelope));
        }

        var reasonCode = string.IsNullOrWhiteSpace(payload.ReasonCode) ? MissingReasonCode : payload.ReasonCode;
        return $"review.completed event={envelope.EventId} review={payload.ReviewId} finding={payload.Finding} " +
               $"evidence={payload.EvidenceLevel} reason={reasonCode}";
    }
}

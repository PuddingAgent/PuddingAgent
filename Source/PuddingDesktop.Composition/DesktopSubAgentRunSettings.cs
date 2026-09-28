using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Diagnostics;
using PuddingDesktop.Foundation;
using PuddingPlatform.Services;

namespace PuddingDesktop.Composition;

/// <summary>
/// DS-14 subagent-runs slice: reads run listings through the application operation sunk out of
/// SubAgentRunController, and the archive-backed detail/events it also owns.
/// </summary>
internal sealed class DesktopSubAgentRunSettings(IDesktopKernel kernel) : ISubAgentRunSettings
{
    public Task<SubAgentRunPage> ListAsync(SubAgentRunFilter filter, CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("subAgentRuns.list", async (scope, token) =>
        {
            var normalized = SubAgentRunText.Normalize(filter);
            var page = await scope.Services.GetRequiredService<SubAgentRunQueryService>().ListAsync(
                Empty(normalized.ParentSessionId), Empty(normalized.WorkspaceId), Empty(normalized.AgentInstanceId),
                Empty(normalized.Status), normalized.Limit, normalized.Offset, token);
            return new SubAgentRunPage(page.Items.Select(Map).ToArray(), page.Total, page.Offset, page.Limit);
        }, cancellationToken);

    public Task<SubAgentRunDetail?> GetAsync(string runId, CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("subAgentRuns.get", async (scope, token) =>
        {
            var detail = await scope.Services.GetRequiredService<SubAgentRunQueryService>().GetAsync(runId, token);
            if (detail is null) return (SubAgentRunDetail?)null;
            return new SubAgentRunDetail(
                Map(detail.Summary), detail.Task ?? "", detail.Output ?? "",
                detail.LlmProfiles, detail.Trace, detail.EventCount, detail.ToolCallCount,
                detail.ArchiveDegraded is { } degraded
                    ? SubAgentRunText.DescribeDegraded(
                        degraded.FirstFailureAt, degraded.LastError, degraded.DroppedEventCount)
                    : "");
        }, cancellationToken);

    public Task<IReadOnlyList<SubAgentRunEvent>> ListEventsAsync(
        string runId, int limit, int offset, CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("subAgentRuns.events", async (scope, token) =>
        {
            var page = await scope.Services.GetRequiredService<SubAgentRunQueryService>()
                .EventsAsync(runId, SubAgentRunText.ClampLimit(limit), Math.Max(0, offset), token);
            return (IReadOnlyList<SubAgentRunEvent>)(page?.Items.Select(item => new SubAgentRunEvent(
                item.EventId, item.EventType, item.Timestamp, item.PayloadSize,
                item.PayloadPreview ?? "")).ToArray() ?? []);
        }, cancellationToken);

    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static SubAgentRun Map(SubAgentRunSummaryDto summary) => new(
        summary.RunId, summary.ParentSessionId, summary.SubSessionId, summary.WorkspaceId,
        summary.AgentInstanceId, summary.TemplateId, summary.Status,
        summary.StartedAt ?? "", summary.CompletedAt ?? "", summary.TotalDurationMs,
        summary.TotalRounds, summary.TotalToolCalls, summary.ErrorMessage ?? "");
}

using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Diagnostics;
using PuddingDesktop.Foundation;
using PuddingPlatform.Services.Diagnostics;

namespace PuddingDesktop.Composition;

/// <summary>
/// DS-14 diagnostics slice: reads the timeline and component health through Core's shared query operation,
/// so the shell receives the same redacted payload the HTTP surface returns.
/// </summary>
internal sealed class DesktopDiagnosticsSettings(IDesktopKernel kernel) : IDiagnosticsSettings
{
    public Task<RuntimeTimelinePage> QueryTimelineAsync(
        RuntimeTimelineFilter filter, CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("diagnostics.timeline", async (scope, token) =>
        {
            var normalized = DiagnosticsText.Normalize(filter);
            var page = await scope.Services.GetRequiredService<RuntimeDiagnosticsQueryService>()
                .QueryTimelineAsync(new RuntimeTimelineQueryDto
                {
                    SessionId = Empty(normalized.SessionId),
                    RunId = Empty(normalized.RunId),
                    TraceId = Empty(normalized.TraceId),
                    AgentInstanceId = Empty(normalized.AgentInstanceId),
                    Component = Empty(normalized.Component),
                    Status = Empty(normalized.Status),
                    Page = normalized.Page,
                    PageSize = normalized.PageSize,
                    SortOrder = normalized.SortOrder,
                    DisplayMode = normalized.DisplayMode,
                }, token);
            return new RuntimeTimelinePage(page.Items.Select(Map).ToArray(), page.Page, page.PageSize, page.Total);
        }, cancellationToken);

    public Task<DiagnosticsOverview> LoadOverviewAsync(CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("diagnostics.overview", async (scope, token) =>
        {
            var service = scope.Services.GetRequiredService<RuntimeDiagnosticsQueryService>();
            var health = await service.GetComponentHealthAsync(token);
            // 概览里的「近期失败」复用同一条已脱敏查询路径。
            var failures = await service.QueryTimelineAsync(new RuntimeTimelineQueryDto
            {
                Status = "failed", Page = 1, PageSize = 20, SortOrder = "desc",
            }, token);
            return new DiagnosticsOverview(
                health.Select(component => new RuntimeComponentHealth(
                    component.Component, component.Status, component.StartedCount, component.SucceededCount,
                    component.FailedCount, component.RetriedCount, component.CancelledCount,
                    ParseTimestamp(component.LastSeenAtUtc))).ToArray(),
                new RuntimeTimelinePage(failures.Items.Select(Map).ToArray(),
                    failures.Page, failures.PageSize, failures.Total));
        }, cancellationToken);

    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static RuntimeTimelineEntry Map(RuntimeTimelineItemDto item) => new(
        item.Id, item.Kind, item.Component, item.Operation, item.Status,
        item.SessionId ?? "", item.AgentInstanceId ?? "", item.RunId ?? "", item.TraceId ?? "",
        item.CorrelationId ?? "", item.StartedAtUtc, item.CompletedAtUtc, item.DurationMs,
        item.Summary ?? "", item.Error ?? "",
        (IReadOnlyDictionary<string, string>?)item.Metadata ?? new Dictionary<string, string>());

    private static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
}

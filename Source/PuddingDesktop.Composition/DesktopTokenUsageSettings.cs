using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Platform;
using PuddingDesktop.Foundation;
using PuddingPlatform.Services;

namespace PuddingDesktop.Composition;

/// <summary>
/// DS-15 usage slice: reads the two usage ledgers through Core's daily aggregate (day windows, cached for
/// closed days) and the filtered event page. The event query's date range is deliberately unused - Core's
/// repository cannot translate a DateTimeOffset comparison on SQLite, which its own comment documents.
/// </summary>
internal sealed class DesktopTokenUsageSettings(IDesktopKernel kernel) : ITokenUsageSettings
{
    public Task<TokenUsageSummary> LoadSummaryAsync(
        TokenUsageWindow window, CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("tokenUsage.summary", async (scope, token) =>
        {
            var aggregates = scope.Services.GetRequiredService<TokenUsageDailyAggregateService>();
            var today = DateTime.UtcNow.Date;
            var endExclusive = window.EndUtcDateExclusive > today ? today : window.EndUtcDateExclusive;

            var rows = new List<TokenUsageRow>();
            var closed = await aggregates.GetClosedDaysAsync(window.StartUtcDate, endExclusive, token);
            rows.AddRange(closed.Select(Map));

            // 当前 UTC 日走实时聚合（不落缓存）；闭日走缓存，两者口径一致。
            var includesLiveToday = window.StartUtcDate <= today && today < window.EndUtcDateExclusive;
            if (includesLiveToday) rows.AddRange((await aggregates.GetLiveTodayAsync(token)).Select(Map));

            return new TokenUsageSummary(window, rows, includesLiveToday, DateTimeOffset.UtcNow);
        }, cancellationToken);

    public Task<TokenUsageLedgerPage> ListEventsAsync(
        TokenUsageEventFilter filter, CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("tokenUsage.events", async (scope, token) =>
        {
            var normalized = TokenUsageText.Normalize(filter);
            // from/to 不传：Core 的实现无法在 SQLite 上翻译 DateTimeOffset 比较（见其自身注释）。
            // 宿主只注册了接口 ITokenUsageEventRepository（具体类型未注册），按接口解析。
            var page = await scope.Services.GetRequiredService<ITokenUsageEventRepository>().GetFilteredAsync(
                Empty(normalized.WorkspaceId), Empty(normalized.SessionId), Empty(normalized.ProviderId),
                Empty(normalized.ModelId), null, null, normalized.Page, normalized.PageSize, token);
            return new TokenUsageLedgerPage(
                page.Events.Select(row => new TokenUsageEvent(
                    row.Id, row.WorkspaceId ?? "", row.SessionId ?? "", row.ProviderId, row.ModelId,
                    row.PromptTokens, row.CompletionTokens, row.TotalTokens,
                    row.CacheHitTokens ?? -1, row.CacheMissTokens ?? -1, row.SourceType, row.OccurredAt)).ToArray(),
                page.TotalCount, page.Page, page.PageSize);
        }, cancellationToken);

    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static TokenUsageRow Map(LlmUsageDailyAggregateRow row) => new(
        row.DayUtc, row.Source, row.ProviderId, row.ModelId,
        row.PromptTokens, row.CompletionTokens, row.CacheHitTokens, row.CacheMissTokens, row.RequestCount,
        row.InputCost, row.CacheHitCost, row.OutputCost, row.TotalCost);
}

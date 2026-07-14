using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// 会话级 prefix cache 诊断服务。基于 TokenUsageEvents 判断缓存收益和 prefix churn 来源。
/// </summary>
public sealed class CacheDiagnosticsService(PlatformDbContext db) : ICacheDiagnosticsService
{
    public async Task<CacheDiagnosticsReport> GetSessionReportAsync(
        string sessionId,
        int limit = 50,
        CancellationToken ct = default)
    {
        var normalizedLimit = Math.Clamp(limit, 1, 200);
        var events = await db.TokenUsageEvents.AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            // SQLite cannot translate DateTimeOffset ordering; Id is monotonic for this append-only ledger.
            .OrderByDescending(e => e.Id)
            .Take(normalizedLimit)
            .ToListAsync(ct);

        var chronological = events
            .OrderBy(e => e.OccurredAtUtc)
            .ThenBy(e => e.Id)
            .ToList();
        var prefixHashes = chronological
            .Select(e => e.PrefixHash)
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var firstChurn = FindFirstChurn(chronological);
        var totalEligible = events.Sum(e => e.CacheEligibleTokens);
        var totalHit = events.Sum(e => e.CacheHitTokens);
        var averageHitRate = totalEligible > 0
            ? Math.Round((double)totalHit / totalEligible, 6)
            : (double?)null;

        var status = ResolveStatus(events.Count, prefixHashes.Count, firstChurn, averageHitRate);
        return new CacheDiagnosticsReport(
            SessionId: sessionId,
            AnalyzedEventCount: events.Count,
            DistinctPrefixHashCount: prefixHashes.Count,
            Status: status,
            AverageCacheHitRate: averageHitRate,
            CacheHitTokens: totalHit,
            CacheMissTokens: events.Sum(e => e.CacheMissTokens),
            CacheEligibleTokens: totalEligible,
            FirstChurnAtUtc: firstChurn?.OccurredAtUtc,
            FirstChurnReason: firstChurn?.PrefixChangeReason,
            FirstChurnSource: ResolveChurnSource(firstChurn),
            Turns: chronological.Select(ToTurn).ToList());
    }

    private static TokenUsageEventEntity? FindFirstChurn(IReadOnlyList<TokenUsageEventEntity> events)
    {
        string? initialHash = null;
        foreach (var ev in events)
        {
            if (string.IsNullOrWhiteSpace(ev.PrefixHash))
                continue;

            initialHash ??= ev.PrefixHash;
            if (!string.Equals(initialHash, ev.PrefixHash, StringComparison.Ordinal))
                return ev;
}


        return null;
    }

    private static string ResolveStatus(
        int eventCount,
        int distinctPrefixHashCount,
        TokenUsageEventEntity? firstChurn,
        double? averageHitRate)
    {
        if (eventCount == 0)
            return "no_events";
        if (distinctPrefixHashCount == 0)
            return "no_prefix_data";
        if (distinctPrefixHashCount > 1)
            return string.IsNullOrWhiteSpace(firstChurn?.PrefixChangeReason)
                ? "unexpected_churn"
                : "expected_churn";
        if (averageHitRate is not null && averageHitRate < 0.25)
            return "stable_low_hit_rate";
        return "stable";
    }

    private static string? ResolveChurnSource(TokenUsageEventEntity? ev)
    {
        if (ev is null)
            return null;
        if (!string.IsNullOrWhiteSpace(ev.PrefixChangeReason))
            return ev.PrefixChangeReason;
        if (!string.IsNullOrWhiteSpace(ev.ToolSpecHash))
            return "prefix_hash_changed";
        return "unknown";
    }

    private static CacheDiagnosticsTurn ToTurn(TokenUsageEventEntity ev) => new(
        SourceType: ev.SourceType,
        SourceId: ev.SourceId,
        OccurredAtUtc: ev.OccurredAtUtc,
        ProviderId: ev.ProviderId,
        ModelId: ev.ModelId,
        PrefixHash: ev.PrefixHash,
        SystemPromptHash: ev.SystemPromptHash,
        ToolSpecHash: ev.ToolSpecHash,
        PrefixChangeReason: ev.PrefixChangeReason,
        CacheHitTokens: ev.CacheHitTokens,
        CacheMissTokens: ev.CacheMissTokens,
        CacheHitRate: ev.CacheHitRate,
        TotalCost: ev.TotalCost);
}


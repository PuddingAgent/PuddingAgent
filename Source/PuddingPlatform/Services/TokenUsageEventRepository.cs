using Microsoft.EntityFrameworkCore;
using PuddingCode.Platform;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services;

/// <summary>
/// EF Core implementation of ITokenUsageEventRepository.
/// </summary>
public sealed class TokenUsageEventRepository : ITokenUsageEventRepository
{
    private readonly IDbContextFactory<PlatformDbContext> _dbFactory;

    public TokenUsageEventRepository(IDbContextFactory<PlatformDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<SessionTokenStats?> GetLatestStatsAsync(string sessionId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var latest = await db.TokenUsageEvents
            .AsNoTracking()
            .Where(ev => ev.SessionId == sessionId && ev.PromptTokens > 0)
            .OrderByDescending(ev => ev.OccurredAtUtc)
            .ThenByDescending(ev => ev.Id)
            .FirstOrDefaultAsync(ct);

        if (latest is null) return null;

        return new SessionTokenStats
        {
            PromptTokens = latest.PromptTokens,
            CompletionTokens = latest.CompletionTokens,
            TotalTokens = latest.TotalTokens,
            OccurredAtUtc = latest.OccurredAtUtc,
            MostRecentEventId = latest.Id,
        };
    }

    public async Task<SessionTokenDiagnostics?> GetLatestLayerDiagnosticsAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.TokenUsageEvents
            .AsNoTracking()
            .Where(e => e.SessionId == sessionId
                && (e.MessageTokens != null
                    || e.ToolDefinitionTokens != null
                    || e.SystemMessageTokens != null
                    || e.HistoryMessageTokens != null))
            .OrderByDescending(e => e.OccurredAtUtc)
            .ThenByDescending(e => e.Id)
            .Select(e => new SessionTokenDiagnostics
            {
                SessionId = e.SessionId,
                OccurredAtUtc = e.OccurredAtUtc,
                MessageTokens = e.MessageTokens,
                ToolDefinitionTokens = e.ToolDefinitionTokens,
                SystemMessageTokens = e.SystemMessageTokens,
                HistoryMessageTokens = e.HistoryMessageTokens,
                PromptTokens = e.PromptTokens,
                CompletionTokens = e.CompletionTokens,
            })
            .FirstOrDefaultAsync(ct);
    }

    public async Task<SessionTokenDiagnostics?> GetLatestEntropyDiagnosticsAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.TokenUsageEvents
            .AsNoTracking()
            .Where(e => e.SessionId == sessionId
                && (e.SystemMessageEntropy != null
                    || e.HistoryMessageEntropy != null
                    || e.ToolDefinitionEntropy != null))
            .OrderByDescending(e => e.OccurredAtUtc)
            .ThenByDescending(e => e.Id)
            .Select(e => new SessionTokenDiagnostics
            {
                SessionId = e.SessionId,
                OccurredAtUtc = e.OccurredAtUtc,
                MessageTokens = e.MessageTokens,
                ToolDefinitionTokens = e.ToolDefinitionTokens,
                SystemMessageEntropy = e.SystemMessageEntropy,
                HistoryMessageEntropy = e.HistoryMessageEntropy,
                ToolDefinitionEntropy = e.ToolDefinitionEntropy,
            })
            .FirstOrDefaultAsync(ct);
    }

    public async Task<TokenUsageEventPage> GetFilteredAsync(
        string? workspaceId = null,
        string? sessionId = null,
        string? providerId = null,
        string? modelId = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.TokenUsageEvents.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(workspaceId)) query = query.Where(e => e.WorkspaceId == workspaceId);
        if (!string.IsNullOrWhiteSpace(sessionId)) query = query.Where(e => e.SessionId == sessionId);
        if (!string.IsNullOrWhiteSpace(providerId)) query = query.Where(e => e.ProviderId == providerId);
        if (!string.IsNullOrWhiteSpace(modelId)) query = query.Where(e => e.ModelId == modelId);
        if (from.HasValue) query = query.Where(e => e.OccurredAtUtc >= from.Value);
        if (to.HasValue) query = query.Where(e => e.OccurredAtUtc <= to.Value);

        var total = await query.CountAsync(ct);
        var events = await query
            .OrderByDescending(e => e.OccurredAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new TokenUsageEventRow
            {
                Id = e.Id,
                WorkspaceId = e.WorkspaceId,
                SessionId = e.SessionId,
                ProviderId = e.ProviderId,
                ModelId = e.ModelId,
                PromptTokens = e.PromptTokens,
                CompletionTokens = e.CompletionTokens,
                TotalTokens = e.TotalTokens,
                CacheHitTokens = e.CacheHitTokens,
                CacheMissTokens = e.CacheMissTokens,
                SourceType = e.SourceType,
                OccurredAt = e.OccurredAtUtc.Ticks,
            })
            .ToListAsync(ct);

        return new TokenUsageEventPage { Events = events, TotalCount = total, Page = page, PageSize = pageSize };
    }
}

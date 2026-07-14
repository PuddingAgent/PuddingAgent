namespace PuddingCode.Abstractions;

/// <summary>
/// Diagnostics report for session-level prefix cache performance.
/// </summary>
public sealed record CacheDiagnosticsReport(
    string SessionId,
    int AnalyzedEventCount,
    int DistinctPrefixHashCount,
    string Status,
    double? AverageCacheHitRate,
    long CacheHitTokens,
    long CacheMissTokens,
    long CacheEligibleTokens,
    DateTimeOffset? FirstChurnAtUtc,
    string? FirstChurnReason,
    string? FirstChurnSource,
    IReadOnlyList<CacheDiagnosticsTurn> Turns);

public sealed record CacheDiagnosticsTurn(
    string SourceType,
    string SourceId,
    DateTimeOffset OccurredAtUtc,
    string? ProviderId,
    string? ModelId,
    string? PrefixHash,
    string? SystemPromptHash,
    string? ToolSpecHash,
    string? PrefixChangeReason,
    long CacheHitTokens,
    long CacheMissTokens,
    double? CacheHitRate,
    decimal TotalCost);

/// <summary>
/// Session-level prefix cache diagnostics.
/// </summary>
public interface ICacheDiagnosticsService
{
    Task<CacheDiagnosticsReport> GetSessionReportAsync(
        string sessionId,
        int limit = 50,
        CancellationToken ct = default);
}

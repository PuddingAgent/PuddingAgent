using PuddingCode.Configuration;
using PuddingFullTextIndex.Contracts;

namespace PuddingRuntime.Services;

/// <summary>
/// Recalls agent-private message logs and daily summaries through the full-text index.
/// </summary>
public sealed class AgentLogRecallService
{
    private readonly PuddingDataPaths _paths;
    private readonly IFullTextSearchEngine _searchEngine;
    private readonly Func<DateTimeOffset> _clock;

    public AgentLogRecallService(
        PuddingDataPaths paths,
        IFullTextSearchEngine searchEngine,
        Func<DateTimeOffset>? clock = null)
    {
        _paths = paths;
        _searchEngine = searchEngine;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public async Task<AgentLogRecallResult> RecallAsync(
        AgentLogRecallRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.AgentInstanceId) || string.IsNullOrWhiteSpace(request.Query))
            return AgentLogRecallResult.Empty;

        var now = _clock();
        var query = request.Query.Trim();
        var messageRoot = _paths.AgentInstanceMessageLogsRoot(request.AgentInstanceId);
        var dailyRoot = _paths.AgentInstanceDailySummaryRoot(request.AgentInstanceId);

        var messageMatches = await SearchDirectoryAsync(
            messageRoot,
            query,
            Math.Max(request.RecentFiveDaysMessageLimit + request.RecentThirtyDaysMessageLimit, 30),
            ct);
        var dailyMatches = await SearchDirectoryAsync(
            dailyRoot,
            query,
            Math.Max(request.RecentDailySummaryLimit, 10),
            ct);

        var recentFiveDaysMessages = messageMatches
            .Select(match => ToRecallMatch(match, AgentLogRecallSource.MessageLog, messageRoot))
            .Where(match => IsWithinDays(match.Day, now, 5))
            .Take(Math.Max(request.RecentFiveDaysMessageLimit, 0))
            .ToList();

        var recentThirtyDaysMessages = messageMatches
            .Select(match => ToRecallMatch(match, AgentLogRecallSource.MessageLog, messageRoot))
            .Where(match => IsWithinDays(match.Day, now, 30))
            .Take(Math.Max(request.RecentThirtyDaysMessageLimit, 0))
            .ToList();

        var recentDailySummaries = dailyMatches
            .Select(match => ToRecallMatch(match, AgentLogRecallSource.DailySummary, dailyRoot))
            .Where(match => IsWithinDays(match.Day, now, 180))
            .Take(Math.Max(request.RecentDailySummaryLimit, 0))
            .ToList();

        return new AgentLogRecallResult(
            recentFiveDaysMessages,
            recentThirtyDaysMessages,
            recentDailySummaries);
    }

    private async Task<IReadOnlyList<FullTextSearchMatch>> SearchDirectoryAsync(
        string directory,
        string query,
        int maxResults,
        CancellationToken ct)
    {
        if (!Directory.Exists(directory))
            return [];

        if (!_searchEngine.HasIndex(directory))
        {
            var indexResult = await _searchEngine.BuildIndexAsync(directory, "*.md", ct);
            if (!indexResult.Success)
                return [];
        }

        var searchResult = await _searchEngine.SearchAsync(query, directory, maxResults, null, null, ct);
        return searchResult.Success ? searchResult.Matches : [];
    }

    private static AgentLogRecallMatch ToRecallMatch(
        FullTextSearchMatch match,
        AgentLogRecallSource source,
        string root)
    {
        var relativePath = Path.GetRelativePath(root, match.FilePath);
        var day = source == AgentLogRecallSource.DailySummary
            ? Path.GetFileNameWithoutExtension(match.FilePath)
            : relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).FirstOrDefault() ?? string.Empty;

        return new AgentLogRecallMatch(
            source,
            day,
            relativePath,
            match.LineNumber,
            match.LineText);
    }

    private static bool IsWithinDays(string day, DateTimeOffset now, int days)
    {
        if (!DateOnly.TryParse(day, out var parsed))
            return false;

        var today = DateOnly.FromDateTime(now.Date);
        var earliest = today.AddDays(-(Math.Max(days, 1) - 1));
        return parsed >= earliest && parsed <= today;
    }
}

public sealed record AgentLogRecallRequest(
    string AgentInstanceId,
    string Query,
    int RecentFiveDaysMessageLimit = 20,
    int RecentThirtyDaysMessageLimit = 10,
    int RecentDailySummaryLimit = 10);

public sealed record AgentLogRecallResult(
    IReadOnlyList<AgentLogRecallMatch> RecentFiveDaysMessages,
    IReadOnlyList<AgentLogRecallMatch> RecentThirtyDaysMessages,
    IReadOnlyList<AgentLogRecallMatch> RecentDailySummaries)
{
    public static AgentLogRecallResult Empty { get; } = new([], [], []);
}

public sealed record AgentLogRecallMatch(
    AgentLogRecallSource Source,
    string Day,
    string RelativePath,
    int LineNumber,
    string Snippet);

public enum AgentLogRecallSource
{
    MessageLog,
    DailySummary,
}

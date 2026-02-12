using System.Text;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PuddingCode.Configuration;

namespace PuddingRuntime.Services;

/// <summary>
/// Builds the unified historical context layer from persistent Session summary files.
/// Cold-start only: returns the full unified file content on first message, empty on subsequent messages.
/// </summary>
public sealed class AgentMemorySummaryContextBuilder
{
    private readonly PuddingDataPaths _paths;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SessionSummaryStore? _sessionSummaryStore;
    private readonly ILogger<AgentMemorySummaryContextBuilder>? _logger;

    // Session 级防重：每个新 Session 只注入一次 HISTORICAL 层。
    private readonly ConcurrentDictionary<string, byte> _builtSessionIds = new(StringComparer.Ordinal);

    public AgentMemorySummaryContextBuilder(
        PuddingDataPaths paths,
        Func<DateTimeOffset>? clock = null,
        SessionSummaryStore? sessionSummaryStore = null,
        ILogger<AgentMemorySummaryContextBuilder>? logger = null)
    {
        _paths = paths;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _sessionSummaryStore = sessionSummaryStore;
        _logger = logger;
    }

    public async Task<string> BuildAsync(
        string sessionId,
        string agentInstanceId,
        bool isFirstMessage,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentInstanceId))
        {
            _logger?.LogWarning("[MemoryRecall:HistoricalContext] skip emptyAgentId");
            return string.Empty;
        }

        if (!isFirstMessage)
        {
            _logger?.LogDebug(
                "[MemoryRecall:HistoricalContext] skip nonFirstMessage session={SessionId} agent={AgentId}",
                sessionId, agentInstanceId);
            return string.Empty;
        }

        // 同一 Session 只构建一次 HISTORICAL 上下文；不同 Session 即便属于同一 Agent，也必须重新冷启动注入。
        if (!_builtSessionIds.TryAdd(sessionId, 0))
        {
            _logger?.LogDebug(
                "[MemoryRecall:HistoricalContext] skip alreadyBuilt session={SessionId} agent={AgentId}",
                sessionId, agentInstanceId);
            return string.Empty;
        }

        if (_sessionSummaryStore is null)
        {
            return await BuildLegacyMemorySummaryAsync(agentInstanceId, ct);
        }

        var unifiedSummary = await _sessionSummaryStore.LoadLatestAsync(agentInstanceId, ct);
        if (string.IsNullOrWhiteSpace(unifiedSummary))
        {
            _logger?.LogInformation(
                "[MemoryRecall:HistoricalContext] recallEmpty agent={AgentId}",
                agentInstanceId);
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("--- LAYER: HISTORICAL CONTEXT ---");
        sb.AppendLine(unifiedSummary);

        var result = sb.ToString();

        _logger?.LogInformation(
            "[MemoryRecall:HistoricalContext] injected session={SessionId} agent={AgentId} totalLen={TotalLen} summaryLen={SummaryLen}",
            sessionId, agentInstanceId, result.Length, unifiedSummary.Length);
        return result;
    }

    private async Task<string> BuildLegacyMemorySummaryAsync(string agentInstanceId, CancellationToken ct)
    {
        var dailyRoot = _paths.AgentInstanceDailySummaryRoot(agentInstanceId);
        var contentFile = _paths.AgentInstanceContentSummaryFile(agentInstanceId);
        var sb = new StringBuilder();

        if (Directory.Exists(dailyRoot))
        {
            var today = DateOnly.FromDateTime(_clock().Date);
            var dailyFiles = Directory.EnumerateFiles(dailyRoot, "*.md")
                .Select(path => new
                {
                    Path = path,
                    Day = DateOnly.TryParse(Path.GetFileNameWithoutExtension(path), out var day) ? day : (DateOnly?)null,
                })
                .Where(x => x.Day is not null && x.Day.Value < today)
                .OrderByDescending(x => x.Day)
                .Take(7)
                .ToList();

            if (dailyFiles.Count > 0)
            {
                sb.AppendLine("--- LAYER: AGENT MEMORY SUMMARY ---");
                sb.AppendLine("Recent daily summaries:");
                foreach (var file in dailyFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    sb.AppendLine($"## {file.Day:yyyy-MM-dd}");
                    sb.AppendLine((await File.ReadAllTextAsync(file.Path, ct)).Trim());
                    sb.AppendLine();
                }
            }
        }

        if (File.Exists(contentFile))
        {
            if (sb.Length == 0)
                sb.AppendLine("--- LAYER: AGENT MEMORY SUMMARY ---");
            sb.AppendLine("Current day rolling summary (content.md):");
            sb.AppendLine((await File.ReadAllTextAsync(contentFile, ct)).Trim());
        }

        return sb.ToString();
    }
}

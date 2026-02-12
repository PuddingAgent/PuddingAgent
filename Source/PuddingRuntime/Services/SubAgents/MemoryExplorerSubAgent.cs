using System.Text;
using PuddingCode.Abstractions;
using PuddingMemoryEngine.Services;

namespace PuddingRuntime.Services.SubAgents;

/// <summary>
/// 记忆探索子代理：在主检索结果不足时，执行更深入的多查询探索并汇总相关记忆线索。
/// </summary>
public sealed class MemoryExplorerSubAgent
{
    private readonly IMemoryLibraryConvenience _libraryConvenience;
    private readonly ISubconsciousOrchestrator? _orchestrator;
    private readonly IMemoryLlmClient? _memoryLlmClient;
    private readonly ILogger<MemoryExplorerSubAgent> _logger;

    public MemoryExplorerSubAgent(
        IMemoryLibraryConvenience libraryConvenience,
        ILogger<MemoryExplorerSubAgent> logger,
        ISubconsciousOrchestrator? orchestrator = null,
        IMemoryLlmClient? memoryLlmClient = null)
    {
        _libraryConvenience = libraryConvenience;
        _orchestrator = orchestrator;
        _memoryLlmClient = memoryLlmClient;
        _logger = logger;
    }

    /// <summary>
    /// 深度探索记忆图书馆，返回自然语言综合总结。
    /// </summary>
    public async Task<string> ExploreAsync(
        string userQuery,
        string workspaceId,
        string[] recentMessages,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userQuery) || string.IsNullOrWhiteSpace(workspaceId))
            return "No additional memory insights are available.";

        _logger.LogDebug(
            "[MemoryExplorerSubAgent] Start explore workspace={Workspace} query={Query}",
            workspaceId,
            userQuery);

        var exploratoryQueries = BuildExploratoryQueries(userQuery, recentMessages);
        var merged = new Dictionary<string, RankedResult>(StringComparer.OrdinalIgnoreCase);

        foreach (var query in exploratoryQueries)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var results = await _libraryConvenience.SmartSearchAsync(query, topK: 5, ct);
                foreach (var result in results)
                {
                    var key = $"{result.BookId}|{result.ChapterId}|{result.Snippet}";
                    if (!merged.ContainsKey(key))
                        merged[key] = result;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "[MemoryExplorerSubAgent] SmartSearch failed workspace={Workspace} query={Query}",
                    workspaceId,
                    query);
            }
        }

        string? recallSummary = null;
        if (_orchestrator is not null)
        {
            try
            {
                recallSummary = await _orchestrator.RecallAugmentedAsync(
                    userQuery,
                    workspaceId,
                    "memory-explorer-subagent",
                    sessionId: null,
                    maxTokens: 1200,
                    ct: ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "[MemoryExplorerSubAgent] RecallAugmentedAsync failed workspace={Workspace}",
                    workspaceId);
            }
        }

        if (merged.Count == 0 && string.IsNullOrWhiteSpace(recallSummary))
            return "No additional memory insights are available.";

        var findings = merged.Values
            .OrderByDescending(r => r.Score)
            .Take(12)
            .Select(r => $"- [{r.BookTitle}] {r.Snippet}")
            .ToArray();

        var findingsBlock = string.Join("\n", findings);
        if (_memoryLlmClient is null)
            return BuildFallbackSummary(findingsBlock, recallSummary);

        const string systemPrompt =
            "You are a memory exploration sub-agent. Produce a concise but comprehensive summary in the user's language. " +
            "Include direct answers and also related clues that might help. Do not fabricate facts.";

        var recentContext = recentMessages?.Length > 0
            ? string.Join("\n", recentMessages.TakeLast(6))
            : "(none)";

        var userPrompt =
            $"User query: {userQuery}\n" +
            $"Recent messages:\n{recentContext}\n\n" +
            $"Recall summary:\n{(string.IsNullOrWhiteSpace(recallSummary) ? "(none)" : recallSummary)}\n\n" +
            $"Search findings:\n{(string.IsNullOrWhiteSpace(findingsBlock) ? "(none)" : findingsBlock)}\n\n" +
            "Also answer: What else might be related?";

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

            var llmSummary = await _memoryLlmClient.ChatAsync(systemPrompt, userPrompt, tools: null, timeoutCts.Token);
            if (!string.IsNullOrWhiteSpace(llmSummary))
                return llmSummary.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[MemoryExplorerSubAgent] LLM summary failed, fallback to rule summary.");
        }

        return BuildFallbackSummary(findingsBlock, recallSummary);
    }

    private static IReadOnlyList<string> BuildExploratoryQueries(string userQuery, IReadOnlyList<string>? recentMessages)
    {
        var queries = new List<string>
        {
            userQuery,
            $"{userQuery} related preferences",
            $"{userQuery} related personal info",
            $"{userQuery} related plans",
        };

        if (recentMessages is { Count: > 0 })
        {
            foreach (var msg in recentMessages.TakeLast(3))
            {
                if (!string.IsNullOrWhiteSpace(msg))
                    queries.Add(msg.Trim());
            }
        }

        return queries
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
    }

    private static string BuildFallbackSummary(string findingsBlock, string? recallSummary)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(recallSummary))
        {
            sb.AppendLine("Recall summary:");
            sb.AppendLine(recallSummary.Trim());
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(findingsBlock))
        {
            sb.AppendLine("Related memory findings:");
            sb.AppendLine(findingsBlock.Trim());
        }

        return sb.ToString().Trim();
    }
}

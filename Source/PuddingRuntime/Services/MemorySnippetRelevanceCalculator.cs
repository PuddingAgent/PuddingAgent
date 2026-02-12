using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingRuntime.Models;

namespace PuddingRuntime.Services;

/// <summary>
/// 记忆线索关联度计算器 — 验证 Flash 裁剪后的记忆片段与最近消息的匹配度，
/// 返回 relevanceBoost（0.5~2.0），用于调整连续性消息的合并数量。
///
/// 关联度映射：
///   avgScore >= 0.8 → relevanceBoost = 2.0 (高度匹配，多合并)
///   avgScore >= 0.5 → relevanceBoost = 1.5 (中度匹配)
///   avgScore >= 0.2 → relevanceBoost = 1.0 (默认)
///   avgScore < 0.2  → relevanceBoost = 0.5 (弱匹配，少合并)
/// </summary>
public sealed class MemorySnippetRelevanceCalculator
{
    private readonly IRawSessionLogService? _sessionLogService;
    private readonly ILogger<MemorySnippetRelevanceCalculator> _logger;

    // 中文停用词
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "的", "了", "是", "在", "有", "和", "就", "不", "人", "都",
        "一个", "上", "也", "很", "到", "说", "要", "去", "你", "会",
        "the", "a", "an", "is", "are", "was", "were", "be", "been",
        "it", "this", "that", "to", "of", "in", "for", "on", "with",
        "这", "那", "吗", "呢", "吧", "啊", "哦", "嗯", "哈", "呀",
    };

    // 回溯搜索天数
    private const int SearchLookbackDays = 3;

    public MemorySnippetRelevanceCalculator(
        ILogger<MemorySnippetRelevanceCalculator> logger,
        IRawSessionLogService? sessionLogService = null)
    {
        _logger = logger;
        _sessionLogService = sessionLogService;
    }

    /// <summary>
    /// 计算记忆线索与最近会话消息的关联度。
    /// 返回 0.5~2.0 的调整因子。
    /// </summary>
    public async Task<double> CalculateRelevanceAsync(
        List<MemorySnippet> snippets,
        string workspaceId,
        string agentInstanceId,
        CancellationToken ct)
    {
        if (snippets.Count == 0 || _sessionLogService is null)
            return 1.0;

        var matchScores = new List<double>();

        foreach (var snippet in snippets)
        {
            if (string.IsNullOrWhiteSpace(snippet.Text))
                continue;

            var keywords = ExtractKeywords(snippet.Text);
            if (keywords.Count == 0) continue;

            try
            {
                var totalMatches = 0;

                // 逐天搜索关键词
                var today = DateTime.UtcNow;
                for (int i = 0; i < SearchLookbackDays; i++)
                {
                    var day = today.AddDays(-i).ToString("yyyy-MM-dd");

                    var request = new RawSessionLogSearchRequest
                    {
                        WorkspaceId = workspaceId,
                        AgentInstanceId = agentInstanceId,
                        Query = string.Join(" ", keywords),
                        FromDay = day,
                        ToDay = day,
                        Limit = 5,
                        Regex = false,
                    };

                    var result = await _sessionLogService.GrepMessagesAsync(request, ct);
                    totalMatches += result.Matches.Count;
                }

                // 5 条以上匹配 = 满分 1.0
                var score = Math.Min(totalMatches / 5.0, 1.0);
                matchScores.Add(score);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[RelevanceCalc] Search failed for keywords={Keywords}", keywords);
            }
        }

        if (matchScores.Count == 0)
            return 1.0;

        var avgScore = matchScores.Average();

        _logger.LogInformation(
            "[RelevanceCalc] AvgScore={Score:F2} from {Count} snippets, samples={Scores}",
            avgScore, matchScores.Count, string.Join(",", matchScores.Select(s => s.ToString("F2"))));

        return avgScore switch
        {
            >= 0.8 => 2.0,
            >= 0.5 => 1.5,
            >= 0.2 => 1.0,
            _      => 0.5,
        };
    }

    /// <summary>
    /// 简单关键词提取：按分隔符分词，去停用词，取最长 5 个。
    /// </summary>
    private static List<string> ExtractKeywords(string text)
    {
        var separators = new[] { ' ', '\n', '\r', '\t', '，', '。', '、', '：', '；', '(', ')', '[', ']', '{', '}' };

        return text
            .Split(separators, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim())
            .Where(w => w.Length > 1 && !StopWords.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
    }
}

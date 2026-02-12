using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;

namespace PuddingRuntime.Services;

/// <summary>
/// 时间聚类分析器 — 根据上一个会话与当前消息的时间间隔，
/// 计算应合并的连续消息数量，并提取上一个会话窗口的原始消息。
///
/// 时间聚类规则：
///   gap &lt; 5分钟  → 合并 10 条 (高连续性)
///   gap &lt; 30分钟 → 合并 8 条  (中连续性)
///   gap &lt; 1小时  → 合并 5 条  (低连续性)
///   gap >= 1小时  → 合并 3 条  (弱连续性)
/// </summary>
public sealed class TimeClusterAnalyzer
{
    private readonly IRawSessionLogService? _sessionLogService;
    private readonly ILogger<TimeClusterAnalyzer> _logger;

    // 回溯天数上限（查找上一个会话）
    private const int MaxLookbackDays = 3;

    // 去除 tool_call / tool_result / thinking 块的正则
    private static readonly Regex ToolCallBlockRegex = new(
        @"```tool_call[\s\S]*?```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ToolResultBlockRegex = new(
        @"```tool_result[\s\S]*?```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ThinkingBlockRegex = new(
        @"<thinking>[\s\S]*?</thinking>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MultiNewlineRegex = new(
        @"\n{3,}",
        RegexOptions.Compiled);

    public TimeClusterAnalyzer(
        ILogger<TimeClusterAnalyzer> logger,
        IRawSessionLogService? sessionLogService = null)
    {
        _logger = logger;
        _sessionLogService = sessionLogService;
    }

    /// <summary>
    /// 根据时间间隔计算基础合并消息数量。
    /// </summary>
    public int CalculateBaseMergeCount(DateTime previousSessionLastMsgTime, DateTime currentMessageTime)
    {
        var gap = currentMessageTime - previousSessionLastMsgTime;

        return gap.TotalMinutes switch
        {
            < 5  => 10,
            < 30 => 8,
            < 60 => 5,
            _    => 3,
        };
    }

    /// <summary>
    /// 应用关联度调整，返回最终合并数量。
    /// </summary>
    public int ApplyRelevanceBoost(int baseCount, double relevanceBoost)
    {
        var adjusted = (int)Math.Round(baseCount * relevanceBoost);
        return Math.Clamp(adjusted, 1, 15);
    }

    /// <summary>
    /// 获取上一个会话窗口的最后 N 条消息（剥离 tool/thinking，只保留 user/assistant 对话）。
    /// 返回 null 表示没有可用的上一个会话。
    /// </summary>
    public async Task<List<ContinuityMessage>?> GetPreviousWindowMessagesAsync(
        string workspaceId,
        string agentInstanceId,
        string currentSessionId,
        DateTime currentMessageTime,
        int mergeCount,
        CancellationToken ct)
    {
        if (_sessionLogService is null)
            return null;

        try
        {
            // 1. 找到上一个会话
            var prevSession = await FindPreviousSessionAsync(
                workspaceId, agentInstanceId, currentSessionId, ct);

            if (prevSession is null)
            {
                _logger.LogDebug(
                    "[TimeCluster] No previous session found for agent={Agent}",
                    agentInstanceId);
                return null;
            }

            // 2. 读取上一个会话窗口的最后 N 条消息
            var messages = new List<RawSessionLogMessage>();
            long? cursor = null;

            while (messages.Count < mergeCount)
            {
                var page = await _sessionLogService.ReadMessagesAsync(
                    workspaceId,
                    prevSession.SessionId,
                    agentInstanceId,
                    before: cursor,
                    limit: Math.Min(mergeCount + 5, 30),
                    ct);

                // messages 按时间倒序返回（最新在前），需要反转以保序
                foreach (var msg in page.Messages)
                {
                    if (msg.Role == "user" || msg.Role == "assistant")
                    {
                        messages.Add(msg);
                        if (messages.Count >= mergeCount)
                            break;
                    }
                }

                if (!page.HasMore)
                    break;

                cursor = page.NextCursor;
            }

            if (messages.Count == 0)
                return null;

            // messages 现在是倒序（最新在前），反转并剥离非对话内容
            // 限制最终输出数量
            var lastN = messages
                .Take(mergeCount)
                .Reverse()
                .Select(m => new ContinuityMessage
                {
                    Role = m.Role,
                    Content = StripNonContent(m.Content),
                })
                .ToList();

            _logger.LogInformation(
                "[TimeCluster] Got {Count} continuity messages from prev session={PrevSession}, gap={GapMinutes}m",
                lastN.Count, prevSession.SessionId, prevSession.GapMinutes);

            return lastN;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[TimeCluster] Failed to get previous window messages");
            return null;
        }
    }

    /// <summary>
    /// 找到当前会话之前最近的一个会话及其最后消息时间。
    /// </summary>
    private async Task<PreviousSessionInfo?> FindPreviousSessionAsync(
        string workspaceId,
        string agentInstanceId,
        string currentSessionId,
        CancellationToken ct)
    {
        if (_sessionLogService is null) return null;

        // 逐天回溯查找
        var today = DateTime.UtcNow;
        var allSessions = new List<(RawSessionLogSessionSummary summary, string day)>();

        for (int i = 0; i < MaxLookbackDays; i++)
        {
            var day = today.AddDays(-i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            try
            {
                var sessionList = await _sessionLogService.ListSessionsAsync(
                    workspaceId, day, limit: 100, agentInstanceId, ct);
                foreach (var s in sessionList.Sessions)
                    allSessions.Add((s, day));
            }
            catch
            {
                // 某天无数据，继续回溯
            }
        }

        // 按最后活跃时间降序排列，取 currentSession 之前的第一个
        var sorted = allSessions
            .Where(s => s.summary.SessionId != currentSessionId)
            .OrderByDescending(s => s.summary.LastRecordedAt)
            .ToList();

        if (sorted.Count == 0)
            return null;

        var prev = sorted[0];
        var lastRecordedAt = DateTime.TryParse(prev.summary.LastRecordedAt,
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed) ? parsed : DateTime.UtcNow.AddHours(-1);

        return new PreviousSessionInfo
        {
            SessionId = prev.summary.SessionId,
            LastRecordedAt = lastRecordedAt,
            GapMinutes = (DateTime.UtcNow - lastRecordedAt).TotalMinutes,
        };
    }

    /// <summary>
    /// 剥离非对话内容：tool_call 块、tool_result 块、thinking 块，清理多余空行。
    /// </summary>
    internal static string StripNonContent(string rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
            return string.Empty;

        var result = rawContent;
        result = ToolCallBlockRegex.Replace(result, "");
        result = ToolResultBlockRegex.Replace(result, "");
        result = ThinkingBlockRegex.Replace(result, "");
        result = MultiNewlineRegex.Replace(result, "\n\n");
        return result.Trim();
    }
}

/// <summary>
/// 连续消息 — 从上一个会话窗口提取的原始 user/assistant 对话。
/// </summary>
public sealed class ContinuityMessage
{
    public string Role { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
}

/// <summary>
/// 上一个会话窗口的基本信息。
/// </summary>
internal sealed class PreviousSessionInfo
{
    public string SessionId { get; init; } = string.Empty;
    public DateTime LastRecordedAt { get; init; }
    public double GapMinutes { get; init; }
}

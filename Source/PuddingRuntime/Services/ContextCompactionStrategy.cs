using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PuddingCode.Observability;
using PuddingCode.Runtime;

namespace PuddingRuntime.Services;

/// <summary>
/// 压缩策略决策器。
/// 封装"是否压缩 / 等待工作总结 / 跳过"的判断逻辑，
/// 从 ContextWindowManager.TryAutoCompactAsync 中提取，消除上帝类。
/// </summary>
public sealed class ContextCompactionStrategy
{
    private readonly ILogger<ContextWindowManager> _logger;
    private readonly ConcurrentDictionary<string, int> _workSummaryRetryCount;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _workSummaryFirstInjectedAt;

    public ContextCompactionStrategy(
        ILogger<ContextWindowManager> logger,
        ConcurrentDictionary<string, int> workSummaryRetryCount,
        ConcurrentDictionary<string, DateTimeOffset> workSummaryFirstInjectedAt)
    {
        _logger = logger;
        _workSummaryRetryCount = workSummaryRetryCount;
        _workSummaryFirstInjectedAt = workSummaryFirstInjectedAt;
    }

    /// <summary>
    /// 根据健康快照和工作总结状态，决定下一步动作。
    /// </summary>
    public CompactionDecision Decide(
        string sessionId,
        ContextHealthSnapshot health,
        string? agentWorkSummary,
        ContextCompactionOptions? compactionOptions,
        bool hasCompactionNotifier)
    {
        // 无压缩服务 → 跳过
        if (!health.ShouldAutoCompact)
            return CompactionDecision.Skip;

        // 已有工作总结 → 直接压缩
        if (!string.IsNullOrWhiteSpace(agentWorkSummary))
        {
            _logger.LogInformation(
                "[ContextWindow:AutoCompact] found agent work summary in history session={Session} len={Len} preview={Preview}",
                sessionId, agentWorkSummary.Length,
                TruncateForLog(agentWorkSummary, ContextWindowConstants.WorkSummaryLogPreviewLength));

            // 重置重试计数
            _workSummaryRetryCount.TryRemove(sessionId, out _);
            _workSummaryFirstInjectedAt.TryRemove(sessionId, out _);
            return CompactionDecision.Proceed;
        }

        // 工作总结尚未生成 —— 检查是否超出最大等待限制
        var maxRetries = compactionOptions?.MaxWorkSummaryRetries
                         ?? ContextWindowConstants.DefaultMaxWorkSummaryRetries;
        var maxWaitSeconds = compactionOptions?.MaxWaitForWorkSummarySeconds
                             ?? ContextWindowConstants.DefaultMaxWaitForWorkSummarySeconds;
        var currentRetry = _workSummaryRetryCount.GetValueOrDefault(sessionId);
        var firstInjected = _workSummaryFirstInjectedAt.GetValueOrDefault(sessionId);
        var elapsed = firstInjected == default
            ? TimeSpan.Zero
            : DateTimeOffset.UtcNow - firstInjected;

        if (hasCompactionNotifier && currentRetry < maxRetries && elapsed.TotalSeconds < maxWaitSeconds)
        {
            _workSummaryFirstInjectedAt.GetOrAdd(sessionId, _ => DateTimeOffset.UtcNow);
            _workSummaryRetryCount.AddOrUpdate(sessionId, 1, (_, c) => c + 1);

            _logger.LogInformation(
                "[ContextWindow:AutoCompact] waiting for agent work summary session={Session} retry={Retry}/{MaxRetries} elapsed={Elapsed:F0}s/{MaxWait}s reason={Reason}",
                sessionId, currentRetry + 1, maxRetries, elapsed.TotalSeconds, maxWaitSeconds,
                currentRetry == 0 ? "first_injection" : "retry");

            return CompactionDecision.WaitForSummary;
        }

        // 超出等待限制或无通知器，直接强制压缩
        if (hasCompactionNotifier)
        {
            var reason = elapsed.TotalSeconds >= maxWaitSeconds ? "timeout" : "retries_exhausted";
            _logger.LogWarning(
                "[ContextWindow:AutoCompact] forcing compact without work summary session={Session} retries={Retries}/{MaxRetries} elapsed={Elapsed:F0}s/{MaxWait}s reason={Reason}",
                sessionId, currentRetry, maxRetries, elapsed.TotalSeconds, maxWaitSeconds, reason);
        }
        else
        {
            _logger.LogInformation(
                "[ContextWindow:AutoCompact] no AgentCompactionNotifier, proceeding without work summary session={Session}",
                sessionId);
        }

        return CompactionDecision.Proceed;
    }

    /// <summary>
    /// 压缩完成后清理重试状态。
    /// </summary>
    public void ClearRetryState(string sessionId)
    {
        _workSummaryRetryCount.TryRemove(sessionId, out _);
        _workSummaryFirstInjectedAt.TryRemove(sessionId, out _);
    }

    /// <summary>
    /// 获取当前重试次数（用于遥测）。
    /// </summary>
    public int GetRetryCount(string sessionId) =>
        _workSummaryRetryCount.GetValueOrDefault(sessionId);

    /// <summary>
    /// 获取已等待时间（用于遥测）。
    /// </summary>
    public TimeSpan GetElapsedTime(string sessionId)
    {
        var firstInjected = _workSummaryFirstInjectedAt.GetValueOrDefault(sessionId);
        return firstInjected == default
            ? TimeSpan.Zero
            : DateTimeOffset.UtcNow - firstInjected;
    }

    private static string TruncateForLog(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var normalized = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "…";
    }
}

/// <summary>
/// 压缩决策结果。
/// </summary>
public enum CompactionDecision
{
    /// <summary>无需压缩。</summary>
    Skip,

    /// <summary>等待 Agent 生成工作总结。</summary>
    WaitForSummary,

    /// <summary>立即执行压缩。</summary>
    Proceed,
}

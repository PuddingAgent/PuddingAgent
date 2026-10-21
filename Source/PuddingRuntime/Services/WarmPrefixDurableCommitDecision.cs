using PuddingCode.Runtime;

namespace PuddingRuntime.Services;

/// <summary>
/// 持久提交准入：已有精确 ID 或提供完整原始跨度时才尝试。未知来源由提交服务解析或原子归档；
/// 只有摘要、原文、覆盖清单和代次一起提交成功，才允许替换内存历史。
/// </summary>
internal static class WarmPrefixDurableCommitDecision
{
    /// <summary>降级原因码（进入日志与 completed 事件的 <c>durableSkipReason</c>）。</summary>
    internal const string ServiceUnavailable = "compaction_service_unavailable";

    internal const string UnresolvedIdentitiesPrefix = "unresolved_durable_identities:";

    /// <summary>完整原文允许提交服务持久化未投影消息；局部 ID 列表本身不足以授权覆盖。</summary>
    internal static bool ShouldAttempt(
        bool compactionServiceAvailable,
        DurableSpanResolution resolution,
        int sourceMessageCount = 0)
        => compactionServiceAvailable
           && ((resolution.AllResolved && resolution.MessageIds.Count > 0) || sourceMessageCount > 0);

    /// <summary>不可落库时的原因码；可以落库时返回 null。</summary>
    internal static string? ResolveSkipReason(bool compactionServiceAvailable, DurableSpanResolution resolution)
    {
        if (!compactionServiceAvailable)
            return ServiceUnavailable;
        if (!resolution.AllResolved)
            return $"{UnresolvedIdentitiesPrefix}{resolution.UnresolvedCount}";
        if (resolution.MessageIds.Count == 0)
            return $"{UnresolvedIdentitiesPrefix}{resolution.UnresolvedCount}";
        return null;
    }

    /// <summary>
    /// 把落库结果折成调用方要上报的 (是否持久化, 原因)。非 <c>Committed</c> 一律是降级，
    /// 且调用方必须保留原始运行期历史。
    /// </summary>
    internal static (bool DurableCheckpoint, string? SkipReason) Interpret(WarmPrefixCheckpointCommitResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Committed
            ? (true, null)
            : (false, $"durable_commit_{result.Status.ToString().ToLowerInvariant()}");
    }
}

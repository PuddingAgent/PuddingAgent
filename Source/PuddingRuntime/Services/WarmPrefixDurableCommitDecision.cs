using PuddingCode.Runtime;

namespace PuddingRuntime.Services;

/// <summary>
/// warm-prefix 落库决策（F2 最后一环）。把「要不要/能不能写入持久 checkpoint」从
/// <c>AgentExecutionService</c> 里抽成**纯判定**，使降级分支可脱离执行宿主单测。
/// <para>
/// 硬约束（来自 [持久消息身份登记] 与 F2 落库原语）：
/// <list type="bullet">
/// <item>跨度里只要有**没有持久行**的消息（本 Turn 新追加、尚未落库），就**不许**写覆盖标注 ——
/// 错标等于静默隐藏真实消息，部分标注则会让跨度与摘要脱节。</item>
/// <item>没有可用的压缩服务（默认实现/未注册）时同样只做内存替换。</item>
/// <item>降级**不影响**本次会话的压缩本身：内存历史照样替换（保住延迟收益），
/// 只是 <c>durableCheckpoint = false</c> 并带上原因。</item>
/// </list>
/// </para>
/// </summary>
internal static class WarmPrefixDurableCommitDecision
{
    /// <summary>降级原因码（进入日志与 completed 事件的 <c>durableSkipReason</c>）。</summary>
    internal const string ServiceUnavailable = "compaction_service_unavailable";

    internal const string UnresolvedIdentitiesPrefix = "unresolved_durable_identities:";

    /// <summary>
    /// 是否可以尝试落库。
    /// <para>
    /// 内存身份解析成功当然可以；**解析失败但有内容键**同样可以 —— 服务端会拿内容键在库里
    /// **唯一匹配**地回退解析（内存对象在归一化/渲染后会被复制，按引用解析可能整体失效：
    /// 流式路径实测 29 条全部 unresolved）。重复内容一律判为不可解析，因此放宽这一步不会引入错标。
    /// </para>
    /// </summary>
    internal static bool ShouldAttempt(
        bool compactionServiceAvailable,
        DurableSpanResolution resolution,
        int spanKeyCount = 0)
        => compactionServiceAvailable
           && ((resolution.AllResolved && resolution.MessageIds.Count > 0) || spanKeyCount > 0);

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
    /// 且**不得**因此撤销已完成的运行期压缩。
    /// </summary>
    internal static (bool DurableCheckpoint, string? SkipReason) Interpret(WarmPrefixCheckpointCommitResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Committed
            ? (true, null)
            : (false, $"durable_commit_{result.Status.ToString().ToLowerInvariant()}");
    }
}

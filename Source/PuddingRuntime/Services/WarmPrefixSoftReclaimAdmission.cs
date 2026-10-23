namespace PuddingRuntime.Services;

/// <summary>
/// 软维护压缩的「微小回收」准入判定。
/// <para>
/// 动机（2026-10-23 实测）：压缩调用本身的缓存命中率约 0–1%（`agent_diagnostics cache_health`：
/// 两次压缩调用 hit_rate 0.012 / 0），且一旦改写历史尾部就作废整段前缀缓存，下一轮按未命中价重算。
/// 而 10/22 日志显示 40/181 次压缩（22%）回收 ≤5k tokens——回收极少、代价极高，是净亏损。
/// </para>
/// <para>
/// 因此**软维护**（历史仍能安全容纳当前请求）在预计回收量低于门槛时直接跳过，保留原历史；
/// **硬保护**必须执行：那时不压缩就发不出去，跳过只会把失败推给下一跳。
/// </para>
/// </summary>
internal static class WarmPrefixSoftReclaimAdmission
{
    /// <summary>最小回收 token 绝对门槛。</summary>
    internal const int MinimumReclaimTokens = 2_000;

    /// <summary>最小回收比例门槛（相对压缩前已用 token），与绝对门槛取较大者。</summary>
    internal const double MinimumReclaimRatio = 0.05;

    /// <summary>回收门槛：<c>max(绝对门槛, ceil(已用 token × 比例))</c>。</summary>
    internal static int ResolveFloor(int initialUsedTokens) =>
        Math.Max(MinimumReclaimTokens, (int)Math.Ceiling(Math.Max(0, initialUsedTokens) * MinimumReclaimRatio));

    /// <summary>
    /// 是否跳过本次压缩。只有「软维护 + 非强制 + 计划回收低于门槛」三者同时成立才跳过。
    /// </summary>
    /// <param name="removedTokenEstimate">计划移除消息的 token 估算（计划里的真实字段，不是另算的公式）。</param>
    /// <param name="initialUsedTokens">压缩前已用 token。</param>
    /// <param name="requiresSynchronousProtection">计划判定必须同步硬保护（已越界）时为 true。</param>
    /// <param name="forcePreparation">调用方强制准备（越界兜底）时为 true。</param>
    internal static bool ShouldSkip(
        int removedTokenEstimate,
        int initialUsedTokens,
        bool requiresSynchronousProtection,
        bool forcePreparation)
    {
        if (forcePreparation || requiresSynchronousProtection)
            return false;
        return removedTokenEstimate < ResolveFloor(initialUsedTokens);
    }
}

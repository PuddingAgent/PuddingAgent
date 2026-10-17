namespace PuddingRuntime.Services;

/// <summary>
/// warm-prefix 维护准入（F3）。把「软维护」与「硬保护」两种尝试的封顶口径收在一处：
/// <list type="bullet">
/// <item>
/// <b>软维护</b>：每 dispatch 只评估一次 —— 同一 dispatch 内重复评估不会产生新的可压缩内容
/// （ADR-095 D1 的候选级准入；真正的去重/退避由持久候选指纹承担，本轮尚未落地）。
/// </item>
/// <item>
/// <b>硬保护</b>：<b>逐轮重新评估</b>，不受软维护封顶限制 —— 第 0 轮被延期的软维护不得让后续轮次
/// 只剩请求副本裁剪。诊断 2026-10-07 §5.2 记录了这条退化路径：第一条样本的 Turn 内在
/// round 34/35 再次越过硬上限，只能靠 <c>Prepare</c> 裁剪出站副本（removed=1 / removed=76）。
/// </item>
/// </list>
/// <para>
/// 判定本身不做 I/O，也**不需要额外计量**：调用方用本轮 <c>LlmRequestBudgetGuard.Prepare</c> 的结果作为
/// 证据 —— <c>RemovedMessageCount &gt; 0</c>（裁掉了才能装下）或 <c>LlmInputBudgetExceededException</c>
/// （连裁剪都不够）都证明「未裁剪请求已越过有效输入上限」。
/// </para>
/// </summary>
internal static class WarmPrefixMaintenanceAdmission
{
    /// <summary>少于这个条数不值得压缩；与既有 <c>history.Count &gt; 12</c> 口径一致。</summary>
    internal const int MinHistoryMessages = 12;

    /// <summary>本轮是否应做软维护评估（每 dispatch 一次）。</summary>
    internal static bool ShouldAttemptSoft(int historyCount, bool softAttemptedThisDispatch)
        => historyCount > MinHistoryMessages && !softAttemptedThisDispatch;

    /// <summary>
    /// 本轮是否应做<b>硬保护</b>尝试。与软维护封顶无关：只要越界且本 dispatch 尚未试过硬保护就尝试；
    /// 试过之后不再重复（避免每轮都做一次昂贵测量），但若那次没形成候选则下一轮仍会重试。
    /// </summary>
    internal static bool ShouldAttemptHardProtection(int historyCount, bool hardProtectionAttemptedThisDispatch)
        => historyCount > MinHistoryMessages && !hardProtectionAttemptedThisDispatch;

    /// <summary>
    /// 记录一次维护尝试后的两个封顶标记。
    /// <para>
    /// 关键不变量：软维护调用若已经走到**同步硬保护**分支（<c>RequiresSynchronousProtection=true</c>），
    /// 它就已经执行过一次硬保护；同一 round 的硬保护准入不得再发一次**字节级相同**的摘要请求。
    /// 2026-10-17 事故：同 round 两次 1.68M token 请求（间隔 24s、请求体 8,063,740B 完全相同）
    /// 产生两张失败卡片，并让 prepare 多花约 12s 做重复的整段计量。
    /// </para>
    /// </summary>
    internal static (bool SoftAttempted, bool HardProtectionAttempted) RecordAttempt(
        bool planCreated,
        bool requiresSynchronousProtection,
        bool softAttempted,
        bool hardProtectionAttempted)
        => (
            softAttempted || planCreated,
            hardProtectionAttempted || (planCreated && requiresSynchronousProtection));
}

using PuddingCode.Models;

namespace PuddingContextPruning;

/// <summary>
/// 没有发生剪枝的原因。<see cref="None"/> 之外的所有取值都表示
/// <see cref="PruneOutcome.Messages"/> 就是入参列表实例本身。
/// </summary>
public enum PruneNoOpReason
{
    /// <summary>发生了剪枝（且通过了准入地板）。</summary>
    None,

    /// <summary>配置关闭（<see cref="DeterministicPruneOptions.Enabled"/> = false）。</summary>
    Disabled,

    /// <summary>输入为空。</summary>
    NoMessages,

    /// <summary>全部消息都落在受保护尾部里，没有可动的位置。</summary>
    TailOnly,

    /// <summary>有可动位置，但没有任何一条消息命中策略。</summary>
    NothingEligible,

    /// <summary>剪枝收益低于准入地板（<c>MinReclaimTokens</c> / <c>MinReclaimRatio</c>），整份回退。</summary>
    ReclaimBelowMinimum
}

/// <summary>
/// 一次剪枝尝试的计数。<see cref="BeforeTokens"/> / <see cref="AfterTokens"/> 用同一计数器测量，
/// 因此 <see cref="ReclaimedTokens"/> 可以直接当作「省下的钱」。
/// <para>回退（<see cref="PruneNoOpReason.ReclaimBelowMinimum"/>）时 After 被记为 Before，Reclaimed = 0。</para>
/// </summary>
public sealed record PruneStats(
    int DeduplicatedToolResults,
    int SummarizedToolResults,
    int TruncatedToolArguments,
    int BeforeTokens,
    int AfterTokens)
{
    /// <summary>全零计数，用于所有「没有改动」的返回路径。</summary>
    public static PruneStats Zero { get; } = new(0, 0, 0, 0, 0);

    /// <summary>回收的估算 token 数（恒 ≥ 0，回退时为 0）。</summary>
    public int ReclaimedTokens => BeforeTokens - AfterTokens;
}

/// <summary>
/// 剪枝结果。<see cref="Messages"/> 在未变更时是**入参列表实例**，
/// 在变更时是新列表且未改动的元素按引用复用。
/// </summary>
public sealed record PruneOutcome(
    IReadOnlyList<ChatMessage> Messages,
    PruneStats Stats,
    PruneNoOpReason NoOpReason)
{
    /// <summary>是否真的改写了至少一条消息。与 <see cref="PruneNoOpReason.None"/> 等价（准入地板保证 None ⇒ 计数 > 0）。</summary>
    public bool Changed => NoOpReason == PruneNoOpReason.None;
}

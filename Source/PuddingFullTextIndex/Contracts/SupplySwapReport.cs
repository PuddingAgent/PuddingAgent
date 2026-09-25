using System.Text;

namespace PuddingFullTextIndex.Contracts;

/// <summary>
/// 一次 staged 供给的**切换终局**（A2a R6）：让「换没换成、为什么没成功」有稳定判据，
/// 不必去解析自由文本消息。
/// </summary>
public enum SupplySwapOutcome
{
    /// <summary>未发生切换：staging 构建本身失败、预算量不出、staging 目录被占用、或直写模式（<c>UseStaging=false</c>）。</summary>
    None = 0,

    /// <summary>staging 已原子切换到 live（旧 live 已移入 <c>.trash</c> 并被尽力删除）。</summary>
    Swapped = 1,

    /// <summary>构建后**实测**超预算 ⇒ 未切 live（live 一字节不动），staging 已被清理。</summary>
    RejectedOverBudget = 2,

    /// <summary>切换途中失败，已把 <c>.trash</c> 副本回滚回 live（live 仍是旧索引）。</summary>
    RolledBack = 3,
}

/// <summary>
/// staged 供给的切换口径快照（A2a R6）：<c>outcome</c> + 预算/字节五项事实，
/// 由 <see cref="IFullTextIndexBuilder"/> 以 <see cref="SupplyBuildResult.Swap"/> 返回，
/// 并被抓进 job 终态消息（<c>SupplyJobStatus.Message</c>）。
/// <para>
/// ⚠️ 这些字段是**实测值**（staging 目录递归字节 / live 全部 scope 索引字节），不是预测值；
/// 预测口径见 <see cref="SupplyPlanResult"/> 与 <c>SupplyIndexSizeEstimator</c>。
/// </para>
/// </summary>
/// <param name="Outcome">切换终局。</param>
/// <param name="BudgetBytes">本次生效的「配置集合总预算」（字节）。</param>
/// <param name="StagingBytes">staging 索引目录的实测字节数（未开始构建则为 0）。</param>
/// <param name="LiveBytesBefore">切换前 live **全部** scope 索引目录的实测字节合计。</param>
/// <param name="LiveBytesAfter">本次供给结束时的同一口径实测合计（未切换/切换失败时应等于 <paramref name="LiveBytesBefore"/>）。</param>
/// <param name="StagingDirectory">本次 job 的 staging 根（<c>&lt;IndexRoot&gt;/.staging/&lt;sha256(scopeKey)&gt;-&lt;jobId&gt;</c>）；直写模式为 null。</param>
/// <param name="TrashDirectory">被替换下来的旧 live 的暂存路径；未移动过旧 live 或删除成功且无需保留时为 null。</param>
/// <param name="CleanedArtifacts">本次供给开始前清掉的过期残留（相对 IndexRoot 的条目名）；没有则为空集合。</param>
/// <param name="CleanupError">残留清理/trash 删除过程中遇到的问题（**不影响供给成败**，如实登记，不静默）。</param>
public sealed record SupplySwapReport(
    SupplySwapOutcome Outcome,
    long BudgetBytes,
    long StagingBytes,
    long LiveBytesBefore,
    long LiveBytesAfter,
    string? StagingDirectory = null,
    string? TrashDirectory = null,
    IReadOnlyList<string>? CleanedArtifacts = null,
    string? CleanupError = null)
{
    /// <summary>清理掉的过期残留条数。</summary>
    public int CleanedArtifactCount => CleanedArtifacts?.Count ?? 0;

    /// <summary>单行可读摘要（进 job 消息与测试断言；字段名与 R6 一一对应）。</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        text.Append("outcome=").Append(Outcome);
        text.Append("；stagingBytes=").Append(StagingBytes);
        text.Append("；liveBytesBefore=").Append(LiveBytesBefore);
        text.Append("；liveBytesAfter=").Append(LiveBytesAfter);
        text.Append("；budgetBytes=").Append(BudgetBytes);

        if (CleanedArtifactCount > 0)
            text.Append("；cleaned=").Append(CleanedArtifactCount);

        if (!string.IsNullOrEmpty(CleanupError))
            text.Append("；cleanupError=").Append(CleanupError);

        return text.ToString();
    }
}

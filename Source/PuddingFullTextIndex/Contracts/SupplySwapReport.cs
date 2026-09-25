using System.Globalization;
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

    /// <summary>
    /// **回归闸门**（A22a）判为可疑回归 ⇒ 未切 live（live 一字节未动），staging 已被清理。
    /// 触发场景：staging 文档数为 0、staging 文档数远少于 live（默认阈值 0.5）、
    /// staging 文档数读不出、或 live 存在却读不出文档数（无法建立基线）。
    /// </summary>
    RejectedSuspiciousRegression = 4,
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
/// <param name="LiveDocsBefore">切换前 live 索引的**文档数**（探针实测）；live 目录不存在或读不出时为 <c>null</c>（**不得伪报 0**）。</param>
/// <param name="StagingDocs">staging 索引的**文档数**（探针实测，取切换前时刻，因为切换后 staging 目录已被搬走）；未探测到/读不出时为 <c>null</c>。</param>
/// <param name="RegressionRatio"><c>StagingDocs / LiveDocsBefore</c>；仅当两者皆可知且 live &gt; 0 时有效，否则为 <c>0</c>（此时以 <paramref name="RegressionVerdict"/> 判定，切勿把它当成「比值确为 0」的唯一依据）。</param>
/// <param name="RegressionVerdict">回归闸门的拒绝原因原文（G1~G4）；通过闸门时为 <c>null</c>。</param>
public sealed record SupplySwapReport(
    SupplySwapOutcome Outcome,
    long BudgetBytes,
    long StagingBytes,
    long LiveBytesBefore,
    long LiveBytesAfter,
    string? StagingDirectory = null,
    string? TrashDirectory = null,
    IReadOnlyList<string>? CleanedArtifacts = null,
    string? CleanupError = null,
    long? LiveDocsBefore = null,
    long? StagingDocs = null,
    double RegressionRatio = 0d,
    string? RegressionVerdict = null)
{
    /// <summary>清理掉的过期残留条数。</summary>
    public int CleanedArtifactCount => CleanedArtifacts?.Count ?? 0;

    /// <summary>
    /// <see cref="RegressionRatio"/> 是否可计算（staging 与 live 的文档数均已读出且 live &gt; 0）；
    /// 为 <c>false</c> 时该字段的 0 **不代表**「比值确实为 0」。
    /// </summary>
    public bool HasRegressionRatio => StagingDocs is not null && LiveDocsBefore is > 0;

    /// <summary>单行可读摘要（进 job 消息与测试断言；字段名与 R6 一一对应）。</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        text.Append("outcome=").Append(Outcome);
        text.Append("；stagingBytes=").Append(StagingBytes);
        text.Append("；liveBytesBefore=").Append(LiveBytesBefore);
        text.Append("；liveBytesAfter=").Append(LiveBytesAfter);
        text.Append("；budgetBytes=").Append(BudgetBytes);
        text.Append("；stagingDocs=").Append(FormatDocs(StagingDocs));
        text.Append("；liveDocs=").Append(FormatDocs(LiveDocsBefore));
        text.Append("；ratio=").Append(FormatRatio());

        if (!string.IsNullOrEmpty(RegressionVerdict))
            text.Append("；regressionVerdict=").Append(RegressionVerdict);

        if (CleanedArtifactCount > 0)
            text.Append("；cleaned=").Append(CleanedArtifactCount);

        if (!string.IsNullOrEmpty(CleanupError))
            text.Append("；cleanupError=").Append(CleanupError);

        return text.ToString();
    }

    /// <summary>文档数格式化：未知一律写 <c>&lt;null&gt;</c>（与「确实是 0」区分，不用区域性数字格式）。</summary>
    private static string FormatDocs(long? documents) =>
        documents?.ToString(CultureInfo.InvariantCulture) ?? "<null>";

    /// <summary>比值格式化：不可计算时写 <c>&lt;null&gt;</c>，不把「无意义」伪装成 0。</summary>
    private string FormatRatio() =>
        HasRegressionRatio ? RegressionRatio.ToString("0.####", CultureInfo.InvariantCulture) : "<null>";
}

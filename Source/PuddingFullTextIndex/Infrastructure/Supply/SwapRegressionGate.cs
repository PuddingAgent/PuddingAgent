using PuddingFullTextIndex.Contracts;

namespace PuddingFullTextIndex.Infrastructure.Supply;

/// <summary>
/// A22a 回归闸门的判定结果（纯值对象，无副作用）。
/// </summary>
/// <param name="Rejected">是否拒绝切换。<c>true</c> ⇒ 调用方必须清 staging、不动 live 一字节。</param>
/// <param name="LiveDocs">live 索引文档数（读不出/不存在则 <c>null</c>）。</param>
/// <param name="StagingDocs">staging 索引文档数（不存在则 <c>null</c>；存在但读不出也 <c>null</c>）。</param>
/// <param name="Ratio"><c>StagingDocs / LiveDocs</c>；不可计算时为 <c>0</c>（用 <see cref="RatioDefined"/> 区分）。</param>
/// <param name="Verdict">拒绝原因原文（G1~G4）；放行时为 <c>null</c>。</param>
internal readonly record struct SwapRegressionDecision(
    bool Rejected,
    long? LiveDocs,
    long? StagingDocs,
    double Ratio,
    string? Verdict)
{
    /// <summary>比值是否可计算（staging 已读出且 live 已读出且 &gt; 0）——为 <c>false</c> 时 <see cref="Ratio"/> 无意义。</summary>
    internal bool RatioDefined => StagingDocs is not null && LiveDocs is > 0;
}

/// <summary>
/// **暂存切换的回归闸门**（A22a R2）：体积合规**不等于**内容可信。
/// <para>
/// 2026-09-25 生产事故：仓库根 scope 的 ~98 MB live 满索引被一次只含 <b>0/99 文档</b>的构建
/// 通过原子切换静默替换（同命令 4 次中 2 次得 99/0 文件却全报 <c>Succeeded</c>）。
/// 既有两道预算闸门只看字节，因此这里补一道只看**文档数**的 fail-closed 闸门。
/// </para>
/// <para>
/// 四条规则（任一条命中 ⇒ 拒绝，live 一字节不动）：
/// <list type="bullet">
/// <item><b>G1</b>：staging 探针不可读（目录不存在，或存在但读不出文档数）—— 刚构建完就该可读，不可读即异常。</item>
/// <item><b>G2</b>：staging 文档数为 <c>0</c> —— <b>一律拒绝</b>，不得把「空索引」也算成功。</item>
/// <item><b>G3</b>：live 存在且文档数 &gt; 0，而 <c>stagingDocs &lt; liveDocs × minRatio</c> —— 可疑回归。</item>
/// <item><b>G4</b>：live 存在但文档数读不出 —— 无法建立基线（与既有「live 字节量不出就不构建」同一纪律）。</item>
/// </list>
/// <b>live 不存在</b>（首次构建）⇒ <b>允许</b>（G2 仍生效），绝不把「首次构建」误判为回归。
/// </para>
/// </summary>
internal static class SwapRegressionGate
{
    /// <summary>评估四条规则；纯函数、不抛异常、不触盘。</summary>
    internal static SwapRegressionDecision Evaluate(
        IndexDocumentProbe staging,
        IndexDocumentProbe live,
        double minRatio)
    {
        var liveDocs = live.Exists ? live.Documents : null;
        var stagingDocs = staging.Exists ? staging.Documents : null;
        var ratio = ComputeRatio(stagingDocs, liveDocs);

        // G1：staging 必须可读（目录不存在 / 读不出文档数都算不可读）。
        if (!staging.Exists || staging.Documents is null)
            return new SwapRegressionDecision(
                true, liveDocs, stagingDocs, ratio,
                $"G1：staging 索引文档数不可读（staging={staging}）⇒ 拒绝切换");

        // G2：0 文档一律拒绝（哪怕 live 也不存在 —— 空索引不得被提升为 live）。
        if (staging.Documents.Value == 0)
            return new SwapRegressionDecision(
                true, liveDocs, stagingDocs, ratio,
                "G2：staging 索引文档数为 0（近乎空的索引不得替换 live）⇒ 拒绝切换");

        // G4：live 存在却读不出文档数 ⇒ 没有基线可比，拒绝（不猜测、不按 0 处理）。
        if (live.Exists && live.Documents is null)
            return new SwapRegressionDecision(
                true, liveDocs, stagingDocs, ratio,
                $"G4：live 索引存在但文档数读不出（live={live}）⇒ 无法建立回归基线，拒绝切换");

        // G3：相对回归（只在 live 文档数 > 0 时有意义；live 不存在 = 首次构建 ⇒ 放行）。
        if (live.Documents is { } liveCount && liveCount > 0 && staging.Documents.Value < liveCount * minRatio)
            return new SwapRegressionDecision(
                true, liveDocs, stagingDocs, ratio,
                $"G3：staging 文档数 {Num(staging.Documents.Value)} < live 文档数 {Num(liveCount)} × 阈值 {Ratio(minRatio)} ⇒ 拒绝切换");

        return new SwapRegressionDecision(false, liveDocs, stagingDocs, ratio, null);
    }

    /// <summary>
    /// 终态消息里的**四个可判定数字 + 判定式**（A22a R2）：消息必须自带判据，
    /// 不接受「只报一句失败」。不可计算的量写 <c>&lt;null&gt;</c>，绝不伪报 0。
    /// </summary>
    internal static string DescribeFacts(SwapRegressionDecision decision, double minRatio)
    {
        var stagingDocs = decision.StagingDocs is { } s ? Num(s) : "<null>";
        var liveDocs = decision.LiveDocs is { } l ? Num(l) : "<null>";
        var ratio = decision.RatioDefined ? Ratio(decision.Ratio) : "<null>";
        var judgement = decision.Rejected
            ? $"(stagingDocs < liveDocs × {Ratio(minRatio)}) 或 (stagingDocs == 0) ⇒ 拒绝"
            : $"(stagingDocs >= liveDocs × {Ratio(minRatio)}) 且 (stagingDocs > 0) ⇒ 放行";

        return $"stagingDocs={stagingDocs}；liveDocs={liveDocs}；ratio={ratio}；判定式={judgement}";
    }

    /// <summary>比值：仅当 staging 已读出且 live 已读出且 &gt; 0 时计算，否则 0（是否有效由 <see cref="SwapRegressionDecision.RatioDefined"/> 判定）。</summary>
    private static double ComputeRatio(long? stagingDocs, long? liveDocs) =>
        stagingDocs is { } staging && liveDocs is { } live && live > 0
            ? (double)staging / live
            : 0d;

    /// <summary>数值一律用不变区域性格式化（消息不随区域漂移，也便于字符串断言）。</summary>
    private static string Num(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Ratio(double value) => value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
}

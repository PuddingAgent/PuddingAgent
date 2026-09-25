namespace PuddingFullTextIndex.Infrastructure.Supply;

/// <summary>
/// 「配置集合总预算」判定的**唯一口径**（A2a R2）。
/// <para>
/// 判定式只有一个：<b>live 全部 scope 索引字节 + 本次索引字节 ≤ 预算</b>。
/// 三个调用点（<c>PlanAsync</c> 的报表、staged 供给的**预检**、构建后的**实测硬限**）都必须走
/// <see cref="Fits"/>，否则会出现任务书明令禁止的「报表说行、构建说不行」。
/// </para>
/// <para>
/// ⚠️ 「集合口径」的意思：预算约束的是**全部 scope 的合计**，不是每个 scope 各自与预算比较
/// （后者会让 N 个 scope 各占满预算、总量 N 倍超标）。
/// </para>
/// </summary>
internal static class SupplyBudgetCalculator
{
    /// <summary>
    /// 解析本次生效预算：job 载荷（请求值经协调器解析后盖章）优先，否则用注入的配置默认值。
    /// 非正值一律拒绝（不猜测、不放行）。
    /// </summary>
    internal static long ResolveEffectiveBudget(long? jobBudgetBytes, long configuredBudgetBytes)
    {
        var budget = jobBudgetBytes ?? configuredBudgetBytes;
        if (budget <= 0)
            throw new ArgumentOutOfRangeException(nameof(configuredBudgetBytes), $"预算必须是正的字节数，收到 {budget}。");

        return budget;
    }

    /// <summary>
    /// 判定式（唯一）：<c>liveIndexBytes + incomingIndexBytes ≤ budgetBytes</c>。
    /// 用**减法**而不是加法，避免 <see cref="long"/> 溢出把「严重超限」折成「看起来合规」。
    /// 负数入参一律判不合规（不可能合规，且说明上游量错了）。
    /// </summary>
    internal static bool Fits(long liveIndexBytes, long incomingIndexBytes, long budgetBytes) =>
        budgetBytes > 0
        && liveIndexBytes >= 0
        && incomingIndexBytes >= 0
        && liveIndexBytes <= budgetBytes
        && incomingIndexBytes <= budgetBytes - liveIndexBytes;

    /// <summary>可读判定说明（预检与实测硬限共用同一措辞，便于把两条消息直接对照）。</summary>
    internal static string Describe(string incomingLabel, long liveIndexBytes, long incomingIndexBytes, long budgetBytes)
    {
        var relation = Fits(liveIndexBytes, incomingIndexBytes, budgetBytes) ? "≤" : ">";
        return $"live 全部 scope 索引 {liveIndexBytes} 字节 + {incomingLabel} {incomingIndexBytes} 字节 {relation} 预算 {budgetBytes} 字节（集合口径）";
    }
}

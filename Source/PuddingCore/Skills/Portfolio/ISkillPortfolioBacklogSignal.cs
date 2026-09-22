namespace PuddingCode.Skills.Portfolio;

/// <summary>
/// G8-D5：**技能组合堆积信号**（可注入）。
/// <para>
/// <b>为什么是可注入的抽象，而不是在记录处直接查询</b>：堆积判定需要「当前启用技能数」，
/// 而真正的取值路径（技能存储 / 索引）属于 Runtime 与 MemoryEngine 的实现细节。
/// 若节奏记录自带一条查询，会在阈值之外再造**第二个事实来源**；把信号抽象出来之后，
/// 判定口径（<c>enabled &gt; SoftTarget</c>）仍然只有一处。
/// </para>
/// <para>
/// ⛔ <b>未注入 ≠ 无堆积</b>：取值不可得时，调用方必须把它记成 <c>not_evaluated</c>，
/// **不得**写成 <c>false</c>（那会让读者把「没测」读成「测过且安全」）。
/// </para>
/// <para>
/// ⛔ 实现方**不得**在查询失败时返回 <c>0</c> 之类的中性值来「假装成功」——请返回 <c>null</c>
/// 或直接抛出，由调用方记 <c>not_evaluated</c>。
/// </para>
/// </summary>
public interface ISkillPortfolioBacklogSignal
{
    /// <summary>
    /// 取当前启用技能数（阈值比较由调用方完成，本接口**不**接受也不解释阈值）。
    /// </summary>
    /// <param name="agentInstanceId">目标 Agent 实例。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>
    /// 启用技能数；<c>null</c> 表示「本次未能取得事实」（调用方须记 <c>not_evaluated</c>）。
    /// </returns>
    ValueTask<int?> TryGetEnabledSkillCountAsync(string agentInstanceId, CancellationToken ct);
}

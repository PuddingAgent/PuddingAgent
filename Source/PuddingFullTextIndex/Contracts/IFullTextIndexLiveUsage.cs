namespace PuddingFullTextIndex.Contracts;

/// <summary>
/// 「live 索引已用量」的实测数据源 —— 配置集合总预算口径（A2a R2）中 live 侧的唯一出处。
/// <para>
/// 为什么需要它：预算判定的 live 侧必须在**索引根**上实测（<c>&lt;IndexRoot&gt;</c> 下全部 64 位 hex 目录的字节合计），
/// 而协调器（<c>FullTextIndexSupplyCoordinator</c>）**不持有** <c>FullTextIndexOptions</c>：
/// 它的组合根（CLI 的 <c>SupplyCliHost</c>）属本切片红线、不可改，无法给它补一个索引根参数。
/// 因此由**真正持有索引根的那个 builder**（staged 供给实现）提供实测值：
/// <c>PlanAsync</c> 在 <c>builder is IFullTextIndexLiveUsage</c> 时用它，否则按 0 计
/// （直写 builder 不参与预算 —— 与 A1 「Plan 只做报表、不做硬限」的既有语义一致）。
/// </para>
/// <para>
/// ⚠️ 判定必须与构建侧**同函数**：本接口只提供事实（字节数），判定式由
/// <c>SupplyBudgetCalculator.Fits</c> 统一给出。
/// </para>
/// </summary>
public interface IFullTextIndexLiveUsage
{
    /// <summary>
    /// 实测**全部** live scope 索引目录的字节合计（保留目录 <c>.staging</c>/<c>.trash</c>/租约/manifests 不计入）。
    /// 索引根不存在 ⇒ 0。量不出来时应抛异常（fail-closed），不得默默返回 0。
    /// </summary>
    long MeasureLiveIndexBytes();
}

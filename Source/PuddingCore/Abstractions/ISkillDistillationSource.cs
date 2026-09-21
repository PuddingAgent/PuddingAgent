using PuddingCode.Skills.Curation;

namespace PuddingCode.Abstractions;

/// <summary>
/// 提炼产物来源（**接线点**，不是生产者实现）。
/// <para>
/// 技能整理轨的"提炼器"由后续切片提供；本片（G7）只固定"产物从哪来"这一形状，
/// 使门禁**有真实消费者** —— 这是 G3 的教训：判定器当时接了线、执行器却没有消费者，
/// 于是"写好了没人用"变成死代码。
/// </para>
/// <para>
/// ⚠️ 两条硬边界：
/// ① 默认实现是**空生产者**（返回空列表）⇒ 接线后报告逐字段零回归；
/// ② 本接口的返回值**只能**驱动裁决（<c>ChangeVerdict</c>），**不得**直接驱动写盘 ——
/// 写盘职权在第 12 位 L3-b。
/// </para>
/// </summary>
public interface ISkillDistillationSource
{
    /// <summary>
    /// 取当前代理的提炼产物。无生产者时返回**空列表**（不是 <c>null</c>）。
    /// </summary>
    /// <param name="workspaceId">工作区 id。</param>
    /// <param name="agentInstanceId">代理实例 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<DistilledSkillProduct>> GetProductsAsync(
        string workspaceId,
        string agentInstanceId,
        CancellationToken cancellationToken);
}

using PuddingCode.Abstractions;

namespace PuddingCode.Skills.Curation;

/// <summary>
/// 默认**空**生产者：本片（G7）不含真实提炼器 ⇒ 恒返回空列表。
/// <para>
/// ⚠️ 这不是"兼容垫片"，而是 RSI-G7 任务书 §2.6 裁决 4 **明文要求的契约的一部分**：
/// 它让接线可被机械验收为「空生产者 ⇒ 技能整理报告逐字段零回归」。
/// 删掉它就会退回"门禁写好了没人用"的死代码风险。
/// </para>
/// </summary>
public sealed class NullSkillDistillationSource : ISkillDistillationSource
{
    /// <inheritdoc />
    public Task<IReadOnlyList<DistilledSkillProduct>> GetProductsAsync(
        string workspaceId,
        string agentInstanceId,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<DistilledSkillProduct>>([]);
}

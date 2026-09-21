namespace PuddingCode.Improvement;

/// <summary>
/// 改进提案：**统一货币**。任何改进（无论来自 RSI 信号、整理作业还是人工）都以本类型进入裁决。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由：在此之前，"改进"分散成若干条**互不相通的 add 通道** ——
/// 技能侧只增、记忆侧只加章节、代码侧直接改文件，于是
/// <list type="bullet">
/// <item>没有统一的地方回答「这次改动动了哪份既有资产、凭什么、能不能回滚」；</item>
/// <item>「新增」成为事实上的默认，而「更新/合并/淘汰」无处表达。</item>
/// </list>
/// 本类型把这两种语义显式化：<b>变更 = 目标 + 操作 + 证据</b>。
/// </para>
/// <para>
/// 本类型**只描述意图**，不执行任何落点动作，也不依赖任何具体存储（纯契约）。
/// </para>
/// </remarks>
public sealed record ImprovementProposal
{
    /// <summary>操作（决定 <see cref="Target"/> 的必填性与 <see cref="Payload"/> 的语义）。</summary>
    public required ImprovementOperation Operation { get; init; }

    /// <summary>
    /// 目标资产句柄。<b>仅当</b> <see cref="ImprovementOperation.Create"/> 时为 <c>null</c>；
    /// 其余操作必填 —— 「更新一份既有资产」而不知道更新谁，是自相矛盾的提案。
    /// </summary>
    public ArtifactRef? Target { get; init; }

    /// <summary>提案来源（Agent 实例 id / 作业名），用于审计与责任归属。</summary>
    public required string ProposedBy { get; init; }

    /// <summary>幂等键：同一份提案重复投递不得产生第二次落点动作。</summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>
    /// 证据引用（canonical 事件 id / turn id / 指标 key / 台账条目等）。
    /// <b>不得为空</b>：无证据的变更不进入裁决（否则"优化器说它更好"就是全部理由）。
    /// </summary>
    public IReadOnlyList<string> EvidenceRefs { get; init; } = [];

    /// <summary>
    /// 「为什么不能 Update / Merge 既有资产」。
    /// <para>
    /// <b>仅 <see cref="ImprovementOperation.Create"/> 必填且必须非空</b>：这是把
    /// 「add 不是默认」从口号变成**字段级强制**的地方 —— 想新增，就得先证明
    /// 「既有的那些为什么不能改、不能合并」。
    /// </para>
    /// <para>
    /// 非 Create 提案<b>不得</b>携带本字段：既然有 target，就不存在这个疑问；
    /// 两处同时表达同一件事会让事后解释产生歧义。
    /// </para>
    /// </summary>
    public string? WhyNotUpdateOrMerge { get; init; }

    /// <summary>
    /// 变更内容（<see cref="ImprovementOperation.Update"/> / <see cref="ImprovementOperation.Merge"/> /
    /// <see cref="ImprovementOperation.Replace"/> 必填；<see cref="ImprovementOperation.Create"/> 为新增物内容；
    /// <see cref="ImprovementOperation.Retire"/> 必须为空）。
    /// </summary>
    public string? Payload { get; init; }

    /// <summary>
    /// 预期收益（**未标定刻度**，仅供排序参考）。
    /// <para>
    /// ⚠️ 本字段**不得**被当作阈值输入：刻度未声明（<c>scoreScale</c>）之前，
    /// 任何"预期收益 &gt; X 就放行"的用法都是把未经标定的自报值当概率用。
    /// 且<b>无数据 ≠ 低价值</b>：缺省为 <c>null</c>，不得用 0 填充。
    /// </para>
    /// </summary>
    public double? ExpectedGain { get; init; }

    /// <summary>人类可读理由（不参与机械判定，只用于审计与排障）。</summary>
    public string? Rationale { get; init; }

    /// <summary>
    /// 校验式工厂：非法提案在**构造期**即被拒绝，调用方不必等到落点阶段才发现提案自相矛盾。
    /// </summary>
    /// <exception cref="ArgumentException">操作/来源/幂等键/证据非法，或 target 与 payload 的组合与操作不匹配。</exception>
    /// <exception cref="ArgumentOutOfRangeException">预期收益为 NaN / 无穷。</exception>
    public static ImprovementProposal Create(
        ImprovementOperation operation,
        string proposedBy,
        string idempotencyKey,
        IReadOnlyList<string> evidenceRefs,
        ArtifactRef? target = null,
        string? whyNotUpdateOrMerge = null,
        string? payload = null,
        double? expectedGain = null,
        string? rationale = null)
    {
        if (operation == ImprovementOperation.Unknown)
        {
            throw new ArgumentException("操作不得为 Unknown：无操作就无法判定该提案的必填项。", nameof(operation));
        }

        if (string.IsNullOrWhiteSpace(proposedBy))
        {
            throw new ArgumentException("提案来源不得为空：无来源的变更无法追责、无法过滤。", nameof(proposedBy));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new ArgumentException("幂等键不得为空：无幂等键的提案会重复落点。", nameof(idempotencyKey));
        }

        if (evidenceRefs is null || evidenceRefs.Count == 0)
        {
            throw new ArgumentException(
                "证据不得为空：无证据的变更不进入裁决（否则提案者的自述就是全部理由）。",
                nameof(evidenceRefs));
        }

        var hasTarget = target is not null;
        var hasWhy = !string.IsNullOrWhiteSpace(whyNotUpdateOrMerge);
        var hasPayload = !string.IsNullOrWhiteSpace(payload);

        if (operation == ImprovementOperation.Create)
        {
            if (hasTarget)
            {
                throw new ArgumentException(
                    $"Create 不得携带 target（收到 {target!.Handle}）：新增一份资产却又指定既有目标，语义自相矛盾。",
                    nameof(target));
            }

            if (!hasWhy)
            {
                throw new ArgumentException(
                    "Create 必须给出 whyNotUpdateOrMerge：新增不是默认，必须证明既有资产为什么不能更新或合并。",
                    nameof(whyNotUpdateOrMerge));
            }

            if (!hasPayload)
            {
                throw new ArgumentException("Create 必须给出 payload（新增物的内容）。", nameof(payload));
            }
        }
        else
        {
            if (!hasTarget)
            {
                throw new ArgumentException(
                    $"操作 {operation} 必须给出 target：更新/合并/取代/淘汰一份未知资产是无意义提案。",
                    nameof(target));
            }

            if (hasWhy)
            {
                throw new ArgumentException(
                    "非 Create 提案不得携带 whyNotUpdateOrMerge：既然有 target，就不存在这个疑问，两处表达同一件事会让事后解释产生歧义。",
                    nameof(whyNotUpdateOrMerge));
            }

            if (operation == ImprovementOperation.Retire)
            {
                if (hasPayload)
                {
                    throw new ArgumentException(
                        "Retire 不得携带 payload：淘汰只有「停用」这一个动作，带内容会把「淘汰」与「更新」混为一谈。",
                        nameof(payload));
                }
            }
            else if (!hasPayload)
            {
                throw new ArgumentException(
                    $"操作 {operation} 必须给出 payload：没有内容的变更不是变更（空变更只会制造 churn）。",
                    nameof(payload));
            }
        }

        if (expectedGain is double gain && (double.IsNaN(gain) || double.IsInfinity(gain)))
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedGain),
                expectedGain,
                "预期收益必须是确定数值（NaN / 无穷一律拒绝，不静默放行）。");
        }

        return new ImprovementProposal
        {
            Operation = operation,
            Target = target,
            ProposedBy = proposedBy.Trim(),
            IdempotencyKey = idempotencyKey.Trim(),
            EvidenceRefs = evidenceRefs,
            WhyNotUpdateOrMerge = hasWhy ? whyNotUpdateOrMerge!.Trim() : null,
            Payload = hasPayload ? payload : null,
            ExpectedGain = expectedGain,
            Rationale = rationale,
        };
    }

    /// <summary>
    /// 结构化相等：显式按**序列内容**比较证据引用（record 自动相等对集合成员用引用相等，会静默误判）。
    /// </summary>
    public bool Equals(ImprovementProposal? other)
        => other is not null
           && Operation == other.Operation
           && Equals(Target, other.Target)
           && string.Equals(ProposedBy, other.ProposedBy, StringComparison.Ordinal)
           && string.Equals(IdempotencyKey, other.IdempotencyKey, StringComparison.Ordinal)
           && EvidenceRefs.SequenceEqual(other.EvidenceRefs)
           && string.Equals(WhyNotUpdateOrMerge, other.WhyNotUpdateOrMerge, StringComparison.Ordinal)
           && string.Equals(Payload, other.Payload, StringComparison.Ordinal)
           && Nullable.Equals(ExpectedGain, other.ExpectedGain)
           && string.Equals(Rationale, other.Rationale, StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(Operation, Target, IdempotencyKey, EvidenceRefs.Count);
}

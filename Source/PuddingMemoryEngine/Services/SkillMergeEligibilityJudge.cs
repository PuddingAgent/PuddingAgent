using PuddingCode.Skills.Family;

namespace PuddingMemoryEngine.Services;

/// <summary>
/// 「合并四条件」里的**每一条**（任务书 §2.5 顺序）。
/// <para>
/// 为什么要枚举而不是一个 <c>bool</c>：验收判据要求**逐条可单独取红** ——
/// 一个"总判定"只能证明"整体坏了"，无法证明"是哪一条在起作用"。
/// 把每条暴露成独立观察点后，四条件各自的负例测试才能钉住各自的那一条。
/// </para>
/// </summary>
public enum SkillMergeCondition
{
    /// <summary>条件①：两个技能属于**同一个家族**（同一 <c>FamilyKey</c>，由 D1 的家族划分产生）。</summary>
    SameFamily = 0,

    /// <summary>条件②：两技能的**关键词集合有交集**（关键词已由 D4 的归一化产出）。</summary>
    KeywordsOverlap = 1,

    /// <summary>条件③：两技能的**程序性文本相似度 ≥ 策略阈值**。</summary>
    ProceduralTextSimilarity = 2,

    /// <summary>条件④：两技能的**证据可归并**（共享至少一个来源标识）。</summary>
    MergeableEvidence = 3,
}

/// <summary>
/// 一个技能的**可归并证据**：由调用方从真实技能文档投影而来，本判据只读数、不做 IO、不解析存储。
/// <para>
/// ⚠️ 字段都是**调用方从既有事实投影并校验过的**（判据只校验非空/非空白，⛔ 不重新发明第二套归一化）：
/// <see cref="Keywords"/> 应当是既有归一化（D4 的 <c>SkillKeywordNormalization</c>）的产出 ——
/// 该口径**保留原始大小写**（靠 <c>KeywordComparer</c> 做大小写不敏感比较），因此两侧的重叠比较一律走同一个比较器，
/// ⛔ 不得要求调用方先自行折叠大小写（那会变成第二套口径，且实测会让真实数据整体被拒）。
/// <see cref="EvidenceIds"/> 必须是"来源标识"（<c>source-turn:</c> / <c>source-session:</c> 的取值）。
/// </para>
/// </summary>
public sealed record SkillMergeEvidence
{
    /// <summary>技能 id（用于定位与落库解释）。</summary>
    public required string SkillId { get; init; }

    /// <summary>该技能所属家族的键（D1 家族划分的产物；单成员簇的键即技能自身）。</summary>
    public required string FamilyKey { get; init; }

    /// <summary>已归一去重的关键词集合（大小写不敏感比较，见本类型说明）。</summary>
    public required IReadOnlyList<string> Keywords { get; init; }

    /// <summary>程序性文本（技能正文/步骤）。允许为空串 —— 空正文意味着与任何技能都不相似（判据不触发）。</summary>
    public required string ProceduralText { get; init; }

    /// <summary>来源证据标识集合（同一次来源轮次/会话即视为可归并）。</summary>
    public required IReadOnlyList<string> EvidenceIds { get; init; }
}

/// <summary>一个待判定的配对（左侧 / 右侧，无方向语义：调用方可以任意顺序给出）。</summary>
public sealed record SkillMergePair
{
    /// <summary>左侧技能的证据。</summary>
    public required SkillMergeEvidence Left { get; init; }

    /// <summary>右侧技能的证据。</summary>
    public required SkillMergeEvidence Right { get; init; }
}

/// <summary>
/// 单个配对的**逐条裁决**：四条件各自的结果都在这里，因此每条都能被单独断言。
/// </summary>
public sealed record SkillMergeVerdict
{
    /// <summary>左侧技能 id。</summary>
    public required string LeftSkillId { get; init; }

    /// <summary>右侧技能 id。</summary>
    public required string RightSkillId { get; init; }

    /// <summary>本配对所属的家族键（两侧不同族时为左值，且 <see cref="FailedConditions"/> 必含同族条件）。</summary>
    public required string FamilyKey { get; init; }

    /// <summary>本次实际算得的程序性文本相似度（即使在未通过时也保留，供事后解释）。</summary>
    public required double ProceduralTextSimilarity { get; init; }

    /// <summary>**未满足**的条件（按任务书 §2.5 顺序；空 ⇒ 四条件全部满足）。</summary>
    public required IReadOnlyList<SkillMergeCondition> FailedConditions { get; init; }

    /// <summary>
    /// 四条件是否全部满足。<b>派生自 <see cref="FailedConditions"/></b>，
    /// ⛔ 不单独存储 —— 两个字段永远不可能互相矛盾。
    /// </summary>
    public bool IsEligible => FailedConditions.Count == 0;
}

/// <summary>
/// 四条件合并判据的一次运行结果（明细 = 每个配对；总量 = 由明细推导）。
/// </summary>
public sealed record SkillMergeEligibilityReport
{
    /// <summary>本次判定的配对总数（= <see cref="Verdicts"/> 的条数，⛔ 不做 Top-N 截断）。</summary>
    public required int EvaluatedPairCount { get; init; }

    /// <summary>四条件全部满足的配对数。</summary>
    public required int EligiblePairCount { get; init; }

    /// <summary>逐个配对的裁决（按技能 id 排序，保证与输入顺序无关）。</summary>
    public required IReadOnlyList<SkillMergeVerdict> Verdicts { get; init; }

    /// <summary>本次判定消费的策略标识（<c>{policyId}@{version}</c>），供事后解释。</summary>
    public required string PolicyRef { get; init; }
}

/// <summary>
/// RSI-G4 交付物 D6 的判据本体：**合并四条件**（同家族 ∧ 关键词重叠 ∧ 程序性文本相似 ≥ 阈值 ∧ 证据可归并）。
/// <para>
/// <b>它是什么</b>：一个纯函数式、确定性的**只读**判据 —— 无 IO、无 LLM、无写入、无副作用；
/// 同一输入必须得到同一裁决（含明细顺序）。它的输出只供"可行性探针 / 后续切片"消费，
/// ⛔ 本类型不执行任何合并动作（不落库、不合并、不禁用技能）。
/// </para>
/// <para>
/// <b>与既有闸门的关系</b>（任务书 §2.5）：<c>IsDeterministicallyEligible</c> 是**另一条**判据
/// （工具集合完全相等 + 元数据文本 ≥ 0.20 + (共享 turn 或 ≥ 0.35)），⛔ 本类型不替换、不放宽、
/// 也不内联它。探针需要"叠加后的数字"时，由**调用方**在得到本判据的合格配对后另行过闸门统计。
/// </para>
/// <para>
/// <b>相似度必须注入</b>：程序性文本相似度的**实现**由调用方给出（<see cref="Func{T1,T2,TResult}"/>），
/// 以便复用既有那一份（<c>SkillEvolutionDeduplicationService.CalculateTextSimilarity</c> 的 token-Jaccard），
/// ⛔ 本类型不得自带第二套相似度实现。阈值则一律来自 <see cref="SkillMergePolicy"/>，⛔ 不得写死字面量。
/// </para>
/// </summary>
public sealed class SkillMergeEligibilityJudge
{
    private readonly Func<string, string, double> _proceduralTextSimilarity;

    /// <param name="proceduralTextSimilarity">
    /// 程序性文本相似度实现（左文本, 右文本 ⇒ [0,1]）。必须非空；
    /// 返回若为 NaN/±∞ 视为调用方缺陷，直接抛错（而非静默判为"不相似"）。
    /// </param>
    /// <exception cref="ArgumentNullException">相似度实现为空时抛出。</exception>
    public SkillMergeEligibilityJudge(Func<string, string, double> proceduralTextSimilarity)
    {
        _proceduralTextSimilarity = proceduralTextSimilarity
            ?? throw new ArgumentNullException(nameof(proceduralTextSimilarity));
    }

    /// <summary>
    /// 对一批配对施加四条件判据。
    /// </summary>
    /// <exception cref="ArgumentNullException">入参为空时抛出。</exception>
    /// <exception cref="InvalidOperationException">
    /// 策略非法、或配对事实畸形（同技能自配对 / 同配对重复 / 字段缺失或不合契约）时抛出。
    /// 畸形输入一律**拒绝**，⛔ 不得静默跳过（静默跳过会让"评估了 N 个配对"变成谎话）。
    /// </exception>
    public SkillMergeEligibilityReport Apply(
        IReadOnlyList<SkillMergePair> pairs,
        SkillMergePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(policy);
        policy.EnsureValid();

        var verdicts = new List<SkillMergeVerdict>(pairs.Count);
        var seenPairs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pair in pairs)
        {
            ArgumentNullException.ThrowIfNull(pair);
            var left = pair.Left;
            var right = pair.Right;
            ArgumentNullException.ThrowIfNull(left);
            ArgumentNullException.ThrowIfNull(right);

            ValidateEvidence(left, nameof(SkillMergePair.Left));
            ValidateEvidence(right, nameof(SkillMergePair.Right));

            if (string.Equals(left.SkillId, right.SkillId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"合并判据拒绝自配对：技能 `{left.SkillId}` 出现在配对两侧。");
            }

            if (!seenPairs.Add(BuildPairKey(left.SkillId, right.SkillId)))
            {
                throw new InvalidOperationException(
                    $"合并判据拒绝重复配对：`{left.SkillId}` 与 `{right.SkillId}` 被判定超过一次。");
            }

            var sameFamily = string.Equals(left.FamilyKey, right.FamilyKey, StringComparison.Ordinal);
            // C2 关键词重叠：必须与 D4 的归一口径用**同一个**比较器（大小写不敏感）——
            // 否则"已归一"的定义会在两处各自演化，且真实 manifest 里存在名形式大小写关键词。
            var keywordsOverlap = left.Keywords
                .Intersect(right.Keywords, PuddingCode.Skills.Retrieval.SkillKeywordNormalization.KeywordComparer)
                .Any();
            var similarity = _proceduralTextSimilarity(left.ProceduralText, right.ProceduralText);
            if (!double.IsFinite(similarity))
            {
                throw new InvalidOperationException(
                    $"程序性文本相似度实现返回了非有限值（{similarity}）：`{left.SkillId}` 与 `{right.SkillId}`。");
            }

            var textSimilarityPasses = similarity >= policy.MinimumProceduralTextSimilarity;
            var evidenceMergeable = left.EvidenceIds.Intersect(right.EvidenceIds, StringComparer.Ordinal).Any();

            var failed = new List<SkillMergeCondition>(4);
            if (!sameFamily)
                failed.Add(SkillMergeCondition.SameFamily);
            if (!keywordsOverlap)
                failed.Add(SkillMergeCondition.KeywordsOverlap);
            if (!textSimilarityPasses)
                failed.Add(SkillMergeCondition.ProceduralTextSimilarity);
            if (!evidenceMergeable)
                failed.Add(SkillMergeCondition.MergeableEvidence);

            verdicts.Add(new SkillMergeVerdict
            {
                LeftSkillId = left.SkillId,
                RightSkillId = right.SkillId,
                FamilyKey = left.FamilyKey,
                ProceduralTextSimilarity = similarity,
                FailedConditions = failed,
            });
        }

        // 明细顺序只取决于技能 id（不取决于输入顺序或调用次数）⇒ 同一输入必得同一结果。
        verdicts.Sort(static (x, y) =>
        {
            var byLeft = string.CompareOrdinal(x.LeftSkillId, y.LeftSkillId);
            return byLeft != 0 ? byLeft : string.CompareOrdinal(x.RightSkillId, y.RightSkillId);
        });

        return new SkillMergeEligibilityReport
        {
            EvaluatedPairCount = verdicts.Count,
            EligiblePairCount = verdicts.Count(static verdict => verdict.IsEligible),
            Verdicts = verdicts,
            PolicyRef = $"{policy.PolicyId}@{policy.Version}",
        };
    }

    private static void ValidateEvidence(SkillMergeEvidence evidence, string side)
    {
        if (string.IsNullOrWhiteSpace(evidence.SkillId))
        {
            throw new InvalidOperationException($"合并判据拒绝缺字段的证据（{side}）：SkillId 为空。");
        }

        if (string.IsNullOrWhiteSpace(evidence.FamilyKey))
        {
            throw new InvalidOperationException(
                $"合并判据拒绝缺字段的证据（{side}，技能 `{evidence.SkillId}`）：FamilyKey 为空。"
                + "未参与任何家族划分的技能不得进入合并判定。");
        }

        if (evidence.Keywords is null)
        {
            throw new InvalidOperationException($"合并判据拒绝缺字段的证据（{side}，技能 `{evidence.SkillId}`）：Keywords 为 null。");
        }

        if (evidence.Keywords.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException(
                $"合并判据拒绝空白关键词（{side}，技能 `{evidence.SkillId}`）：空白关键词不携带可比较信息，却会参与重叠判定。");
        }

        if (evidence.ProceduralText is null)
        {
            throw new InvalidOperationException($"合并判据拒绝缺字段的证据（{side}，技能 `{evidence.SkillId}`）：ProceduralText 为 null。");
        }

        if (evidence.EvidenceIds is null)
        {
            throw new InvalidOperationException($"合并判据拒绝缺字段的证据（{side}，技能 `{evidence.SkillId}`）：EvidenceIds 为 null。");
        }

        if (evidence.EvidenceIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException(
                $"合并判据拒绝空白的来源证据标识（{side}，技能 `{evidence.SkillId}`）。");
        }
    }

    /// <summary>无向配对键（两侧顺序不影响身份），用于拒绝重复判定同一配对。</summary>
    private static string BuildPairKey(string first, string second)
        => string.CompareOrdinal(first, second) <= 0
            ? $"{first}\u001f{second}"
            : $"{second}\u001f{first}";
}

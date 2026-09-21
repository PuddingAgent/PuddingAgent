using System.Globalization;
using PuddingCode.Skills.Portfolio;

namespace PuddingMemoryEngine.Services;

/// <summary>
/// 预算判定器的输入：既有去重结论 + **带刻度的**候选分 / 已启用最低分 + 当前启用数。
/// <para>
/// ⚠️ 输入里<b>不含任何 LLM 输出</b>：LLM 的影响只能经由 <see cref="DedupResult"/> 进入，
/// 而判定器对它是**只能下调、不能上调**（见 <see cref="SkillPortfolioAdmissionJudge"/> 的 no-upgrade 不变式）。
/// 这就是「预算不可被 LLM 越过」的可判定形式。
/// </para>
/// </summary>
public sealed record SkillPortfolioInput
{
    /// <summary>当前**启用**技能数（由调用方从技能仓实测，不得估算）。</summary>
    public required int EnabledCount { get; init; }

    /// <summary>既有去重/准入服务的结论（LLM 结论只能到这里，之后只允许被下调）。</summary>
    public required SkillAdmissionResult DedupResult { get; init; }

    /// <summary>候选项的价值分快照。无观测数据时必须传 <see cref="SkillScoreSnapshot.Unavailable"/>。</summary>
    public SkillScoreSnapshot? CandidateScore { get; init; }

    /// <summary>已启用技能中的**最低**价值分快照（置换目标即它的持有者）。</summary>
    public SkillScoreSnapshot? MinEnabledScore { get; init; }

    /// <summary>最低分持有者的技能 id。置换时作为被禁用目标。</summary>
    public string? MinEnabledSkillId { get; init; }
}

/// <summary>
/// 技能组合**预算判定器**（纯判定，零 IO、零 LLM、零裸阈值）。
/// <para>
/// 职责边界：本类型<b>只做预算裁决</b>，不查技能仓、不算分、不写盘。启用数、候选分、最低分
/// 全部由调用方传入 —— 这样它才能被穷举式单测（含"预算已满但 LLM 要求建新技能"这类用例），
/// 也才能在类型上证明"LLM 无法越过预算"。
/// </para>
/// <para>
/// 判定结果复用既有 <see cref="SkillAdmissionActions"/> 动作空间（新增 <c>displace</c>），
/// **不新建第二套动作枚举**，以免同一个"准入动作"概念出现两个真相源。
/// </para>
/// </summary>
/// <param name="policy">预算策略（阈值唯一来源；判定器内不得出现裸阈值）。</param>
public sealed class SkillPortfolioAdmissionJudge(SkillPortfolioPolicy policy)
{
    private readonly SkillPortfolioPolicy _policy =
        policy ?? throw new ArgumentNullException(nameof(policy));

    /// <summary>本判定器使用的策略（供调用方落库/日志，说明结论用的是哪一版）。</summary>
    public SkillPortfolioPolicy Policy => _policy;

    /// <summary>
    /// 应用预算判定。返回的 <see cref="SkillAdmissionResult"/> 可直接交给既有的
    /// <c>create</c> / <c>merge</c> / <c>skip</c> / <c>defer</c> / <c>displace</c> 分支消费。
    /// </summary>
    /// <remarks>
    /// 判定序（fail-closed）：
    /// <list type="number">
    /// <item>非 <c>create</c> 结论 ⇒ <b>原样返回</b>（no-upgrade：预算层永不把 merge/skip/defer 提升为 create）；</item>
    /// <item><c>enabled &lt; SoftTarget</c> ⇒ 原样返回（软目标以下完全走既有去重，行为与接线前一致）；</item>
    /// <item>分数不可比（含冷启动）⇒ 降级 <c>defer</c>，<b>禁止置换</b>；</item>
    /// <item><c>enabled ≥ HardCap</c> ⇒ 边际收益够 ⇒ <c>displace</c>（目标 = 最低分持有者）；不够或<b>无置换目标</b> ⇒ <c>defer</c>；</item>
    /// <item><c>SoftTarget ≤ enabled &lt; HardCap</c> ⇒ 边际收益够 ⇒ 准入；不够 ⇒ <c>defer</c>。</item>
    /// </list>
    /// <b>关于 §13.5 里的「Merge / Defer」</b>：本切片把 <c>merge</c> 保留给既有去重层的裁决 ——
    /// 预算层若凭空返回 <c>merge</c>，就等于替去重层"发明"一条并不存在的合并关系。
    /// 因此预算不足时一律 <c>defer</c>（延后再评），并在理由里区分"边际不足"与"无置换目标"。
    /// </remarks>
    /// <exception cref="InvalidOperationException">策略配置非法。</exception>
    public SkillAdmissionResult Apply(SkillPortfolioInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        _policy.EnsureValid();

        var dedup = input.DedupResult
            ?? throw new ArgumentNullException(nameof(input), "DedupResult 不得为 null。");

        // no-upgrade（S3）：预算层只能收紧既有结论，永不放宽。
        if (!string.Equals(dedup.Action, SkillAdmissionActions.Create, StringComparison.Ordinal))
        {
            return dedup;
        }

        // 行 1：软目标以下 ⇒ 完全走既有去重（零回归区间）。
        if (input.EnabledCount < _policy.SoftTarget)
        {
            return dedup;
        }

        // 行 2/3 需要可比分数。
        if (!TryResolveScores(input, out var candidateScore, out var minEnabledScore, out var unavailable))
        {
            // S2：冷启动只降不杀 —— 无数据 ⇒ 延后，绝不置换。
            return Downgrade(
                dedup,
                SkillAdmissionActions.Defer,
                policyId: _policy.PolicyId,
                policyVersion: _policy.Version,
                detail: unavailable);
        }

        var margin = candidateScore - minEnabledScore;
        var required = _policy.MinMarginalGain;

        if (input.EnabledCount >= _policy.HardCap)
        {
            // 行 3：预算已满 ⇒ 必须置换才有位置。
            if (string.IsNullOrWhiteSpace(input.MinEnabledSkillId))
            {
                // fail-closed：没有可置换目标就不许"先建后算"，否则硬上限形同虚设。
                return Downgrade(
                    dedup,
                    SkillAdmissionActions.Defer,
                    _policy.PolicyId,
                    _policy.Version,
                    BuildDetail(
                        "displace_target_missing",
                        input.EnabledCount,
                        margin,
                        required));
            }

            if (margin > required)
            {
                return new SkillAdmissionResult
                {
                    Action = SkillAdmissionActions.Displace,
                    TargetSkillId = input.MinEnabledSkillId,
                    Confidence = dedup.Confidence,
                    Reason = BuildDetail(
                        "displace_lowest_value",
                        input.EnabledCount,
                        margin,
                        required),
                };
            }

            return Downgrade(
                dedup,
                SkillAdmissionActions.Defer,
                _policy.PolicyId,
                _policy.Version,
                BuildDetail("insufficient_margin_at_hard_cap", input.EnabledCount, margin, required));
        }

        // 行 2：软区间 ⇒ 需证明边际收益。
        if (margin > required)
        {
            return dedup;
        }

        return Downgrade(
            dedup,
            SkillAdmissionActions.Defer,
            _policy.PolicyId,
            _policy.Version,
            BuildDetail("insufficient_margin_above_soft_target", input.EnabledCount, margin, required));
    }

    /// <summary>
    /// 两侧快照都必须是**可比分数**且**同刻度**：刻度不同意味着量纲不同，直接相减就是错的。
    /// </summary>
    private static bool TryResolveScores(
        SkillPortfolioInput input,
        out double candidateScore,
        out double minEnabledScore,
        out string unavailableDetail)
    {
        candidateScore = 0d;
        minEnabledScore = 0d;

        if (input.CandidateScore is not SkillScoreSnapshot.Observed candidate)
        {
            unavailableDetail = "candidate_score_unavailable";
            return false;
        }

        if (input.MinEnabledScore is not SkillScoreSnapshot.Observed minEnabled)
        {
            unavailableDetail = "min_enabled_score_unavailable";
            return false;
        }

        if (!string.Equals(candidate.ScoreScale, minEnabled.ScoreScale, StringComparison.Ordinal))
        {
            unavailableDetail = "score_scale_mismatch";
            return false;
        }

        candidateScore = candidate.Score;
        minEnabledScore = minEnabled.Score;
        unavailableDetail = string.Empty;
        return true;
    }

    private static SkillAdmissionResult Downgrade(
        SkillAdmissionResult dedup,
        string action,
        string policyId,
        int policyVersion,
        string detail)
        => new()
        {
            Action = action,
            TargetSkillId = null,
            // 预算层不重新推导语义置信：沿用去重结论的置信，判定依据放在 Reason 里，避免读者
            // 误以为这次降级是按置信度做的。宁可留白，也不编造一个看起来能用的数。
            Confidence = dedup.Confidence,
            Reason = "budget:" + detail + ";policy=" + policyId
                     + "@" + policyVersion.ToString(CultureInfo.InvariantCulture),
        };

    private static string BuildDetail(string code, int enabledCount, double margin, double required)
        => code
           + ";enabled=" + enabledCount.ToString(CultureInfo.InvariantCulture)
           + ";margin=" + margin.ToString("F4", CultureInfo.InvariantCulture)
           + ";required_margin=" + required.ToString("F4", CultureInfo.InvariantCulture);
}

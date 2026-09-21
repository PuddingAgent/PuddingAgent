using PuddingCode.Improvement;
using PuddingCode.Skills.Curation;
using PuddingCode.Skills.Portfolio;

namespace PuddingMemoryEngine.Services;

/// <summary>
/// 门禁输入里的**在册技能最小快照**（纯数据，零 IO）。
/// <para>
/// ⚠️ 只承载**被消费**的字段：C1 用 <see cref="Tags"/>，C3 用 <see cref="Keywords"/>，C4 用 <see cref="Score"/>。
/// 不在这里放 <c>IsEnabled</c> / 时间戳之类"看起来有用"的字段 —— 未被消费的字段会让读者误判判定依据。
/// </para>
/// </summary>
public sealed record SkillCurationSkillSnapshot
{
    /// <summary>技能 id（C3 用它把"本次被取代者"排除出判定域，见 RSI-G7 任务书 §2.6 裁决 1）。</summary>
    public required string SkillId { get; init; }

    /// <summary>技能 tags（provenance 唯一载体：<c>source-turn:*</c> / <c>source-session:*</c>）。</summary>
    public required IReadOnlyList<string> Tags { get; init; }

    /// <summary>技能关键词（C3 的判定域来源；与注入侧 <c>SkillEnforcerService</c> 同源，都是技能的 <c>Keywords</c>）。</summary>
    public required IReadOnlyList<string> Keywords { get; init; }

    /// <summary>价值分快照（两态）：无观测数据时必须传 <see cref="SkillScoreSnapshot.Unavailable"/>。</summary>
    public required SkillScoreSnapshot Score { get; init; }
}

/// <summary>
/// 门禁输入：提炼产物 + 被取代者 + 仍将保持启用的其它技能 + 请求的决策/操作。
/// <para>
/// <see cref="RequestedDecision"/> / <see cref="RequestedOperation"/> / <see cref="RollbackHandle"/> 来自**请求方**
/// （将来的 LLM 提炼器）；它们是"意图"，**不是**裁决 —— 能否落点只由本门禁产出的
/// <see cref="ChangeVerdict"/> 决定（提案者不能自己给自己发通行证）。
/// </para>
/// </summary>
public sealed record SkillCurationRequest
{
    /// <summary>待裁决的提炼产物。</summary>
    public required DistilledSkillProduct Product { get; init; }

    /// <summary>被本次整理取代的技能（C1/C2/C4 的判定域，也是"禁字面量"的来源）。</summary>
    public required IReadOnlyList<SkillCurationSkillSnapshot> ReplacedSkills { get; init; }

    /// <summary>**仍将保持启用**的其它技能（C3 的判定域；本次被取代者即使出现在这里也会被排除）。</summary>
    public required IReadOnlyList<SkillCurationSkillSnapshot> RetainedSkills { get; init; }

    /// <summary>产物的价值分快照（无观测数据时传 <see cref="SkillScoreSnapshot.Unavailable"/>）。</summary>
    public required SkillScoreSnapshot ProductScore { get; init; }

    /// <summary>请求的操作。本片产物**只允许** Merge / Replace / Retire（Create 不是默认，见任务书 §2.5）。</summary>
    public required ImprovementOperation RequestedOperation { get; init; }

    /// <summary>请求的决策。本片**不得**产出 Apply（无写盘职权），Apply 请求会被降级为 shadow。</summary>
    public required ChangeDecision RequestedDecision { get; init; }

    /// <summary>
    /// 工具名谓词（P4）。⚠️ **必须由调用方注入**：工具名的唯一来源是记忆引擎的
    /// <c>SkillEvolutionDeduplicationService.ToolKeywordRegex()</c>，而契约层（<c>PuddingCore</c>）不得反向依赖本程序集。
    /// </summary>
    public required Func<string, bool> IsToolLikeKeyword { get; init; }

    /// <summary>请求方提供的回滚句柄。仅在 <see cref="ChangeDecision.Apply"/> 时才有意义（C5）。</summary>
    public string? RollbackHandle { get; init; }
}

/// <summary>单条判据的结果（逐条留痕，供事后解释"为什么拒/为什么放"）。</summary>
public sealed record SkillCurationCriterionResult
{
    /// <summary>判据标识：<c>contract</c> / <c>C1</c>…<c>C5</c> / <c>operation</c>。</summary>
    public required string Criterion { get; init; }

    /// <summary>是否通过。⚠️ 冷启动降级时**仍为 true**（记录但不阻断）。</summary>
    public required bool Passed { get; init; }

    /// <summary>人类可读补充（含降级码或违规明细）。</summary>
    public string? Detail { get; init; }
}

/// <summary>门禁输出：唯一凭据 + 逐条判据留痕 + 结构化明细。</summary>
public sealed record SkillCurationGateOutcome
{
    /// <summary>裁决（唯一允许驱动落点动作的凭据；本片**只会**产出 <c>Reject</c> / <c>ApplyShadow</c>）。</summary>
    public required ChangeVerdict Verdict { get; init; }

    /// <summary>逐条判据结果（顺序固定：contract, C1, C2, C3, C4, C5, operation）。</summary>
    public required IReadOnlyList<SkillCurationCriterionResult> Criteria { get; init; }

    /// <summary>P1–P6 违规码。</summary>
    public required IReadOnlyList<string> ContractViolations { get; init; }

    /// <summary>C1：被取代者有过、新产物漏掉的 source turn（<b>理由码里会带上它们</b>）。</summary>
    public required IReadOnlyList<string> MissingSourceTurns { get; init; }

    /// <summary>C3：与"仍将保持启用"的第三方技能相交的关键词。</summary>
    public required IReadOnlyList<string> KeywordCollisions { get; init; }

    /// <summary>可用性说明（冷启动降级码）。**不阻断**：无数据 ≠ 低价值。</summary>
    public required IReadOnlyList<string> AvailabilityNotes { get; init; }

    /// <summary>是否存在阻断性发现。true ⇒ 裁决必为 <c>Reject</c>。</summary>
    public required bool HasBlockingFindings { get; init; }
}

/// <summary>
/// 技能整理门禁（C1–C5）：**纯判定，零 IO、零 LLM、零裸阈值、零写盘**。
/// <para>
/// 职责边界：本类型不查技能仓、不算分、不写盘、不禁用任何技能 —— 被取代者快照、价值分、关键词
/// 全部由调用方传入（这样它才能被穷举式单测）。<b>写盘职权在第 12 位 L3-b</b>，本片连"顺手禁用被取代者"都不做。
/// </para>
/// <para>
/// 五条判据（fail-closed，见设计 §14.6 与本片任务书 §2.2）：
/// <list type="number">
/// <item><b>C1 证据不丢</b>：被取代者出现过的全部 <c>source-turn</c> ⊆ 新产物证据 ⇒ 否则 <c>Reject</c>；</item>
/// <item><b>C2 一般性不降</b>：<c>coveringSessions(new) ≥ max(被取代者)</c>；被取代者**全无** session tag ⇒ 降级记录；</item>
/// <item><b>C3 关键词唯一</b>：与**仍将保持启用**的第三方技能无交集（被取代者已被排除，否则必然误报）；</item>
/// <item><b>C4 价值不降</b>：<c>productValue ≥ ratio × Σ(被取代者)</c>；任一侧无数据/刻度不一致 ⇒ 降级记录；</item>
/// <item><b>C5 可回滚</b>：<c>Apply</c> 必须带句柄（缺 ⇒ 构造期抛异常 ⇒ 本门禁转 <c>Reject</c>）；且**一律禁用语义、不引入删除**。</item>
/// </list>
/// </para>
/// </summary>
/// <param name="policy">整理策略（阈值唯一来源；门禁内不得出现裸阈值）。</param>
public sealed class SkillCurationGate(SkillCurationPolicy policy)
{
    /// <summary>理由码：产物违反反笔记不变式（P1–P6），后缀带具体违规码。</summary>
    public const string ReasonContractViolated = "curation:contract_violated";

    /// <summary>理由码：证据丢失，后缀带缺失 turn（可 grep、可用于回归）。</summary>
    public const string ReasonEvidenceLost = "curation:c1_evidence_lost";

    /// <summary>理由码：一般性下降，后缀带 <c>new&lt;max</c> 实测计数。</summary>
    public const string ReasonGeneralityReduced = "curation:c2_generality_reduced";

    /// <summary>理由码：关键词与第三方启用技能相撞，后缀带交集明细。</summary>
    public const string ReasonKeywordCollision = "curation:c3_keyword_collision";

    /// <summary>理由码：价值低于保留比例下限。</summary>
    public const string ReasonValueReduced = "curation:c4_value_reduced";

    /// <summary>理由码：请求 Apply 但缺可回滚句柄（<c>ChangeVerdict</c> 构造期强制所致）。</summary>
    public const string ReasonRollbackUnavailable = "curation:c5_rollback_unavailable";

    /// <summary>理由码：请求的决策非法（<c>Unknown</c> 不得当放行）。</summary>
    public const string ReasonInvalidRequestedDecision = "curation:invalid_requested_decision";

    /// <summary>理由码：请求 <c>Create</c> —— 「Add 不是默认」（L3-a 字段级强制）。</summary>
    public const string ReasonAddNotDefault = "curation:add_not_default";

    /// <summary>理由码：无阻断性发现 ⇒ shadow（本片无写盘职权）。</summary>
    public const string ReasonNoBlockingFindings = "curation:no_blocking_findings";

    /// <summary>理由码：请求了 Apply 且句柄齐备，但本片无写盘职权 ⇒ 降级为 shadow。</summary>
    public const string ReasonShadowOnly = "curation:shadow_only_no_write_authority";

    /// <summary>降级码：被取代者全无 <c>source-session:</c> tag ⇒ C2 算不出来 ⇒ 记录但不阻断。</summary>
    public const string AvailabilityCoveringSessionsUnavailable = "cold_start:covering_sessions_unavailable";

    /// <summary>降级码：任一侧价值分不可比 ⇒ C4 算不出来 ⇒ 记录但不阻断。</summary>
    public const string AvailabilityValueScoreUnavailable = "cold_start:value_score_unavailable";

    /// <summary>降级码：价值分刻度不一致（不是冷启动，是口径冲突）⇒ 记录但不阻断。</summary>
    public const string AvailabilityScoreScaleMismatch = "score_scale_mismatch:value_score_unavailable";

    private readonly SkillCurationPolicy _policy =
        policy ?? throw new ArgumentNullException(nameof(policy));

    /// <summary>本门禁使用的策略（供调用方落库/日志，说明结论用的是哪一版）。</summary>
    public SkillCurationPolicy Policy => _policy;

    /// <summary>
    /// 裁决一条提炼产物。返回的 <see cref="SkillCurationGateOutcome.Verdict"/> 是**唯一**可驱动落点的凭据。
    /// </summary>
    /// <exception cref="ArgumentNullException">请求 / 产物 / 工具名谓词为 null（fail-closed，不静默放行）。</exception>
    public SkillCurationGateOutcome Evaluate(SkillCurationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _policy.EnsureValid();

        if (request.Product is null)
        {
            throw new ArgumentNullException(nameof(request), "产物不得为 null：无产物即无可裁决对象。");
        }

        if (request.IsToolLikeKeyword is null)
        {
            throw new ArgumentNullException(nameof(request), "工具名谓词不得为 null：P4 无法判定即不得放行。");
        }

        var replaced = request.ReplacedSkills ?? [];
        var retained = request.RetainedSkills ?? [];
        var product = request.Product;
        var productTags = SafeList(product.EvidenceTags);
        var productKeywords = SafeList(product.Keywords);

        var replacedIds = CollectReplacedIds(product, replaced);
        var allReplacedTags = replaced.SelectMany(snapshot => SafeList(snapshot.Tags)).ToList();

        // ── P1–P6：反笔记不变式（禁字面量 = 真实 provenance 里的 turn/session id，绝不用正则猜） ──
        var forbiddenLiterals = SkillDistillationContract.SourceTurnsOf(allReplacedTags)
            .Concat(SkillDistillationContract.SourceSessionsOf(allReplacedTags))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var contractViolations = SkillDistillationContract.Validate(
            product, forbiddenLiterals, request.IsToolLikeKeyword, _policy);

        // ── C1 证据不丢 ──
        var newTurns = SkillDistillationContract.SourceTurnsOf(productTags);
        var missingTurns = SkillDistillationContract.SourceTurnsOf(allReplacedTags)
            .Where(turn => !newTurns.Contains(turn, StringComparer.OrdinalIgnoreCase))
            .ToList();

        // ── C2 一般性不降（口径 = source-session: tag 去重计数；不是"被注入过的会话数"） ──
        var availabilityNotes = new List<string>();
        var newSessionCount = SkillDistillationContract.SourceSessionsOf(productTags).Count;
        var replacedSessionCounts = replaced
            .Select(snapshot => SkillDistillationContract.SourceSessionsOf(SafeList(snapshot.Tags)).Count)
            .ToList();
        var maxReplacedSessions = replacedSessionCounts.Count == 0 ? 0 : replacedSessionCounts.Max();
        var sessionDataAvailable = replacedSessionCounts.Exists(count => count > 0);
        if (!sessionDataAvailable)
        {
            availabilityNotes.Add(AvailabilityCoveringSessionsUnavailable);
        }

        var generalityReduced = sessionDataAvailable && newSessionCount < maxReplacedSessions;

        // ── C3 关键词唯一（判定域**必须排除本次被取代者**，否则合并自身关键词必然误报） ──
        var domainKeywords = retained
            .Where(snapshot => !replacedIds.Contains((snapshot.SkillId ?? string.Empty).Trim()))
            .SelectMany(snapshot => SafeList(snapshot.Keywords))
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .Select(keyword => keyword.Trim())
            .ToList();
        var collisions = productKeywords
            .Where(keyword => domainKeywords.Contains(keyword, StringComparer.OrdinalIgnoreCase))
            .Select(keyword => keyword.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // ── C4 价值不降（任一侧不可比 ⇒ 降级记录，禁止把"无数据"当 0） ──
        var valueReduced = false;
        if (request.ProductScore is not SkillScoreSnapshot.Observed productObserved)
        {
            availabilityNotes.Add(AvailabilityValueScoreUnavailable);
        }
        else if (replaced.Any(snapshot => snapshot.Score is not SkillScoreSnapshot.Observed))
        {
            availabilityNotes.Add(AvailabilityValueScoreUnavailable);
        }
        else
        {
            var replacedObserved = replaced
                .Select(snapshot => (SkillScoreSnapshot.Observed)snapshot.Score)
                .ToList();
            if (replacedObserved.Exists(observed => !string.Equals(
                    observed.ScoreScale, productObserved.ScoreScale, StringComparison.OrdinalIgnoreCase)))
            {
                availabilityNotes.Add(AvailabilityScoreScaleMismatch);
            }
            else
            {
                var replacedTotal = replacedObserved.Sum(observed => observed.Score);
                valueReduced = productObserved.Score < _policy.MinRetainedValueRatio * replacedTotal;
            }
        }

        // ── operation：Add 不是默认 ──
        var operationAllowed = request.RequestedOperation
            is ImprovementOperation.Merge or ImprovementOperation.Replace or ImprovementOperation.Retire;

        // ── 阻断性发现（顺序固定，保证理由码可回归） ──
        var reasons = new List<string>();
        if (contractViolations.Count > 0)
        {
            reasons.Add($"{ReasonContractViolated}:{string.Join("+", contractViolations)}");
        }

        if (missingTurns.Count > 0)
        {
            reasons.Add($"{ReasonEvidenceLost}:{string.Join("+", missingTurns)}");
        }

        if (generalityReduced)
        {
            reasons.Add($"{ReasonGeneralityReduced}:{newSessionCount}<{maxReplacedSessions}");
        }

        if (collisions.Count > 0)
        {
            reasons.Add($"{ReasonKeywordCollision}:{string.Join("+", collisions)}");
        }

        if (valueReduced)
        {
            reasons.Add(ReasonValueReduced);
        }

        if (!operationAllowed)
        {
            reasons.Add($"{ReasonAddNotDefault}:{request.RequestedOperation}");
        }

        var criteria = new List<SkillCurationCriterionResult>
        {
            new()
            {
                Criterion = "contract",
                Passed = contractViolations.Count == 0,
                Detail = contractViolations.Count == 0 ? null : string.Join(",", contractViolations),
            },
            new()
            {
                Criterion = "C1",
                Passed = missingTurns.Count == 0,
                Detail = missingTurns.Count == 0 ? null : string.Join(",", missingTurns),
            },
            new()
            {
                Criterion = "C2",
                Passed = !generalityReduced,
                Detail = !sessionDataAvailable
                    ? AvailabilityCoveringSessionsUnavailable
                    : $"new={newSessionCount}, maxReplaced={maxReplacedSessions}",
            },
            new()
            {
                Criterion = "C3",
                Passed = collisions.Count == 0,
                Detail = collisions.Count == 0 ? null : string.Join(",", collisions),
            },
            new()
            {
                Criterion = "C4",
                Passed = !valueReduced,
                Detail = valueReduced ? $"ratio={_policy.MinRetainedValueRatio}" : null,
            },
            new()
            {
                Criterion = "C5",
                Passed = true,
                Detail = "本片**禁用而非删除**：门禁不表达 Delete，写盘职权在 L3-b。",
            },
            new()
            {
                Criterion = "operation",
                Passed = operationAllowed,
                Detail = operationAllowed ? null : $"{request.RequestedOperation} 不是整理产物允许的操作",
            },
        };

        var verdict = BuildVerdict(request, reasons, availabilityNotes);

        return new SkillCurationGateOutcome
        {
            Verdict = verdict,
            Criteria = criteria,
            ContractViolations = contractViolations,
            MissingSourceTurns = missingTurns,
            KeywordCollisions = collisions,
            AvailabilityNotes = availabilityNotes,
            HasBlockingFindings = reasons.Count > 0,
        };
    }

    /// <summary>
    /// 构造裁决。<b>一律</b>经由 <see cref="ChangeVerdict.Create"/>（禁止绕过构造器直接 <c>new</c>），
    /// 于是三条构造期硬约束在本路径上**真的被走到**：
    /// ① <c>Unknown</c> 必失败；② <c>Apply</c> 缺句柄必失败；③ 判据 id / 理由码非空。
    /// </summary>
    private ChangeVerdict BuildVerdict(
        SkillCurationRequest request,
        IReadOnlyList<string> reasons,
        IReadOnlyList<string> availabilityNotes)
    {
        var degradeNote = availabilityNotes.Count == 0
            ? string.Empty
            : $"（降级记录：{string.Join(",", availabilityNotes)}）";

        if (reasons.Count > 0)
        {
            return ChangeVerdict.Create(
                ChangeDecision.Reject,
                _policy.PolicyId,
                _policy.Version,
                string.Join("|", reasons),
                note: $"门禁拒绝 {request.Product.Name}{degradeNote}");
        }

        if (request.RequestedDecision == ChangeDecision.Unknown)
        {
            try
            {
                // 故意走构造器：Unknown 不得当放行 —— 此处必须抛出。
                _ = ChangeVerdict.Create(
                    ChangeDecision.Unknown, _policy.PolicyId, _policy.Version, ReasonInvalidRequestedDecision);
            }
            catch (ArgumentException ex)
            {
                return ChangeVerdict.Create(
                    ChangeDecision.Reject,
                    _policy.PolicyId,
                    _policy.Version,
                    ReasonInvalidRequestedDecision,
                    note: ex.Message + degradeNote);
            }

            return ChangeVerdict.Create(
                ChangeDecision.Reject,
                _policy.PolicyId,
                _policy.Version,
                ReasonInvalidRequestedDecision,
                note: "裁决器未按预期拒绝 Unknown 决策（理论不可达）。" + degradeNote);
        }

        if (request.RequestedDecision == ChangeDecision.Apply)
        {
            if (string.IsNullOrWhiteSpace(request.RollbackHandle))
            {
                try
                {
                    // 故意走构造器：Apply 缺句柄必须失败 —— 这就是 C5 的牙。
                    _ = ChangeVerdict.Create(
                        ChangeDecision.Apply, _policy.PolicyId, _policy.Version, ReasonRollbackUnavailable);
                }
                catch (ArgumentException ex)
                {
                    return ChangeVerdict.Create(
                        ChangeDecision.Reject,
                        _policy.PolicyId,
                        _policy.Version,
                        ReasonRollbackUnavailable,
                        note: ex.Message + degradeNote);
                }

                return ChangeVerdict.Create(
                    ChangeDecision.Reject,
                    _policy.PolicyId,
                    _policy.Version,
                    ReasonRollbackUnavailable,
                    note: "Apply 缺句柄未被构造器拒绝（理论不可达）。" + degradeNote);
            }

            // 句柄齐备：本片仍**不得** Apply（写盘职权在 L3-b）⇒ 降级为 shadow 并留痕。
            return ChangeVerdict.Create(
                ChangeDecision.ApplyShadow,
                _policy.PolicyId,
                _policy.Version,
                ReasonShadowOnly,
                note: $"请求 Apply 但本片无写盘职权 ⇒ shadow；句柄 {request.RollbackHandle}{degradeNote}");
        }

        return ChangeVerdict.Create(
            ChangeDecision.ApplyShadow,
            _policy.PolicyId,
            _policy.Version,
            ReasonNoBlockingFindings,
            note: $"五条门禁均通过{degradeNote}");
    }

    private static HashSet<string> CollectReplacedIds(
        DistilledSkillProduct product,
        IReadOnlyList<SkillCurationSkillSnapshot> replacedSnapshots)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in SafeList(product.ReplacedSkillIds))
        {
            var trimmed = id.Trim();
            if (trimmed.Length > 0)
            {
                ids.Add(trimmed);
            }
        }

        foreach (var snapshot in replacedSnapshots)
        {
            var trimmed = (snapshot.SkillId ?? string.Empty).Trim();
            if (trimmed.Length > 0)
            {
                ids.Add(trimmed);
            }
        }

        return ids;
    }

    private static List<string> SafeList(IReadOnlyList<string>? values)
        => values is null ? [] : values.ToList();
}

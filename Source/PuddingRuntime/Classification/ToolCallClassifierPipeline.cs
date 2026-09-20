using PuddingCode.Classification;
using PuddingCode.Tools;

namespace PuddingRuntime.Classification;

/// <summary>
/// 分类器管线可调参数（方案 v2 §14.13.6 / §14.13.3）。
/// </summary>
public sealed class ToolCallClassifierPipelineOptions
{
    /// <summary>仲裁调用独立超时默认值：3000 ms（§14.13.6，实测 Jev ≈1.1 s，余量 ≈2.7×）。</summary>
    public const int DefaultArbiterTimeoutMs = 3000;

    /// <summary>永久类结论逐分类可信度门槛默认值：0.90（§14.13.3）。</summary>
    public const double DefaultPermanentConfidenceThreshold = 0.90;

    /// <summary>仲裁分类器调用的独立超时（毫秒）；超时计入「仲裁不可用」分支。</summary>
    public int ArbiterTimeoutMs { get; init; } = DefaultArbiterTimeoutMs;

    /// <summary>
    /// 永久类结论（AllowPermanent / DenyPermanent）的逐分类可信度门槛；
    /// 低于门槛（含缺失）降级为对应单次类（§14.13.3）。
    /// </summary>
    public double PermanentConfidenceThreshold { get; init; } = DefaultPermanentConfidenceThreshold;
}

/// <summary>
/// 分类器管线（方案 v2 §14.13，切片 S3a）：把「候选 → 覆盖」落成确定性求值序。
/// <para>
/// 管线本身实现 <see cref="IToolCallClassifier"/>：所有消费方只依赖抽象，
/// 不知道任何具体仲裁实现、也不知道「规则」的存在（§14.13.1 权威在抽象层）。
/// </para>
/// <para>
/// 求值序（§14.13.2，不可交换）：
/// ① 规则类分类器命中 allow ⇒ 立即终局放行（零仲裁调用）；
/// ② 规则类命中 deny ⇒ 候选，必须给仲裁一次覆盖机会（§11.3）；
/// ③ 规则均未命中 ⇒ 仲裁；
/// ④a ③ 场景仲裁不可用 ⇒ <see cref="ClassificationOutcome.Unknown"/> +
///     <c>classifier.pipeline.arbiter_unavailable</c>（上层按 §14.7 转 deferred）；
/// ④b ② 场景仲裁不可用 ⇒ Unknown + <c>classifier.pipeline.override_unavailable</c>
///     （绝不折叠为 Deny、绝不放行，ADR-091 §4.4）；
/// ⑤ 仲裁正常返回 ⇒ 采纳；永久类逐分类可信度低于门槛 ⇒ 降级为对应单次类；
/// ⑥ 仲裁返回 Unknown ⇒ 同 ④（按场景取码）。
/// </para>
/// <para>
/// 防循环（§11.3 / §14.13.5）：候选 deny 的来源规则若由分类器自身产出（Source=Classifier），
/// 该结论就是分类器此前的裁决，管线直接复用、不再回调仲裁。规则来源经审计存储回查
/// （构造契约只注入 <see cref="IToolApprovalAuditStore"/>；策展器落规则时必写携带
/// <c>AllowlistRuleId + Source</c> 的审计事件）；无法判定时按普通候选处理（宁可多问一次仲裁，
/// 也不错杀覆盖权）。同一次求值对仲裁至多调用一次。
/// </para>
/// <para>
/// 异常契约（§14.13.6）：仲裁调用捕获全部异常转 ④；外层 <see cref="CancellationToken"/>
/// 触发的 <see cref="OperationCanceledException"/> 照常传播。每次返回必带非空
/// Reason / ReasonCode / ClassifierId；耗时经注入 <see cref="TimeProvider"/> 计量；无静态可变状态。
/// </para>
/// </summary>
public sealed class ToolCallClassifierPipeline : IToolCallClassifier
{
    /// <summary>管线稳定标识（审计溯源与健康面聚合键）。</summary>
    public const string WellKnownClassifierId = "pipeline";

    /// <summary>① 规则快路径终局放行（零仲裁调用）。</summary>
    public const string ReasonAllowFastPath = "classifier.pipeline.allow_fast_path";

    /// <summary>④a/⑥ ③ 场景仲裁不可用（超时/异常/无答案/返回 Unknown）。</summary>
    public const string ReasonArbiterUnavailable = "classifier.pipeline.arbiter_unavailable";

    /// <summary>④b/⑥ ② 场景仲裁不可用：候选 deny 未获覆盖机会，返回 Unknown（不折叠、不放行）。</summary>
    public const string ReasonOverrideUnavailable = "classifier.pipeline.override_unavailable";

    /// <summary>⑤ 永久类结论因逐分类可信度低于门槛降级为单次类。</summary>
    public const string ReasonPermanentDowngraded = "classifier.pipeline.permanent_downgraded";

    /// <summary>⑤ 仲裁结论按门槛校验通过、被采纳为终局结论。</summary>
    public const string ReasonArbiterFinal = "classifier.pipeline.arbiter_final";

    /// <summary>防循环：候选 deny 规则由分类器自身产出，复用分类器裁决，不回调仲裁（§11.3）。</summary>
    public const string ReasonClassifierRuleDenyFinal = "classifier.pipeline.classifier_rule_deny_final";

    /// <summary>逐分类可信度的规范键（对齐 <see cref="ClassificationVerdict.PerOutcomeConfidence"/> 契约）。</summary>
    private const string ConfidenceKeyAllowPermanent = "allow_permanent";

    private const string ConfidenceKeyDenyPermanent = "deny_permanent";

    private const string ConfidenceKeyAllowOnce = "allow_once";

    private const string ConfidenceKeyDenyOnce = "deny_once";

    private readonly IReadOnlyList<IToolCallClassifier> _ruleClassifiers;
    private readonly IToolCallClassifier _arbiter;
    private readonly IToolApprovalAuditStore _auditStore;
    private readonly TimeProvider _timeProvider;
    private readonly ToolCallClassifierPipelineOptions _options;

    /// <summary>构造分类器管线。</summary>
    /// <param name="ruleClassifiers">确定性规则类分类器（零网络），按给定顺序求值；允许为空列表。</param>
    /// <param name="arbiter">仲裁分类器（如模型实现）；单次求值至多调用一次。</param>
    /// <param name="auditStore">审计存储：用于覆盖审计（§14.13.4）与防循环的规则来源回查（§11.3）。</param>
    /// <param name="timeProvider">时间源；缺省系统时钟（测试注入假钟）。</param>
    /// <param name="options">管线可调参数；缺省超时 3000 ms、永久类门槛 0.90。</param>
    public ToolCallClassifierPipeline(
        IReadOnlyList<IToolCallClassifier> ruleClassifiers,
        IToolCallClassifier arbiter,
        IToolApprovalAuditStore auditStore,
        TimeProvider? timeProvider = null,
        ToolCallClassifierPipelineOptions? options = null)
    {
        _ruleClassifiers = ruleClassifiers ?? throw new ArgumentNullException(nameof(ruleClassifiers));
        _arbiter = arbiter ?? throw new ArgumentNullException(nameof(arbiter));
        _auditStore = auditStore ?? throw new ArgumentNullException(nameof(auditStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new ToolCallClassifierPipelineOptions();
    }

    /// <inheritdoc />
    public string ClassifierId => WellKnownClassifierId;

    /// <inheritdoc />
    public async Task<ClassificationVerdict> ClassifyAsync(
        ToolCallClassificationContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();
        var startTimestamp = _timeProvider.GetTimestamp();
        var verdict = await ClassifyCoreAsync(context, ct).ConfigureAwait(false);
        return verdict with { LatencyMs = _timeProvider.GetElapsedTime(startTimestamp).TotalMilliseconds };
    }

    private async Task<ClassificationVerdict> ClassifyCoreAsync(
        ToolCallClassificationContext context,
        CancellationToken ct)
    {
        // —— ① 规则类快路径（§14.13.2①）：任一命中 allow ⇒ 立即终局放行，零仲裁调用。——
        List<ClassificationVerdict>? denyCandidates = null;
        foreach (var rule in _ruleClassifiers)
        {
            ClassificationVerdict? verdict;
            try
            {
                verdict = await rule.ClassifyAsync(context, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 取消是调用方意图，照常传播。
                throw;
            }
            catch (Exception)
            {
                // 规则类分类器按契约 fail-safe 不冒泡；实现违约时视该环 Unknown，继续后续环。
                verdict = null;
            }

            if (verdict is null)
            {
                continue;
            }

            if (verdict.Outcome is ClassificationOutcome.AllowOnce or ClassificationOutcome.AllowPermanent)
            {
                return new ClassificationVerdict
                {
                    Outcome = verdict.Outcome,
                    Reason = $"管线快路径：规则分类器 {verdict.ClassifierId} 命中放行 ⇒ 终局放行（零仲裁调用）。{verdict.Reason}",
                    ReasonCode = ReasonAllowFastPath,
                    ClassifierId = WellKnownClassifierId,
                    ClassifierModel = verdict.ClassifierModel,
                    PerOutcomeConfidence = verdict.PerOutcomeConfidence,
                    AppliedRuleId = verdict.AppliedRuleId,
                };
            }

            if (verdict.Outcome is ClassificationOutcome.DenyOnce or ClassificationOutcome.DenyPermanent)
            {
                // —— ② deny ⇒ 候选（保留 Reason / AppliedRuleId 供审计），不终局，必须给仲裁一次覆盖机会。——
                denyCandidates ??= [];
                denyCandidates.Add(verdict);
            }

            // Unknown ⇒ 继续下一环（③ 前置）。
        }

        var candidate = denyCandidates is { Count: > 0 } candidates ? candidates[0] : null;

        // —— 防循环（§11.3 / §14.13.5）：候选 deny 规则由分类器自身产出 ⇒ 复用分类器裁决，不回调仲裁。——
        ToolApprovalAllowlistRuleSource? candidateRuleSource = null;
        if (candidate is not null)
        {
            candidateRuleSource = await ResolveRuleSourceAsync(candidate.AppliedRuleId, ct).ConfigureAwait(false);
            if (candidateRuleSource == ToolApprovalAllowlistRuleSource.Classifier)
            {
                return new ClassificationVerdict
                {
                    Outcome = candidate.Outcome,
                    Reason = $"防循环：候选 deny 规则 {candidate.AppliedRuleId} 由分类器自身产出（Source=Classifier），"
                             + $"复用分类器既有裁决，不再回调仲裁（§11.3/§14.13.5）。原候选理由：{candidate.Reason}",
                    ReasonCode = ReasonClassifierRuleDenyFinal,
                    ClassifierId = WellKnownClassifierId,
                    ClassifierModel = null,
                    AppliedRuleId = candidate.AppliedRuleId,
                };
            }
        }

        // —— ②/③ 仲裁：单次求值至多调用一次；独立 CTS + 超时（§14.13.6）。——
        var (arbiterVerdict, failureDetail) = await InvokeArbiterAsync(context, ct).ConfigureAwait(false);

        // —— ④/⑥ 仲裁不可用或返回 Unknown：按场景取码，绝不折叠为 Deny、绝不放行（ADR-091 §4.4）。——
        if (arbiterVerdict is null || arbiterVerdict.Outcome == ClassificationOutcome.Unknown)
        {
            return candidate is not null
                ? Unknown(
                    $"候选 deny（规则 {candidate.AppliedRuleId ?? "无"}）遇仲裁不可用（{failureDetail}），"
                    + "按 §14.13.2④b 返回 Unknown：绝不折叠为 Deny、绝不放行，由上层转 deferred。原候选理由："
                    + candidate.Reason,
                    ReasonOverrideUnavailable,
                    candidate.AppliedRuleId)
                : Unknown(
                    $"规则均未命中且仲裁不可用（{failureDetail}），按 §14.13.2④a 返回 Unknown，由上层转 deferred。",
                    ReasonArbiterUnavailable);
        }

        // —— ⑤ 采纳仲裁结论 + 永久类置信度门槛（§14.13.3）。——
        var final = ApplyPermanentConfidenceGate(arbiterVerdict, candidate, out var gatedConfidence, out var downgraded);

        // —— 覆盖审计（§14.13.4，必落不可省）：仅「候选 deny → 仲裁给出终局结论」的覆盖裁决落审计。——
        if (candidate is not null)
        {
            await WriteOverrideAuditAsync(
                context, candidate, final, arbiterVerdict, gatedConfidence, downgraded, candidateRuleSource, ct)
                .ConfigureAwait(false);
        }

        return final;
    }

    /// <summary>
    /// 调用仲裁分类器一次：独立链接 CTS 施加 <see cref="ToolCallClassifierPipelineOptions.ArbiterTimeoutMs"/> 超时；
    /// 捕获全部异常转「仲裁不可用」（§14.13.6）；外层 ct 触发的取消照常传播。
    /// </summary>
    private async Task<(ClassificationVerdict? Verdict, string FailureDetail)> InvokeArbiterAsync(
        ToolCallClassificationContext context,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(_options.ArbiterTimeoutMs));
        try
        {
            var verdict = await _arbiter.ClassifyAsync(context, timeoutCts.Token).ConfigureAwait(false);
            return verdict is null
                ? (null, "arbiter_returned_null")
                : (verdict, string.Empty);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 外层取消令牌触发：调用方意图，照常传播（不得折叠成 Unknown）。
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // 非外层取消 ⇒ 独立超时触发（§14.13.6）。
            return (null, $"timeout_after_{_options.ArbiterTimeoutMs}ms({ex.GetType().Name})");
        }
        catch (Exception ex)
        {
            // 其余任意异常（含实现违约）：一律按「仲裁不可用」处理，绝不冒泡（§14.13.6）。
            return (null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 永久类置信度门槛（§14.13.3）：AllowPermanent / DenyPermanent 需对应键逐分类可信度 ≥ 门槛，
    /// 缺失或低于门槛 ⇒ 降级为对应单次类并保留降级事实。
    /// </summary>
    private ClassificationVerdict ApplyPermanentConfidenceGate(
        ClassificationVerdict arbiterVerdict,
        ClassificationVerdict? candidate,
        out double? gatedConfidence,
        out bool downgraded)
    {
        downgraded = false;
        var outcome = arbiterVerdict.Outcome;

        if (outcome is not (ClassificationOutcome.AllowPermanent or ClassificationOutcome.DenyPermanent))
        {
            gatedConfidence = ConfidenceFor(arbiterVerdict, outcome);
            return Adopt(arbiterVerdict, candidate, arbiterVerdict.Reason);
        }

        var permanentKey = outcome == ClassificationOutcome.AllowPermanent
            ? ConfidenceKeyAllowPermanent
            : ConfidenceKeyDenyPermanent;
        double? confidence = null;
        if (arbiterVerdict.PerOutcomeConfidence is { } map && map.TryGetValue(permanentKey, out var confidenceValue))
        {
            confidence = confidenceValue;
        }
        gatedConfidence = confidence;

        if (confidence.HasValue && confidence.Value >= _options.PermanentConfidenceThreshold)
        {
            return Adopt(arbiterVerdict, candidate, arbiterVerdict.Reason);
        }

        downgraded = true;
        var onceOutcome = outcome == ClassificationOutcome.AllowPermanent
            ? ClassificationOutcome.AllowOnce
            : ClassificationOutcome.DenyOnce;
        var confidenceText = confidence.HasValue
            ? confidence.Value.ToString("0.###")
            : "missing";
        return new ClassificationVerdict
        {
            Outcome = onceOutcome,
            Reason = $"永久类结论的逐分类可信度（{permanentKey}={confidenceText}）低于门槛 "
                     + $"{_options.PermanentConfidenceThreshold.ToString("0.###")}，按 §14.13.3 降级为单次类。"
                     + $"原仲裁理由：{arbiterVerdict.Reason}",
            ReasonCode = ReasonPermanentDowngraded,
            ClassifierId = WellKnownClassifierId,
            ClassifierModel = arbiterVerdict.ClassifierModel,
            PerOutcomeConfidence = arbiterVerdict.PerOutcomeConfidence,
            AppliedRuleId = candidate?.AppliedRuleId ?? arbiterVerdict.AppliedRuleId,
        };
    }

    /// <summary>采纳仲裁结论为管线终局结论（保留仲裁溯源与候选规则 id）。</summary>
    private ClassificationVerdict Adopt(ClassificationVerdict arbiterVerdict, ClassificationVerdict? candidate, string arbiterReason)
    {
        var candidateNote = candidate is null
            ? "规则均未命中，直接采纳。"
            : $"候选 deny（规则 {candidate.AppliedRuleId ?? "无"}）经仲裁覆盖。";
        return new ClassificationVerdict
        {
            Outcome = arbiterVerdict.Outcome,
            Reason = $"管线采纳仲裁结论（classifier={arbiterVerdict.ClassifierId}）。{candidateNote}{arbiterReason}",
            ReasonCode = ReasonArbiterFinal,
            ClassifierId = WellKnownClassifierId,
            ClassifierModel = arbiterVerdict.ClassifierModel,
            PerOutcomeConfidence = arbiterVerdict.PerOutcomeConfidence,
            AppliedRuleId = candidate?.AppliedRuleId ?? arbiterVerdict.AppliedRuleId,
        };
    }

    /// <summary>读取指定结论对应的逐分类可信度；缺失为 null。</summary>
    private static double? ConfidenceFor(ClassificationVerdict verdict, ClassificationOutcome outcome)
    {
        var key = outcome switch
        {
            ClassificationOutcome.AllowOnce => ConfidenceKeyAllowOnce,
            ClassificationOutcome.AllowPermanent => ConfidenceKeyAllowPermanent,
            ClassificationOutcome.DenyOnce => ConfidenceKeyDenyOnce,
            ClassificationOutcome.DenyPermanent => ConfidenceKeyDenyPermanent,
            _ => null,
        };
        if (key is null || verdict.PerOutcomeConfidence is not { } map || !map.TryGetValue(key, out var value))
        {
            return null;
        }

        return value;
    }

    /// <summary>
    /// 覆盖审计（§14.13.4）：至少含原候选结论、覆盖后结论、分类器 id/型号、逐分类可信度、
    /// 理由、时间、身份四元组、工具 id 与参数；Reason 以 key=value 形式承载结构化字段并保留原始候选理由。
    /// 审计失败不改变已定裁决（裁决先于留痕成立）。
    /// </summary>
    private async Task WriteOverrideAuditAsync(
        ToolCallClassificationContext context,
        ClassificationVerdict candidate,
        ClassificationVerdict final,
        ClassificationVerdict arbiterVerdict,
        double? gatedConfidence,
        bool downgraded,
        ToolApprovalAllowlistRuleSource? candidateRuleSource,
        CancellationToken ct)
    {
        try
        {
            await _auditStore.SaveAsync(new ToolApprovalAuditEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                EventType = ToolApprovalAuditEventType.ClassifierInvoked,
                WorkspaceId = context.WorkspaceId,
                SessionId = context.SessionId,
                AgentInstanceId = context.AgentInstanceId,
                UserId = context.UserId,
                ToolId = context.ToolId,
                Command = context.CommandName,
                ArgumentsJson = context.ArgumentsJson,
                AllowlistRuleId = candidate.AppliedRuleId,
                Effect = final.Outcome is ClassificationOutcome.AllowOnce or ClassificationOutcome.AllowPermanent
                    ? ToolApprovalRuleEffect.Allow
                    : ToolApprovalRuleEffect.Deny,
                Source = candidateRuleSource,
                ReviewerModel = arbiterVerdict.ClassifierModel,
                ClassifierId = arbiterVerdict.ClassifierId,
                ClassifierConfidence = gatedConfidence,
                Reason = BuildOverrideReason(candidate, final, arbiterVerdict, gatedConfidence, downgraded),
                CreatedAtUtc = _timeProvider.GetUtcNow(),
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 审计写入失败不得改变已定裁决；溯源缺失风险在健康面/运维侧另行暴露（本切片不引入日志依赖）。
        }
    }

    /// <summary>覆盖审计 Reason：结构化 key=value 前缀（candidate/final/classifier/model/confidence/threshold/…）+ 原始理由。</summary>
    private string BuildOverrideReason(
        ClassificationVerdict candidate,
        ClassificationVerdict final,
        ClassificationVerdict arbiterVerdict,
        double? gatedConfidence,
        bool downgraded)
    {
        var confidenceText = gatedConfidence.HasValue ? gatedConfidence.Value.ToString("0.###") : "missing";
        return $"candidate={candidate.Outcome}; final={final.Outcome}; classifier={arbiterVerdict.ClassifierId}; "
               + $"model={arbiterVerdict.ClassifierModel ?? "na"}; confidence={confidenceText}; "
               + $"threshold={_options.PermanentConfidenceThreshold.ToString("0.###")}; "
               + $"downgraded={(downgraded ? "true" : "false")}; "
               + $"candidate_rule={candidate.AppliedRuleId ?? "na"}; "
               + $"arbiter_reason={arbiterVerdict.Reason}; candidate_reason={candidate.Reason}";
    }

    /// <summary>
    /// 解析规则 id 的来源（§11.3 / §14.13.5 防循环判定）。
    /// 管线构造契约只注入审计存储（不注入 allowlist store），故经审计事件回查：
    /// 策展器落规则时必写携带 <c>AllowlistRuleId + Source</c> 的事件；
    /// 任一事件显示 Source=Classifier ⇒ 防循环；审计不可读 ⇒ null ⇒ 按普通候选处理（任务书明示）。
    /// </summary>
    private async Task<ToolApprovalAllowlistRuleSource?> ResolveRuleSourceAsync(string? appliedRuleId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(appliedRuleId))
        {
            return null;
        }

        try
        {
            var events = await _auditStore.ListAsync(ct).ConfigureAwait(false);
            ToolApprovalAllowlistRuleSource? resolved = null;
            foreach (var auditEvent in events)
            {
                if (!string.Equals(auditEvent.AllowlistRuleId, appliedRuleId, StringComparison.Ordinal)
                    || auditEvent.Source is null)
                {
                    continue;
                }

                if (auditEvent.Source == ToolApprovalAllowlistRuleSource.Classifier)
                {
                    return auditEvent.Source;
                }

                resolved = auditEvent.Source;
            }

            return resolved;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 无法判定 ⇒ 按普通候选处理：宁可多问一次仲裁，也不错杀覆盖权（§11.3）。
            return null;
        }
    }

    /// <summary>构造 Unknown 结论（降级契约：Reason/ReasonCode/ClassifierId 非空，绝不冒泡异常）。</summary>
    private static ClassificationVerdict Unknown(string reason, string reasonCode, string? appliedRuleId = null) => new()
    {
        Outcome = ClassificationOutcome.Unknown,
        Reason = reason,
        ReasonCode = reasonCode,
        ClassifierId = WellKnownClassifierId,
        ClassifierModel = null,
        AppliedRuleId = appliedRuleId,
    };
}

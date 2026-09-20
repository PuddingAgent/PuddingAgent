using PuddingCode.Classification;
using PuddingCode.Tools;

namespace PuddingRuntime.Classification;

/// <summary>
/// 零网络的系统规则分类器（方案 v2 §2.2 / §14.12.2，切片 S2b）：
/// 把既有黑白名单规则翻译成 <see cref="IToolCallClassifier"/> 裁决结论，
/// 是分类器管线的第一环（确定性规则快路径）。
/// <para>
/// 行为契约：
/// - 规则键完全复用 <see cref="ClassificationRuleCurator.NormalizeSubject"/> /
///   <see cref="ClassificationRuleCurator.BuildKey"/>（§14.12.1，命中为逐字节相等，禁止通配语义）；
/// - <see cref="ToolApprovalAllowlistRuleStatus.Disabled"/> 的规则不参与匹配（§14.12.7 已退役）；
/// - 命中 allow（任意来源）⇒ <see cref="ClassificationOutcome.AllowOnce"/>；
/// - 命中 deny ⇒ <see cref="ClassificationOutcome.DenyOnce"/> <b>候选</b>（§11.3：由上层管线决定
///   是否给后续分类器一次覆盖机会；本实现绝不执行覆盖，也绝不返回
///   <see cref="ClassificationOutcome.DenyPermanent"/> / <see cref="ClassificationOutcome.AllowPermanent"/>）；
/// - 同键同时命中 allow 与 deny ⇒ §14.12.4 默认策略 <c>deny_wins</c>；
/// - 未命中 ⇒ <see cref="ClassificationOutcome.Unknown"/>（绝不是放行/拒绝）；
/// - store 异常 ⇒ fail-closed 返回 Unknown，绝不冒泡；
///   <see cref="OperationCanceledException"/> 是调用方意图，照常传播；
/// - 零网络：唯一外部依赖 = <see cref="IToolApprovalAllowlistStore"/>；无静态可变状态；
///   时间经注入 <see cref="TimeProvider"/> 获取（§14.12.9 可测性）。
/// </para>
/// </summary>
public sealed class SystemRuleClassifier : IToolCallClassifier
{
    /// <summary>分类器稳定标识（审计溯源与健康面聚合键）。</summary>
    public const string WellKnownClassifierId = "system-rules";

    /// <summary>命中 allow 规则（快路径放行候选）。</summary>
    public const string ReasonAllowHit = "classifier.rule.allow_hit";

    /// <summary>命中 deny 规则（候选 deny，可被后续分类器覆盖，§11.3）。</summary>
    public const string ReasonDenyCandidate = "classifier.rule.deny_candidate";

    /// <summary>同键 allow/deny 冲突，按 §14.12.4 默认策略 deny_wins。</summary>
    public const string ReasonConflictDenyWins = "classifier.rule.conflict_deny_wins";

    /// <summary>未命中任何启用的系统规则。</summary>
    public const string ReasonNoMatch = "classifier.rule.no_match";

    /// <summary>规则存储读取失败（fail-closed，不冒泡）。</summary>
    public const string ReasonStoreError = "classifier.rule.store_error";

    private readonly IToolApprovalAllowlistStore _allowlistStore;
    private readonly TimeProvider _timeProvider;

    public SystemRuleClassifier(IToolApprovalAllowlistStore allowlistStore, TimeProvider? timeProvider = null)
    {
        _allowlistStore = allowlistStore ?? throw new ArgumentNullException(nameof(allowlistStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string ClassifierId => WellKnownClassifierId;

    /// <inheritdoc />
    public async Task<ClassificationVerdict> ClassifyAsync(
        ToolCallClassificationContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var startTimestamp = _timeProvider.GetTimestamp();

        ClassificationVerdict verdict;
        try
        {
            // 取消检查放在 store IO 之前；取消是调用方意图，不属于「异常吞掉」契约（对齐 ClassificationRuleCurator）。
            ct.ThrowIfCancellationRequested();
            verdict = await ClassifyCoreAsync(context, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 行为契约：OperationCanceledException 必须照常传播（rethrow），不得折叠成 Unknown。
            throw;
        }
        catch (Exception ex)
        {
            // fail-closed：任何非取消异常（含 store IO 故障）不得冒泡，按降级契约返回 Unknown，绝不伪造结论。
            verdict = Unknown(
                $"规则存储读取失败，fail-closed 返回未知（不冒泡）：{ex.GetType().Name}: {ex.Message}",
                ReasonStoreError);
        }

        return verdict with { LatencyMs = _timeProvider.GetElapsedTime(startTimestamp).TotalMilliseconds };
    }

    private async Task<ClassificationVerdict> ClassifyCoreAsync(
        ToolCallClassificationContext context,
        CancellationToken ct)
    {
        var rules = await _allowlistStore.ListAsync(ct).ConfigureAwait(false);

        // §14.12.1：请求侧键完全复用 S2 策展器构造（subject / 工作目录 / shell 全部规范化），不重复实现。
        var key = ClassificationRuleCurator.BuildKey(context);

        // 多条同效果命中时取 store 返回顺序的首条（确定性，不做二次优选）。
        ToolApprovalAllowlistRule? allowHit = null;
        ToolApprovalAllowlistRule? denyHit = null;
        foreach (var rule in rules)
        {
            // §14.12.7：Disabled 视为已退役，不参与匹配（不复活）。
            if (rule.Status != ToolApprovalAllowlistRuleStatus.Enabled || !MatchesKey(rule, key))
            {
                continue;
            }

            if (rule.Effect == ToolApprovalRuleEffect.Deny)
            {
                denyHit ??= rule;
            }
            else
            {
                allowHit ??= rule;
            }
        }

        // §14.12.4：同键同时存在 allow 与 deny ⇒ 生效 deny（默认策略 deny_wins，不建议改动）。
        if (denyHit is not null)
        {
            var isConflict = allowHit is not null;
            return new ClassificationVerdict
            {
                Outcome = ClassificationOutcome.DenyOnce,
                Reason = isConflict
                    ? $"同键同时命中 allow 规则 {allowHit!.RuleId}（来源 {allowHit.Source}）与 deny 规则 {denyHit.RuleId}"
                      + $"（来源 {denyHit.Source}），按 §14.12.4 默认策略 deny_wins 生效 deny 候选。"
                    : $"命中 deny 规则 {denyHit.RuleId}（来源 {denyHit.Source}）；按 §14.12.2 该结论为候选，"
                      + "可由上层管线决定是否给后续分类器一次覆盖机会（§11.3）。",
                ReasonCode = isConflict ? ReasonConflictDenyWins : ReasonDenyCandidate,
                ClassifierId = ClassifierId,
                ClassifierModel = null,
                AppliedRuleId = denyHit.RuleId,
            };
        }

        if (allowHit is not null)
        {
            return new ClassificationVerdict
            {
                Outcome = ClassificationOutcome.AllowOnce,
                Reason = $"命中 allow 规则 {allowHit.RuleId}（来源 {allowHit.Source}）；"
                         + "按 §14.12.2 快路径放行（AllowOnce，不沉淀新规则）。",
                ReasonCode = ReasonAllowHit,
                ClassifierId = ClassifierId,
                ClassifierModel = null,
                AppliedRuleId = allowHit.RuleId,
            };
        }

        // 未命中 ⇒ 必须是 Unknown（绝不是 AllowOnce/DenyOnce），调用方按判定优先级继续后续分类器（§1③）。
        return Unknown("未命中任何启用的系统规则（§14.12.1 规则键逐字节相等匹配）。", ReasonNoMatch);
    }

    /// <summary>
    /// 规则行与请求键是否逐字节相等（§14.12.1，无任何通配 / 前缀 / 正则语义）。
    /// 复用 <see cref="ClassificationRuleCurator.BuildKey"/> 对规则行做与请求侧完全相同的规范化，
    /// 再按 <see cref="RuleKey"/> record 值相等比较（五元组逐字段 Ordinal），
    /// 与策展器内部键匹配语义一致，不重复实现规则键逻辑。
    /// </summary>
    private static bool MatchesKey(ToolApprovalAllowlistRule rule, RuleKey key)
    {
        var ruleKey = ClassificationRuleCurator.BuildKey(new ToolCallClassificationContext
        {
            WorkspaceId = rule.WorkspaceId ?? string.Empty,
            ToolId = rule.ToolId,
            CommandName = rule.Command,
            ArgumentsJson = rule.ArgumentsJson,
            WorkingDirectory = rule.WorkingDirectory,
            Shell = rule.Shell,
            SessionId = string.Empty,
            AgentInstanceId = string.Empty,
            UserId = string.Empty,
        });

        return ruleKey == key;
    }

    private static ClassificationVerdict Unknown(string reason, string reasonCode) => new()
    {
        Outcome = ClassificationOutcome.Unknown,
        Reason = reason,
        ReasonCode = reasonCode,
        ClassifierId = WellKnownClassifierId,
        ClassifierModel = null,
        AppliedRuleId = null,
    };
}

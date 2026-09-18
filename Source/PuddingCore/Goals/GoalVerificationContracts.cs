namespace PuddingCode.Goals;

public enum GoalVerificationVerdict
{
    Continue = 0,
    Complete = 1,
    Blocked = 2,
    NeedsUser = 3,
    Unsafe = 4,
}

/// <summary>Verifier 的有界、结构化、只读输入；不携带无限 transcript。</summary>
public sealed record GoalEvidenceCapsule
{
    public required string GoalRunId { get; init; }
    public required int ActivationEpoch { get; init; }
    public required int AggregateVersion { get; init; }
    public required int IterationNo { get; init; }
    public required string Objective { get; init; }
    public required int ObjectiveVersion { get; init; }
    public required int RemainingIterations { get; init; }
    public required string TurnId { get; init; }
    public required string TerminalKind { get; init; }
    public required long TerminalSequence { get; init; }
    public required IReadOnlyList<string> EvidenceRefs { get; init; }
    public string? TaskId { get; init; }
    public string? TaskStatus { get; init; }
    public string? TaskAcceptanceCriteria { get; init; }
    public bool HasPendingExecutionFacts { get; init; }
    public bool EvidenceComplete { get; init; }

    /// <summary>ADR-092 §4/§5（G92-1）：本次裁决依据的验收条件快照（空集合表示尚无验收合同）。</summary>
    public IReadOnlyList<GoalCriterion> Criteria { get; init; } = [];

    /// <summary>ADR-092 §5.3（G92-1）：受控检查的实际报告；verifier 只读它，不执行工具。</summary>
    public IReadOnlyList<GoalCheckReport> CheckReports { get; init; } = [];

    /// <summary>ADR-092 §5.3（G92-1）：本次裁决依据的版本化检查定义；未声明的报告不得计入通过。</summary>
    public IReadOnlyList<GoalCheckSpec> Checks { get; init; } = [];

    /// <summary>
    /// G92-1 S1-c（片5）：text-assertion 的证据锚 —— 本 canonical Turn 终态最终 assistant 输出的 TurnId。
    /// null 表示该上下文不可得（终态 turn.completed 事件缺失 / payload 不可解析），
    /// 此时任何 assistant-output 证据都不得放行（policy 对该 kind fail-closed）。
    /// 注意：不可直接复用 <see cref="TurnId"/> —— 后者恒非空，无法表达「reply 不可得」。
    /// </summary>
    public string? AssistantOutputTurnId { get; init; }

    /// <summary>
    /// G92-1 S1-a：本次裁决依据的验收合同来源（goal_acceptance_contracts.source）。
    /// 取值见 <see cref="GoalAcceptanceContractSources"/>；null 表示合同行缺失或来源未知
    /// （此时 Criteria/Checks 亦为空，裁决会先走 acceptance_contract_missing / check_contract_missing）。
    /// </summary>
    public string? AcceptanceContractSource { get; init; }

    /// <summary>
    /// G92-1 S1-c 片6-3b：本次裁决依据的验收合同版本（goal_acceptance_contracts.contract_version）。
    /// null 表示合同行缺失或版本未知；A1 合同整理成功后随 Criteria/Checks/Source 一起完整重载
    /// （规格 WHY §5：Worker 必须完整重载四项，不能只回填前两项）。
    /// </summary>
    public int? AcceptanceContractVersion { get; init; }

    /// <summary>
    /// G92-1 S1-a：合同是否为纯工程门禁（objective 未声明任何目标级证据）。
    /// 仅当来源精确等于 bounded_planning 时为真；未知来源（null/其他受控值）不视为纯门禁、
    /// 由既有判定分支裁决——覆盖门只拦「明确声明自己只覆盖工程门禁」的合同。
    /// </summary>
    public bool IsEngineeringGatesOnly => string.Equals(
        AcceptanceContractSource,
        GoalAcceptanceContractSources.BoundedPlanning,
        StringComparison.Ordinal);
}

/// <summary>
/// G92-1 S1-a：goal_acceptance_contracts.source 的受控词表。
/// 与 GoalAcceptanceContractPlanner 的合同级常量（Source / SourceWithObjectiveEvidence）同值；
/// 在 Core 侧独立声明以保持依赖方向（Core 不引用 Platform）。
/// </summary>
public static class GoalAcceptanceContractSources
{
    /// <summary>纯工程门禁合同：objective 未声明任何目标级证据（G92-1 S1-a 覆盖门的拦截对象）。</summary>
    public const string BoundedPlanning = "bounded_planning";

    /// <summary>携带 objective 显式声明证据的合同：具备目标级覆盖，完成路径保持不变。</summary>
    public const string BoundedPlanningWithObjectiveEvidence = "bounded_planning:objective_evidence";

    /// <summary>
    /// G92-1 S1-c 片6（A1）：Agent 经 canonical Goal Turn 的合同整理通道成功提交（CAS）后的
    /// 合同来源。一次性闸：同一 (goalRunId, activationEpoch, objectiveVersion) 已为该来源时，
    /// 再次整理只能返回 already-applied/needs_user，绝不二次覆盖。
    /// 值长度 13 &lt; 实体 MaxLength(32)（goal_acceptance_contracts.source，见
    /// GoalAcceptanceContractEntity）。
    /// </summary>
    public const string AgentRefined = "agent_refined";
}

public sealed record GoalVerificationDecision
{
    public required GoalVerificationVerdict Verdict { get; init; }
    public required string Reason { get; init; }
    public required IReadOnlyList<string> EvidenceRefs { get; init; }
    public IReadOnlyList<string> UnmetCriteria { get; init; } = [];
    public string? NextAction { get; init; }
    public string? BlockerCode { get; init; }
    public string? BlockerMessage { get; init; }
    public string? ProgressFingerprint { get; init; }

    /// <summary>
    /// ADR-092 §4（G92-0）：本单元/目标的验收条件快照。空集合表示“尚无验收合同”，
    /// 不得因此判定完成（禁止 vacuous pass）。
    /// </summary>
    public IReadOnlyList<GoalCriterion> Criteria { get; init; } = [];

    /// <summary>ADR-092 §4（G92-0）：逐项实际检查结果。空集合表示尚未有已验证验收。</summary>
    public IReadOnlyList<GoalCriterionResult> CriterionResults { get; init; } = [];
}

/// <summary>
/// 只读 Goal verifier。实现不得写 Goal/Task、执行工具或扩大权限；Coordinator
/// 独占终态提交。默认实现只接受 Task canonical terminal fact，绝不相信自然语言 DONE。
/// </summary>
public interface IGoalIterationVerifier
{
    Task<GoalVerificationDecision> VerifyAsync(
        GoalEvidenceCapsule capsule,
        CancellationToken ct = default);
}

/// <summary>ADR-092 §4：验收条件（最小合同，G92-0 引入；实际执行由 G92-1 的 CheckRunner 负责）。</summary>
public sealed record GoalCriterion
{
    public required string Id { get; init; }

    /// <summary>条件版本；条件内容变化时必须递增，旧结果随之失效。</summary>
    public int Revision { get; init; } = 1;

    public required string Requirement { get; init; }

    /// <summary>是否必需。只有必需条件全部通过才能推进/完成。</summary>
    public bool Required { get; init; } = true;

    /// <summary>检查类型，取值见 <see cref="GoalVerificationSpecKinds"/>。</summary>
    public string Kind { get; init; } = GoalVerificationSpecKinds.Semantic;

    /// <summary>版本化检查定义引用（不是自由 shell 字符串）。</summary>
    public string? DefinitionRef { get; init; }

    public string? DefinitionHash { get; init; }

    public IReadOnlyList<string> InputRefs { get; init; } = [];

    /// <summary>执行者角色：core / external_controller / human。</summary>
    public string ExecutorRole { get; init; } = "core";

    public string FreshnessPolicy { get; init; } = "input-fingerprint";

    public IReadOnlyList<string> DependencyIds { get; init; } = [];
}

/// <summary>ADR-092 §4：检查类型（显式字符串，避免与既有 wire 值冲突）。</summary>
public static class GoalVerificationSpecKinds
{
    public const string Build = "build";
    public const string Test = "test";
    public const string Postcondition = "postcondition";
    public const string Artifact = "artifact";
    public const string Semantic = "semantic";
    public const string External = "external";

    /// <summary>
    /// 目标级文件证据（ADR-092 §5.1 迭代扩展）：objective 显式声明的工作区内相对文件，
    /// 由受控检查器只读核验（存在且非空）；绝不构建命令、绝不启动进程。
    /// </summary>
    public const string FileEvidence = "file-evidence";

    /// <summary>
    /// G92-1 S1-c（片 1）：纯文本断言。判据为本 Goal 绑定 canonical Turn 的最终 assistant
    /// 输出与 spec.ExpectedText 的 ordinal 精确匹配；拒绝 contains、不 Trim、不做 Unicode 归一
    /// （决策 D1）。绝不构建命令、绝不启动进程。
    /// </summary>
    public const string TextAssertion = "text-assertion";
}

/// <summary>ADR-092 §4：逐项条件结果状态。</summary>
public static class GoalCriterionResultStatuses
{
    public const string Pending = "pending";
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Waiting = "waiting";
    public const string Invalidated = "invalidated";
}

/// <summary>
/// ADR-092 §6.2：验证作用域。步骤（work_unit）全部通过只能推进，
/// 只有整体（goal）条件全部通过才允许完成——避免“一个单元通过即整个目标完成”。
/// </summary>
public static class GoalVerificationScopes
{
    public const string WorkUnit = "work_unit";
    public const string Goal = "goal";
}

public sealed record GoalCriterionResult
{
    public required string CriterionId { get; init; }
    public int CriterionRevision { get; init; } = 1;

    /// <summary>取值见 <see cref="GoalCriterionResultStatuses"/>。</summary>
    public required string Status { get; init; }

    public IReadOnlyList<string> EvidenceRefs { get; init; } = [];

    /// <summary>实际检查输入的指纹；变了就必须重跑，不复用旧绿灯。</summary>
    public string? InputFingerprint { get; init; }

    public string? FailureCode { get; init; }
}

/// <summary>ADR-092 §4/§6.1：结算处置取值。</summary>
public static class GoalSettlementDispositions
{
    public const string ContinueCurrent = "continue_current";
    public const string Repair = "repair";
    public const string Advance = "advance";
    public const string VerifyGoal = "verify_goal";
    public const string Complete = "complete";
    public const string Wait = "wait";
    public const string NeedsUser = "needs_user";
    public const string Stop = "stop";
}

/// <summary>
/// ADR-092 §6.1：纯结算决策——只根据条件/结果/verdict 计算处置与是否已验证验收，
/// 不读写任何状态、不执行工具。Store 与 Verifier 共用它，避免“Turn 结束即完成”的分支散落。
/// </summary>
public static class GoalSettlementDecisionCalculator
{
    /// <summary>依赖等待族：可恢复的等待，既不是失败，也不是人工决定。</summary>
    private static readonly string[] WaitBlockerCodes =
    [
        "dependency_wait",
        "evidence_pending",
        "approval_review_profile_not_configured",
        "approval_review_service_unavailable",
        "approval_review_timeout",
        "approval_review_call_failed",
        "check_results_pending",
        // acceptance_contract_missing 不在此列：空合同是系统能执行的有界规划工作
        // （在本轮生成版本化条件与检查定义），必须有可执行的修复步骤，
        // 而不是等待一个永远不会到来的事件。
    ];

    /// <summary>
    /// P0-2（ADR-092 §7）：基础设施类失败阻塞码 —— 租约/fence/存储证据等非业务失败。
    /// 这些码连续出现计入 ConsecutiveInfraFailures；成功一次归零。业务类失败
    /// （criterion 未通过、iteration_failed 透传的 errorCode 等）不在此列。
    /// </summary>
    private static readonly string[] InfraFailureBlockerCodes =
    [
        "evidence_incomplete",
        "reservation_fence_lost",
    ];

    /// <summary>只有不可恢复的阻塞码允许终止 Goal。</summary>
    private static readonly string[] UnrecoverableBlockerCodes =
    [
        "task_plan_state_invalid",
        "unsafe",
        "cancelled",
        // 迭代被显式取消：ADR-092 把"明确取消"列为终态，不得降级为 repair 继续推进
        // （gates 实际发的是 iteration_cancelled，旧词汇表只有 cancelled，属错位）。
        "iteration_cancelled",
        // iteration_failed（回合硬失败）刻意**不在此列**：ADR-092「对旧设计的修订」逐字修订了
        // “非 completed Turn 一律终止整个目标”，并在取舍表中把“测试失败直接 Goal Failed，
        // 再新建 Goal 重试”列为不采用（丢失目标身份、归属和累计预算，形成任务碎片）。
        // 因此回合失败 = 本单元内可修复的未通过（Repair）：Goal 保持 Active、保留逻辑归属，
        // 由下一轮 continuation 续行；只有图损坏/显式取消等不可恢复情形才终结目标。
        // 代价：旧 ADR-074 语义下断言“回合失败 ⇒ Goal Failed”的测试需按 ADR-092 重定基线。
    ];

    // 注：gates 产生的 evidence_incomplete / reservation_fence_lost 以及透传的 errorCode 不在上述两集合时
    // 按 repair（本轮做有界修复/重扫）处理：它们可重试，但把它们归入 wait 会把目标卡在一个未必到来的事件上。

    /// <summary>P0-2：等待族阻塞码白名单判断（合法等待不参与熔断计数）。</summary>
    public static bool IsWaitBlockerCode(string? blockerCode)
        => !string.IsNullOrWhiteSpace(blockerCode)
           && WaitBlockerCodes.Contains(blockerCode, StringComparer.Ordinal);

    /// <summary>P0-2：基础设施类失败阻塞码判断（租约/fence/存储等非业务失败）。</summary>
    public static bool IsInfraFailureBlockerCode(string? blockerCode)
        => !string.IsNullOrWhiteSpace(blockerCode)
           && InfraFailureBlockerCodes.Contains(blockerCode, StringComparer.Ordinal);

    /// <summary>
    /// ADR-092 §6.2：按条件聚合其全部关联检查结果（同一条件可有多个必需检查）。
    /// 严重度 failed &gt; invalidated &gt; waiting &gt; pending &gt; passed，与结果列表顺序无关；
    /// 未知状态一律高于 passed（不得因无法解释的状态而通过）。
    /// </summary>
    public static IReadOnlyList<GoalCriterionResult> AggregateByCriterion(GoalVerificationDecision decision)
    {
        var aggregated = new List<GoalCriterionResult>();
        foreach (var group in decision.CriterionResults.GroupBy(
                     result => (result.CriterionId, result.CriterionRevision)))
        {
            var worst = group.First();
            foreach (var candidate in group)
            {
                if (StatusSeverity(candidate.Status) > StatusSeverity(worst.Status))
                    worst = candidate;
            }
            aggregated.Add(worst);
        }

        return aggregated;
    }

    private static int StatusSeverity(string? status) => status switch
    {
        GoalCriterionResultStatuses.Failed => 5,
        GoalCriterionResultStatuses.Invalidated => 4,
        GoalCriterionResultStatuses.Waiting => 3,
        GoalCriterionResultStatuses.Passed => 1,
        _ => 2,
    };

    /// <summary>
    /// 全部必需条件的全部关联检查都必须是同版本且 passed 才为真。
    /// 空合同一律返回 false；同一条件的任一检查 failed/waiting/pending/invalidated 或缺失都不得通过。
    /// </summary>
    public static bool AllRequiredCriteriaPassed(GoalVerificationDecision decision)
    {
        var required = decision.Criteria.Where(criterion => criterion.Required).ToList();
        if (required.Count == 0)
            return false;

        var aggregated = AggregateByCriterion(decision);
        foreach (var criterion in required)
        {
            var result = aggregated.FirstOrDefault(
                candidate => string.Equals(candidate.CriterionId, criterion.Id, StringComparison.Ordinal)
                             && candidate.CriterionRevision == criterion.Revision);

            if (result is null
                || !string.Equals(result.Status, GoalCriterionResultStatuses.Passed, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>当前单元是否已有可用的通过验收（推进/完成的唯一依据）。</summary>
    public static bool HasVerifiedAcceptance(GoalVerificationDecision decision)
        => AllRequiredCriteriaPassed(decision);

    /// <summary>按条件聚合后的最差状态为 failed 即为真（与列表顺序无关）。</summary>
    public static bool HasFailedCriteria(GoalVerificationDecision decision)
        => AggregateByCriterion(decision).Any(
            result => string.Equals(result.Status, GoalCriterionResultStatuses.Failed, StringComparison.Ordinal));

    public static bool IsWaiting(GoalVerificationDecision decision)
        => string.Equals(ComputeDisposition(decision), GoalSettlementDispositions.Wait, StringComparison.Ordinal);

    /// <summary>ADR-092 §6.1：由 verdict + 条件结果推导处置；未知一律保守为 repair，不得默认完成。</summary>
    public static string ComputeDisposition(GoalVerificationDecision decision)
    {
        if (decision.Verdict == GoalVerificationVerdict.Unsafe)
            return GoalSettlementDispositions.Stop;

        if (decision.Verdict == GoalVerificationVerdict.NeedsUser)
            return GoalSettlementDispositions.NeedsUser;

        if (decision.Verdict == GoalVerificationVerdict.Blocked)
        {
            var blocker = decision.BlockerCode ?? string.Empty;
            if (WaitBlockerCodes.Contains(blocker, StringComparer.Ordinal))
                return GoalSettlementDispositions.Wait;

            return UnrecoverableBlockerCodes.Contains(blocker, StringComparer.Ordinal)
                ? GoalSettlementDispositions.Stop
                : GoalSettlementDispositions.Repair;
        }

        // Continue/Complete 但没有任何已验证必需条件：Turn 结束不等于单元完成。
        if (!HasVerifiedAcceptance(decision))
        {
            return HasFailedCriteria(decision)
                ? GoalSettlementDispositions.Repair
                : GoalSettlementDispositions.ContinueCurrent;
        }

        return decision.Verdict == GoalVerificationVerdict.Complete
            ? GoalSettlementDispositions.Complete
            : GoalSettlementDispositions.Advance;
    }
}

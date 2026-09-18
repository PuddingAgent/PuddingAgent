using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PuddingCode.Goals;

namespace PuddingPlatform.Services.Goals;

/// <summary>
/// G92-1 S1-c 片6（A1）3a：proposal 验证输入 —— 全部身份字段来自结算候选/DB 的可信事实，
/// 绝不接受 proposal 自报的 goalRunId/epoch/turnId/objectiveVersion 等身份字段。
/// <see cref="ProposalJson"/> 是 turn.completed payload 独立键 goal_contract_proposal 的原始
/// JSON（经 <see cref="GoalContractProposalValidator.ReadProposalFromTurnCompletedPayload"/>
/// 提取；历史行无该键时为 null）。
/// </summary>
public sealed record GoalContractProposalFacts
{
    public required string GoalRunId { get; init; }

    public required int ActivationEpoch { get; init; }

    public required int ObjectiveVersion { get; init; }

    /// <summary>结算候选所属 canonical Turn 的 TurnId（幂等键与审计 CausationId）。</summary>
    public required string TurnId { get; init; }

    /// <summary>结算候选的 AggregateVersion（身份绑定元组一员；op key 不含它，聚合版本 CAS 归结算侧）。</summary>
    public required int AggregateVersion { get; init; }

    /// <summary>frozen objective 原文 —— requirementRefs 覆盖检查的唯一依据（ordinal，不归一）。</summary>
    public required string Objective { get; init; }

    /// <summary>canonical Turn 终态 kind；只接受 completed（turn.completed）。</summary>
    public required string TerminalKind { get; init; }

    /// <summary>证据完备事实（持久 ToolCallRequested 等扫描结论），proposal 要求为 true。</summary>
    public required bool EvidenceComplete { get; init; }

    public string? ProposalJson { get; init; }
}

/// <summary>
/// 验证成功后的派生产物：全部身份/哈希/版本/ID 均由平台计算，调用方（Worker/Store）不得改写。
/// </summary>
public sealed record GoalContractRefinementPlan
{
    public required string GoalRunId { get; init; }

    public required int ActivationEpoch { get; init; }

    public required int ObjectiveVersion { get; init; }

    public required string TurnId { get; init; }

    /// <summary>Agent 提交的乐观并发期望值；CAS 提交时由 Store 与当前行逐字校验。</summary>
    public required int ExpectedContractVersion { get; init; }

    /// <summary>SHA-256(goalRunId|activationEpoch|objectiveVersion|turnId|canonicalProposalJson)，服务端派生。</summary>
    public required string OperationKey { get; init; }

    /// <summary>canonical proposal JSON：由解析后的记录确定性重序列化（camelCase），与原始字节/空白无关。</summary>
    public required string CanonicalProposalJson { get; init; }

    public required IReadOnlyList<GoalCriterion> Criteria { get; init; }

    public required IReadOnlyList<GoalCheckSpec> Checks { get; init; }

    /// <summary>审计事件 payload 专用摘要（只有 ID 与 hash，绝无自然语言）。</summary>
    public required IReadOnlyList<string> CriterionIds { get; init; }

    public required IReadOnlyList<string> DefinitionHashes { get; init; }
}

/// <summary>验证结果：要么带完整计划，要么带 fail-closed 拒绝原因，绝不部分接受。</summary>
public sealed record GoalContractProposalValidation
{
    public GoalContractRefinementPlan? Plan { get; init; }

    public string? RejectionReason { get; init; }

    public bool IsAccepted => Plan is not null;

    public static GoalContractProposalValidation Accept(GoalContractRefinementPlan plan) => new() { Plan = plan };

    public static GoalContractProposalValidation Reject(string reason) => new() { RejectionReason = reason };
}

/// <summary>turn.completed payload 中 goal_contract_proposal 键的读取结果。</summary>
public sealed record GoalContractProposalPayloadReadResult
{
    /// <summary>历史行无该键（或显式 null）⇒ false 且无拒绝原因（按"无提议"处理）。</summary>
    public bool HasProposal { get; init; }

    public string? ProposalJson { get; init; }

    public string? RejectionReason { get; init; }
}

/// <summary>
/// G92-1 S1-c 片6（A1）3a：服务端 proposal 验证器（纯函数、fail-closed、无 I/O）。
/// <para>
/// 职责：① 从 turn.completed payload 提取 proposal（TryGetProperty，兼容无键历史行）；
/// ② 经 <see cref="GoalContractProposalParser"/>（唯一解析入口）拒绝一切自报身份/未知字段；
/// ③ canonical Turn 事实门（仅 turn.completed + EvidenceComplete=true）；
/// ④ 受控验证声明门（仅 text-assertion + 已登记 checks/text-assertion.md#equals，期望文本进 hash）；
/// ⑤ requirementRefs 对回 frozen objective（拒绝空覆盖/无关引用/删义务）；
/// ⑥ 服务端派生 canonical operation key 与 criterion/check 集（Registry/派生值，绝不信 Agent）。
/// 合同 source/版本比对与 CAS 属于 <see cref="GoalContractRefinementStore"/>（DB 态判定）。
/// </para>
/// </summary>
public static class GoalContractProposalValidator
{
    public const string CompletedTerminalKind = "completed";

    /// <summary>canonical proposal JSON 序列化选项：与段2 持久化写入同形（camelCase、无缩进、声明序）。</summary>
    private static readonly JsonSerializerOptions CanonicalJsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// 从 turn.completed payload 提取独立键 goal_contract_proposal（段2 写入形状：嵌套 JSON 对象）。
    /// 历史行没有该键（或显式 null）⇒ HasProposal=false（按"无提议"处理，不抛异常）；
    /// 键存在但不是 JSON 对象 ⇒ fail-closed 返回拒绝原因。必须走 TryGetProperty，绝不异常控制流。
    /// </summary>
    public static GoalContractProposalPayloadReadResult ReadProposalFromTurnCompletedPayload(
        JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return new GoalContractProposalPayloadReadResult();

        if (!payload.TryGetProperty("goal_contract_proposal", out var element))
            return new GoalContractProposalPayloadReadResult();

        if (element.ValueKind == JsonValueKind.Null)
            return new GoalContractProposalPayloadReadResult();

        return element.ValueKind != JsonValueKind.Object
            ? new GoalContractProposalPayloadReadResult
            {
                RejectionReason = $"goal_contract_proposal must be a json object, got {element.ValueKind}",
            }
            : new GoalContractProposalPayloadReadResult
            {
                HasProposal = true,
                ProposalJson = element.GetRawText(),
            };
    }

    /// <summary>
    /// 内容级验证 + 平台侧派生。null/空白 proposalJson 按"无提议"拒绝（调用方应先用
    /// <see cref="ReadProposalFromTurnCompletedPayload"/> 判定有无提议再进入本方法）。
    /// </summary>
    public static GoalContractProposalValidation Validate(GoalContractProposalFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (string.IsNullOrWhiteSpace(facts.GoalRunId) || string.IsNullOrWhiteSpace(facts.TurnId))
            return GoalContractProposalValidation.Reject("goalRunId and turnId are required platform-side facts");

        if (!string.Equals(facts.TerminalKind, CompletedTerminalKind, StringComparison.OrdinalIgnoreCase))
            return GoalContractProposalValidation.Reject(
                $"proposal only binds canonical turn.completed terminals, got '{facts.TerminalKind}'");

        if (!facts.EvidenceComplete)
            return GoalContractProposalValidation.Reject(
                "evidence incomplete: a contract proposal requires EvidenceComplete=true on the canonical turn");

        if (string.IsNullOrWhiteSpace(facts.ProposalJson))
            return GoalContractProposalValidation.Reject("no goal_contract_proposal payload present");

        var parse = GoalContractProposalParser.Parse(facts.ProposalJson!);
        if (!parse.IsAccepted || parse.Proposal is null)
            return GoalContractProposalValidation.Reject($"proposal rejected by parser: {parse.RejectionReason}");

        var proposal = parse.Proposal;
        if (proposal.ExpectedContractVersion < 1)
            return GoalContractProposalValidation.Reject("'expectedContractVersion' must be a positive integer");

        var refFailure = ValidateCoverage(proposal, facts.Objective);
        if (refFailure is not null)
            return GoalContractProposalValidation.Reject(refFailure);

        var derived = DeriveContractItems(proposal, facts);
        if (derived.Reason is not null || derived.Criteria is null)
            return GoalContractProposalValidation.Reject(derived.Reason ?? "criteria produced no valid items (registry drift)");

        var canonicalProposalJson = JsonSerializer.Serialize(proposal, CanonicalJsonOptions);
        var operationKey = ComputeOperationKey(
            facts.GoalRunId,
            facts.ActivationEpoch,
            facts.ObjectiveVersion,
            facts.TurnId,
            canonicalProposalJson);

        return GoalContractProposalValidation.Accept(new GoalContractRefinementPlan
        {
            GoalRunId = facts.GoalRunId,
            ActivationEpoch = facts.ActivationEpoch,
            ObjectiveVersion = facts.ObjectiveVersion,
            TurnId = facts.TurnId,
            ExpectedContractVersion = proposal.ExpectedContractVersion,
            OperationKey = operationKey,
            CanonicalProposalJson = canonicalProposalJson,
            Criteria = derived.Criteria,
            Checks = derived.Checks!,
            CriterionIds = derived.CriterionIds,
            DefinitionHashes = derived.DefinitionHashes,
        });
    }

    private record DerivedItems(
        List<GoalCriterion>? Criteria,
        List<GoalCheckSpec>? Checks,
        List<string> CriterionIds,
        List<string> DefinitionHashes,
        string? Reason = null);

    private static DerivedItems Failure(string reason) => new(null, null, [], [], reason);

    /// <summary>
    /// 受控验证声明派生门：A1 提议只允许 text-assertion（已登记 checks/text-assertion.md#equals，
    /// 期望文本必填并参与 DefinitionHash —— 方案 A）；build/test/semantic/file-evidence 等其余 kind
    /// 一律拒绝（A1 是目标级覆盖通道，不附加工程门禁，更不允许 Agent 自设工程门）。
    /// inputRefs 必须为空：text-assertion 的证据是 canonical Turn 终态最终 assistant 输出。
    /// </summary>
    private static DerivedItems DeriveContractItems(GoalContractProposal proposal, GoalContractProposalFacts facts)
    {
        var criteria = new List<GoalCriterion>();
        var checks = new List<GoalCheckSpec>();
        var criterionIds = new List<string>();
        var definitionHashes = new List<string>();
        var inputFingerprint = $"epoch:{facts.ActivationEpoch}:objective:{facts.ObjectiveVersion}";

        for (var index = 0; index < proposal.Criteria.Count; index++)
        {
            var item = proposal.Criteria[index];
            if (!string.Equals(item.Verification.Kind, GoalVerificationSpecKinds.TextAssertion, StringComparison.Ordinal))
                return Failure($"criteria[{index}].verification.kind must be '{GoalVerificationSpecKinds.TextAssertion}' in an A1 proposal, got '{item.Verification.Kind}'");

            if (!string.Equals(item.Verification.DefinitionRef, GoalCheckDefinitionRegistry.TextAssertionRef, StringComparison.Ordinal))
                return Failure($"criteria[{index}].verification.definitionRef must be the registered '{GoalCheckDefinitionRegistry.TextAssertionRef}', got '{item.Verification.DefinitionRef}'");

            if (item.Verification.ExpectedText is null)
                return Failure($"criteria[{index}].verification.expectedText is required for text-assertion (ordinal exact; it enters the definition hash)");

            if (item.Verification.InputRefs.Count > 0)
                return Failure($"criteria[{index}].verification.inputRefs must be empty for text-assertion (evidence is the canonical turn's final assistant output)");

            if (!GoalCheckDefinitionRegistry.TryResolve(GoalCheckDefinitionRegistry.TextAssertionRef, out var definition))
                return Failure("text-assertion definition is not registered (registry drift)");

            // 期望文本按方案 A 进入定义 hash 载荷：定义或期望文本变化 ⇒ hash 变化 ⇒ 旧报告失效。
            var definitionHash = GoalCheckDefinitionRegistry.ComputeDefinitionHash(
                definition with { ExpectedText = item.Verification.ExpectedText });
            var criterionId = $"objective-text-assertion:{index}";

            criteria.Add(new GoalCriterion
            {
                Id = criterionId,
                Revision = 1,
                Requirement = item.Requirement,
                Required = true,
                Kind = GoalVerificationSpecKinds.TextAssertion,
                DefinitionRef = GoalCheckDefinitionRegistry.TextAssertionRef,
                DefinitionHash = definitionHash,
                InputRefs = [],
                ExecutorRole = "core",
            });
            checks.Add(new GoalCheckSpec
            {
                CheckId = $"objective:text-assertion:{index}",
                CriterionId = criterionId,
                CriterionRevision = 1,
                Kind = GoalVerificationSpecKinds.TextAssertion,
                DefinitionRef = GoalCheckDefinitionRegistry.TextAssertionRef,
                DefinitionHash = definitionHash,
                ExpectedText = item.Verification.ExpectedText,
                InputRefs = [],
                InputFingerprint = inputFingerprint,
                ExecutorRole = "core",
                ExpectedEvidence = "canonical turn 的最终 assistant 输出与期望文本 ordinal 精确相等",
            });
            criterionIds.Add(criterionId);
            definitionHashes.Add(definitionHash);
        }

        return new DerivedItems(criteria, checks, criterionIds, definitionHashes);
    }

    /// <summary>
    /// requirementRefs 对回 frozen objective（规格 §A1_SHAPE 4 / WHY 4）：
    /// ① 每条 ref 至少包含一个 objective 词元（拒绝空覆盖/与目标无关的引用）；
    /// ② 全部 ref 的词元并集必须覆盖 objective 的全部词元（拒绝删义务：提议不得丢掉目标的一部分）。
    /// 词元切分只按 Unicode 空白/标点/符号断开，不做大小写折叠、不做 Unicode 归一（ordinal 语义）。
    /// objective 为空词元时防御性放行（Goal 创建侧已保证非空 objective）。
    /// </summary>
    private static string? ValidateCoverage(GoalContractProposal proposal, string objective)
    {
        var objectiveTokens = Tokenize(objective);
        if (objectiveTokens.Count == 0)
            return null;

        var objectiveSet = new HashSet<string>(objectiveTokens, StringComparer.Ordinal);
        var covered = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < proposal.Criteria.Count; index++)
        {
            foreach (var reference in proposal.Criteria[index].RequirementRefs)
            {
                var referenceTokens = Tokenize(reference);
                if (referenceTokens.Count == 0 || !referenceTokens.Any(objectiveSet.Contains))
                    return $"criteria[{index}].requirementRefs entry does not reference the frozen objective";
                covered.UnionWith(referenceTokens);
            }
        }

        var missing = objectiveTokens
            .Where(token => !covered.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return missing.Count > 0
            ? $"requirementRefs do not cover the whole objective; missing: {string.Join(' ', missing)}"
            : null;
    }

    /// <summary>按 Unicode 空白/标点/符号切词元；保留字母数字与 CJK 连续串（ordinal，不归一）。</summary>
    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0)
            tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>canonical operation key：SHA-256(goalRunId|activationEpoch|objectiveVersion|turnId|canonicalProposalJson)。</summary>
    public static string ComputeOperationKey(
        string goalRunId,
        int activationEpoch,
        int objectiveVersion,
        string turnId,
        string canonicalProposalJson)
    {
        var payload = $"{goalRunId}|{activationEpoch}|{objectiveVersion}|{turnId}|{canonicalProposalJson}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return $"sha256:{Convert.ToHexStringLower(bytes)}";
    }
}

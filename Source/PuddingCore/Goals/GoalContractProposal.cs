using System.Text.Json;

namespace PuddingCode.Goals;

/// <summary>
/// G92-1 S1-c 片6（A1）：Agent 在 canonical Goal Turn 的 hidden metadata 中提出的
/// 验收合同整理提议（最小 payload）。本类型只承载 shape 契约；goalRunId/epoch/turnId/
/// criterionRevision/definitionHash/source/status/passed 等身份与状态字段由平台从可信
/// candidate/DB 派生，Agent 一律不得自报（解析层 fail-closed，见
/// <see cref="GoalContractProposalParser"/>）。
/// </summary>
public sealed record GoalContractProposal
{
    public const int CurrentSchemaVersion = 1;

    public const string RefineAcceptanceContractKind = "refine_acceptance_contract";

    public required int SchemaVersion { get; init; }

    public required string Kind { get; init; }

    /// <summary>乐观并发：平台侧当前合同 ContractVersion 的期望值；CAS 提交时逐字校验。</summary>
    public required int ExpectedContractVersion { get; init; }

    public required IReadOnlyList<GoalContractProposalCriterion> Criteria { get; init; }
}

/// <summary>提议中的单条验收条件（Agent 视角）：requirement + 对 objective 的引用/覆盖说明 + 受控验证声明。</summary>
public sealed record GoalContractProposalCriterion
{
    public required string Requirement { get; init; }

    /// <summary>对 frozen objective 的精确引用/覆盖说明；平台 validator 逐条回对 objective。</summary>
    public required IReadOnlyList<string> RequirementRefs { get; init; }

    public required GoalContractProposalVerification Verification { get; init; }
}

/// <summary>
/// 提议中的受控验证声明：只允许 kind/definitionRef/inputRefs/expectedText；
/// definitionHash/criterionRevision/status/passed 等由 Registry/Planner 派生，禁止出现。
/// </summary>
public sealed record GoalContractProposalVerification
{
    public required string Kind { get; init; }

    /// <summary>版本化检查定义引用（如 checks/text-assertion.md#equals），不是自由 shell 字符串。</summary>
    public required string DefinitionRef { get; init; }

    public required IReadOnlyList<string> InputRefs { get; init; }

    /// <summary>可选：text-assertion 的 ordinal exact 期望文本。</summary>
    public string? ExpectedText { get; init; }
}

/// <summary>
/// A1 proposal 解析结果：要么带完整 proposal，要么带拒绝原因；fail-closed，绝不部分接受。
/// </summary>
public sealed record GoalContractProposalParseResult
{
    public GoalContractProposal? Proposal { get; init; }

    public string? RejectionReason { get; init; }

    public bool IsAccepted => Proposal is not null;

    public static GoalContractProposalParseResult Accepted(GoalContractProposal proposal)
        => new() { Proposal = proposal };

    public static GoalContractProposalParseResult Rejected(string reason)
        => new() { RejectionReason = reason };
}

/// <summary>
/// A1 proposal 的唯一 fail-closed 解析入口：未知/额外字段、Agent 自报身份字段、
/// 缺失必需字段、重复属性一律拒绝（绝不静默忽略）。属性名大小写敏感（Ordinal）。
/// </summary>
public static class GoalContractProposalParser
{
    /// <summary>规格 A1_SHAPE §2：Agent 不得自报的身份/状态字段，出现在任何对象层都拒绝。</summary>
    private static readonly HashSet<string> AgentForbiddenFields = new(StringComparer.Ordinal)
    {
        "goalRunId", "activationEpoch", "turnId", "criterionRevision",
        "definitionHash", "source", "status", "passed",
    };

    private static readonly HashSet<string> TopLevelFields = new(StringComparer.Ordinal)
    {
        "schemaVersion", "kind", "expectedContractVersion", "criteria",
    };

    private static readonly HashSet<string> CriterionFields = new(StringComparer.Ordinal)
    {
        "requirement", "requirementRefs", "verification",
    };

    private static readonly HashSet<string> VerificationFields = new(StringComparer.Ordinal)
    {
        "kind", "definitionRef", "inputRefs", "expectedText",
    };

    public static GoalContractProposalParseResult Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return GoalContractProposalParseResult.Rejected($"invalid json: {ex.Message}");
        }

        using (document)
        {
            return document.RootElement.ValueKind != JsonValueKind.Object
                ? GoalContractProposalParseResult.Rejected(
                    $"root must be a json object, got {document.RootElement.ValueKind}")
                : ParseRoot(document.RootElement);
        }
    }

    private static GoalContractProposalParseResult ParseRoot(JsonElement root)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int? schemaVersion = null;
        string? kind = null;
        int? expectedContractVersion = null;
        var criteria = new List<GoalContractProposalCriterion>();

        foreach (var property in root.EnumerateObject())
        {
            var verdict = CheckFieldName(property.Name, TopLevelFields, "root");
            if (verdict is not null)
                return GoalContractProposalParseResult.Rejected(verdict);
            if (!seen.Add(property.Name))
                return GoalContractProposalParseResult.Rejected(
                    $"duplicate field '{property.Name}' at root");

            switch (property.Name)
            {
                case "schemaVersion":
                    if (!TryGetInt(property.Value, out var parsedSchemaVersion))
                        return GoalContractProposalParseResult.Rejected(
                            "'schemaVersion' must be an integer at root");
                    schemaVersion = parsedSchemaVersion;
                    break;
                case "kind":
                    if (property.Value.ValueKind != JsonValueKind.String)
                        return GoalContractProposalParseResult.Rejected(
                            "'kind' must be a string at root");
                    kind = property.Value.GetString();
                    break;
                case "expectedContractVersion":
                    if (!TryGetInt(property.Value, out var parsedVersion) || parsedVersion < 1)
                        return GoalContractProposalParseResult.Rejected(
                            "'expectedContractVersion' must be a positive integer at root");
                    expectedContractVersion = parsedVersion;
                    break;
                case "criteria":
                    if (property.Value.ValueKind != JsonValueKind.Array)
                        return GoalContractProposalParseResult.Rejected(
                            "'criteria' must be an array at root");
                    foreach (var item in property.Value.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object)
                            return GoalContractProposalParseResult.Rejected(
                                $"'criteria[{criteria.Count}]' must be a json object");
                        var criterion = ParseCriterion(item, criteria.Count, out var criterionError);
                        if (criterion is null)
                            return GoalContractProposalParseResult.Rejected(criterionError!);
                        criteria.Add(criterion);
                    }
                    break;
            }
        }

        if (schemaVersion != GoalContractProposal.CurrentSchemaVersion)
            return GoalContractProposalParseResult.Rejected(
                $"'schemaVersion' must be {GoalContractProposal.CurrentSchemaVersion}, got {Describe(schemaVersion)}");
        if (!string.Equals(kind, GoalContractProposal.RefineAcceptanceContractKind, StringComparison.Ordinal))
            return GoalContractProposalParseResult.Rejected(
                $"'kind' must be '{GoalContractProposal.RefineAcceptanceContractKind}', got {kind ?? "missing"}");
        if (expectedContractVersion is null)
            return GoalContractProposalParseResult.Rejected("'expectedContractVersion' is required at root");
        if (criteria.Count == 0)
            return GoalContractProposalParseResult.Rejected("'criteria' must contain at least one criterion");

        return GoalContractProposalParseResult.Accepted(new GoalContractProposal
        {
            SchemaVersion = schemaVersion.Value,
            Kind = kind!,
            ExpectedContractVersion = expectedContractVersion.Value,
            Criteria = criteria,
        });
    }

    private static GoalContractProposalCriterion? ParseCriterion(
        JsonElement item,
        int index,
        out string? error)
    {
        var scope = $"criteria[{index}]";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? requirement = null;
        IReadOnlyList<string>? requirementRefs = null;
        GoalContractProposalVerification? verification = null;

        foreach (var property in item.EnumerateObject())
        {
            var verdict = CheckFieldName(property.Name, CriterionFields, scope);
            if (verdict is not null)
            {
                error = verdict;
                return null;
            }
            if (!seen.Add(property.Name))
            {
                error = $"duplicate field '{property.Name}' at {scope}";
                return null;
            }

            switch (property.Name)
            {
                case "requirement":
                    if (!TryGetNonEmptyString(property.Value, out requirement))
                    {
                        error = $"'requirement' must be a non-empty string at {scope}";
                        return null;
                    }
                    break;
                case "requirementRefs":
                    if (!TryGetStringList(property.Value, out requirementRefs, out var refsError))
                    {
                        error = $"{refsError} at {scope}";
                        return null;
                    }
                    break;
                case "verification":
                    if (property.Value.ValueKind != JsonValueKind.Object)
                    {
                        error = $"'verification' must be a json object at {scope}";
                        return null;
                    }
                    verification = ParseVerification(property.Value, scope, out var verificationError);
                    if (verification is null)
                    {
                        error = verificationError;
                        return null;
                    }
                    break;
            }
        }

        if (requirement is null)
        {
            error = $"'requirement' is required at {scope}";
            return null;
        }
        if (requirementRefs is null)
        {
            error = $"'requirementRefs' is required at {scope}";
            return null;
        }
        if (verification is null)
        {
            error = $"'verification' is required at {scope}";
            return null;
        }

        error = null;
        return new GoalContractProposalCriterion
        {
            Requirement = requirement,
            RequirementRefs = requirementRefs,
            Verification = verification,
        };
    }

    private static GoalContractProposalVerification? ParseVerification(
        JsonElement item,
        string parentScope,
        out string? error)
    {
        var scope = $"{parentScope}.verification";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? kind = null;
        string? definitionRef = null;
        IReadOnlyList<string>? inputRefs = null;
        string? expectedText = null;

        foreach (var property in item.EnumerateObject())
        {
            var verdict = CheckFieldName(property.Name, VerificationFields, scope);
            if (verdict is not null)
            {
                error = verdict;
                return null;
            }
            if (!seen.Add(property.Name))
            {
                error = $"duplicate field '{property.Name}' at {scope}";
                return null;
            }

            switch (property.Name)
            {
                case "kind":
                    if (!TryGetNonEmptyString(property.Value, out kind))
                    {
                        error = $"'verification.kind' must be a non-empty string at {scope}";
                        return null;
                    }
                    break;
                case "definitionRef":
                    if (!TryGetNonEmptyString(property.Value, out definitionRef))
                    {
                        error = $"'verification.definitionRef' must be a non-empty string at {scope}";
                        return null;
                    }
                    break;
                case "inputRefs":
                    if (!TryGetStringList(property.Value, out inputRefs, out var refsError))
                    {
                        error = $"{refsError} at {scope}";
                        return null;
                    }
                    break;
                case "expectedText":
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        error = $"'verification.expectedText' must be a string at {scope}";
                        return null;
                    }
                    expectedText = property.Value.GetString();
                    break;
            }
        }

        if (kind is null)
        {
            error = $"'verification.kind' is required at {scope}";
            return null;
        }
        if (definitionRef is null)
        {
            error = $"'verification.definitionRef' is required at {scope}";
            return null;
        }
        if (inputRefs is null)
        {
            error = $"'verification.inputRefs' is required at {scope}";
            return null;
        }

        error = null;
        return new GoalContractProposalVerification
        {
            Kind = kind,
            DefinitionRef = definitionRef,
            InputRefs = inputRefs,
            ExpectedText = expectedText,
        };
    }

    /// <summary>字段名门禁：先拒 Agent 禁自报字段（更精确理由），再拒未知/额外字段（fail-closed）。</summary>
    private static string? CheckFieldName(string name, HashSet<string> allowed, string scope)
        => AgentForbiddenFields.Contains(name)
            ? $"agent-forbidden field '{name}' at {scope}"
            : allowed.Contains(name)
                ? null
                : $"unknown field '{name}' at {scope}";

    private static bool TryGetInt(JsonElement value, out int result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result);
    }

    private static bool TryGetNonEmptyString(JsonElement value, out string result)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                result = text!;
                return true;
            }
        }
        result = string.Empty;
        return false;
    }

    private static bool TryGetStringList(
        JsonElement value,
        out IReadOnlyList<string> list,
        out string? error)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            list = [];
            error = "expected a string array";
            return false;
        }

        var items = new List<string>();
        foreach (var element in value.EnumerateArray())
        {
            if (!TryGetNonEmptyString(element, out var item))
            {
                list = [];
                error = "array items must be non-empty strings";
                return false;
            }
            items.Add(item);
        }

        list = items;
        error = null;
        return true;
    }

    private static string Describe(int? value) => value?.ToString() ?? "missing";
}

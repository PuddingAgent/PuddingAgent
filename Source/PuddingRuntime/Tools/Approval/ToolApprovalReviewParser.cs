using System.Text.Json;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>Parses strict JSON returned by an approval reviewer.</summary>
public static class ToolApprovalReviewParser
{
    public static ToolApprovalReviewResult Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return ProtocolFailure(ToolApprovalWire.CodeEmptyResponse, "Reviewer returned an empty response.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException ex)
        {
            return ProtocolFailure(ToolApprovalWire.CodeInvalidJson, "Invalid approval reviewer JSON: " + ex.Message);
        }

        using (document)
        {
            var root = document.RootElement;

            // F03：语法合法但根不是对象（null/[]/1/"text"）必须归入协议失败，不能抛异常也不能默认放行。
            if (root.ValueKind != JsonValueKind.Object)
                return ProtocolFailure(ToolApprovalWire.CodeNonObjectRoot, "Approval reviewer JSON root must be an object.");

            var decisionRaw = GetString(root, "decision");
            if (!ToolApprovalWire.TryParseDecision(decisionRaw, out var decision))
                return ProtocolFailure(
                    ToolApprovalWire.CodeUnknownDecision,
                    $"Invalid or missing approval reviewer decision '{decisionRaw}'.");

            if (HasWrongType(root, "decision", JsonValueKind.String)
                || HasWrongType(root, "reason", JsonValueKind.String)
                || HasWrongType(root, "decisionReason", JsonValueKind.String)
                || HasWrongType(root, "requiresHumanAuthorization", JsonValueKind.True, JsonValueKind.False)
                || HasWrongType(root, "allowedScope", JsonValueKind.String)
                || HasWrongType(root, "allowedDurationMinutes", JsonValueKind.Number))
            {
                return ProtocolFailure(
                    ToolApprovalWire.CodeInvalidFieldType,
                    "Approval reviewer JSON contains a field with an unexpected type.");
            }

            var reason = FirstNonEmpty(GetString(root, "reason"), GetString(root, "decisionReason"));
            if (string.IsNullOrWhiteSpace(reason))
                return ProtocolFailure(
                    ToolApprovalWire.CodeMissingReason,
                    "Approval reviewer JSON is missing a non-empty reason.");

            var requiresHuman = GetBool(root, "requiresHumanAuthorization")
                                || decision == ToolApprovalDecision.NeedHuman;

            // F03：跨字段一致性——批准不得同时要求人工授权；依赖等待不是人工决定。
            if (decision == ToolApprovalDecision.Approved && requiresHuman)
            {
                return ProtocolFailure(
                    ToolApprovalWire.CodeContradictory,
                    "Approval reviewer JSON declares approved but also requires human authorization.");
            }

            if (decision == ToolApprovalDecision.DeferredDependency)
                requiresHuman = false;

            var allowedScopeRaw = GetString(root, "allowedScope");
            ToolApprovalScope? allowedScope = null;
            if (!string.IsNullOrWhiteSpace(allowedScopeRaw))
            {
                if (!Enum.TryParse<ToolApprovalScope>(allowedScopeRaw, ignoreCase: true, out var parsedScope))
                {
                    return ProtocolFailure(
                        ToolApprovalWire.CodeInvalidFieldType,
                        $"Approval reviewer JSON declares an unknown allowedScope '{allowedScopeRaw}'.");
                }

                allowedScope = parsedScope;
            }

            var allowedDurationMinutes = GetInt(root, "allowedDurationMinutes");

            // F03：拒绝/等待结果不得携带生效的批准 scope、时长或 allowlist 提案。
            var approved = decision == ToolApprovalDecision.Approved;

            return new ToolApprovalReviewResult
            {
                Decision = decision,
                DecisionReason = reason,
                ReasonCode = GetString(root, "reasonCode"),
                AllowedScope = approved ? allowedScope : null,
                AllowedDuration = approved && allowedDurationMinutes is > 0
                    ? TimeSpan.FromMinutes(allowedDurationMinutes.Value)
                    : null,
                RequiresHumanAuthorization = requiresHuman,
                ChecklistFindings = GetStringArray(root, "checklistFindings"),
                MissingRequirements = GetStringArray(root, "missingRequirements"),
                AllowlistProposals = approved ? GetAllowlistProposals(root) : [],
                RecommendedFix = GetString(root, "recommendedFix"),
                ReviewerModel = GetString(root, "reviewerModel"),
            };
        }
    }

    /// <summary>
    /// ADR-091 §4.1 步骤 5：空响应、非法 JSON、非对象根、schema 不符属于依赖/协议失败，
    /// 既不是人工决定，也不得伪造批准。统一归入 DeferredDependency 并携带稳定 reasonCode。
    /// </summary>
    private static ToolApprovalReviewResult ProtocolFailure(string reasonCode, string reason)
        => new()
        {
            Decision = ToolApprovalDecision.DeferredDependency,
            DecisionReason = reason,
            ReasonCode = reasonCode,
            RequiresHumanAuthorization = false,
            MissingRequirements = ["schema-valid review response"],
            RecommendedFix = "Restore the isolated review dependency and retry the same invocation; this is a dependency wait, not a human authorization request.",
        };

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    /// <summary>属性存在但类型不符时返回 true；属性缺失不算类型错误。</summary>
    private static bool HasWrongType(JsonElement root, string name, params JsonValueKind[] allowed)
    {
        if (!root.TryGetProperty(name, out var value))
            return false;

        if (value.ValueKind == JsonValueKind.Null)
            return false;

        return !allowed.Contains(value.ValueKind);
    }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static bool GetBool(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static IReadOnlyList<string> GetStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];

        return value.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToArray();
    }

    private static IReadOnlyList<ToolApprovalAllowlistProposal> GetAllowlistProposals(JsonElement root)
    {
        var proposals = new List<ToolApprovalAllowlistProposal>();
        if (root.TryGetProperty("allowlistProposals", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var proposal = ParseAllowlistProposal(item);
                if (proposal is not null)
                    proposals.Add(proposal);
            }
        }

        if (root.TryGetProperty("allowlistProposal", out var single))
        {
            var proposal = ParseAllowlistProposal(single);
            if (proposal is not null)
                proposals.Add(proposal);
        }

        return proposals;
    }

    private static ToolApprovalAllowlistProposal? ParseAllowlistProposal(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return null;

        var toolId = GetString(value, "toolId") ?? GetString(value, "tool_id");
        var command = GetString(value, "command") ?? GetString(value, "commandName") ?? GetString(value, "command_name");
        var argumentsJson = GetJsonText(value, "argumentsJson")
                            ?? GetJsonText(value, "arguments_json")
                            ?? GetJsonText(value, "requestedArgumentsJson")
                            ?? GetJsonText(value, "requested_arguments_json")
                            ?? GetJsonText(value, "arguments");
        var reason = GetString(value, "reason");
        if (string.IsNullOrWhiteSpace(command) && string.IsNullOrWhiteSpace(argumentsJson))
            return null;

        return new ToolApprovalAllowlistProposal
        {
            ToolId = toolId,
            Command = command,
            ArgumentsJson = argumentsJson,
            Reason = reason,
        };
    }

    private static string? GetJsonText(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
            _ => null,
        };
    }
}

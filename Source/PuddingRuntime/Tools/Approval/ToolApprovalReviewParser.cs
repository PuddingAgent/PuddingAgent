using System.Text.Json;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>Parses strict JSON returned by an approval reviewer.</summary>
public static class ToolApprovalReviewParser
{
    public static ToolApprovalReviewResult Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Invalid("Reviewer returned an empty response.");

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var decisionRaw = GetString(root, "decision");
            if (!TryParseDecision(decisionRaw, out var decision))
                return Invalid($"Invalid approval reviewer decision '{decisionRaw}'.");

            var reason = GetString(root, "reason")
                         ?? GetString(root, "decisionReason")
                         ?? "Approval reviewer did not provide a reason.";
            var allowedScope = TryParseScope(GetString(root, "allowedScope"));
            var allowedDurationMinutes = GetInt(root, "allowedDurationMinutes");

            return new ToolApprovalReviewResult
            {
                Decision = decision,
                DecisionReason = reason,
                AllowedScope = allowedScope,
                AllowedDuration = allowedDurationMinutes is > 0
                    ? TimeSpan.FromMinutes(allowedDurationMinutes.Value)
                    : null,
                RequiresHumanAuthorization = GetBool(root, "requiresHumanAuthorization") || decision == ToolApprovalDecision.NeedHuman,
                ChecklistFindings = GetStringArray(root, "checklistFindings"),
                MissingRequirements = GetStringArray(root, "missingRequirements"),
                AllowlistProposals = GetAllowlistProposals(root),
                RecommendedFix = GetString(root, "recommendedFix"),
                ReviewerModel = GetString(root, "reviewerModel"),
            };
        }
        catch (JsonException ex)
        {
            return Invalid("Invalid approval reviewer JSON: " + ex.Message);
        }
    }

    private static ToolApprovalReviewResult Invalid(string reason)
        => new()
        {
            Decision = ToolApprovalDecision.NeedHuman,
            DecisionReason = reason,
            RequiresHumanAuthorization = true,
            MissingRequirements = ["valid reviewer JSON"],
            RecommendedFix = "Retry request_tool_approval with valid approval facts. Use /authorize only as a manual human fallback.",
        };

    private static bool TryParseDecision(string? value, out ToolApprovalDecision decision)
    {
        decision = ToolApprovalDecision.NeedHuman;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim().Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();
        return normalized switch
        {
            "approved" => Set(ToolApprovalDecision.Approved, out decision),
            "denied" => Set(ToolApprovalDecision.Denied, out decision),
            "need_human" => Set(ToolApprovalDecision.NeedHuman, out decision),
            "needhuman" => Set(ToolApprovalDecision.NeedHuman, out decision),
            _ => false,
        };
    }

    private static bool Set(ToolApprovalDecision value, out ToolApprovalDecision decision)
    {
        decision = value;
        return true;
    }

    private static ToolApprovalScope? TryParseScope(string? value)
        => Enum.TryParse<ToolApprovalScope>(value, ignoreCase: true, out var scope) ? scope : null;

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

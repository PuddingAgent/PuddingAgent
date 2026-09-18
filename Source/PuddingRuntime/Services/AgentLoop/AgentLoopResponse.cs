using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PuddingCode.Goals;

namespace PuddingRuntime.Services.AgentLoop;

/// <summary>Agent 每轮输出的结构化 JSON 响应模型。</summary>
public sealed class AgentLoopResponse
{
    /// <summary>"DONE" 表示任务完成；"CONTINUE" 表示继续迭代。</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = "CONTINUE";

    /// <summary>当前轮次的推理过程或最终答案。</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>本轮需要执行的工具调用；若无工具调用则为 null。</summary>
    [JsonPropertyName("tool")]
    public AgentLoopToolCall? Tool { get; init; }

    /// <summary>额外元信息（原因、置信度等）。</summary>
    [JsonPropertyName("meta")]
    public AgentLoopMeta? Meta { get; init; }

    /// <summary>是否已发出完成信号（status=DONE）。</summary>
    [JsonIgnore]
    public bool IsDone => Status.Equals("DONE", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否发出等待信号（status=WAIT）。</summary>
    [JsonIgnore]
    public bool IsWaiting => Status.Equals("WAIT", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否发出失败信号（status=FAILED）。</summary>
    [JsonIgnore]
    public bool IsFailed => Status.Equals("FAILED", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the provider output was successfully parsed as the Runtime control envelope.
    /// Plain-text fallback responses keep this false so delegated output-contract recovery can
    /// distinguish an omitted envelope from an explicit structured CONTINUE decision.
    /// </summary>
    [JsonIgnore]
    public bool IsStructured { get; init; }

    // ── 解析 ──────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions _parseOptions =
        new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// 从 LLM 原始输出文本解析 AgentLoopResponse。
    /// 自动提取 Markdown JSON 代码围栏，即使模型在围栏前添加了说明文字；
    /// 解析失败时安全降级。
    /// </summary>
    public static AgentLoopResponse Parse(string text)
    {
        var json = StripCodeFence(text.Trim());
        try
        {
            var result = JsonSerializer.Deserialize<AgentLoopResponse>(json, _parseOptions);
            if (result is not null)
            {
                return new AgentLoopResponse
                {
                    Status = result.Status,
                    Message = result.Message,
                    Tool = result.Tool,
                    Meta = ParseProposalMeta(result.Meta),
                    IsStructured = true,
                };
            }
        }
        catch { /* 解析失败，进入降级逻辑 */ }

        // 降级：仅当出现 status=DONE 时才判定完成，避免普通文本误触发
        var isDone = Regex.IsMatch(
            json,
            "\"status\"\\s*:\\s*\"DONE\"",
            RegexOptions.IgnoreCase);
        return new AgentLoopResponse { Status = isDone ? "DONE" : "CONTINUE", Message = text };
    }

    /// <summary>
    /// A1（G92-1 S1-c 片6）：解析 envelope hidden meta.goal_contract_proposal → typed proposal。
    /// fail-closed：解析失败（未知/额外字段、Agent 自报身份、缺必需字段、形态错误）不抛异常、
    /// 不影响 Turn 终态，仅记录拒绝原因并把 proposal 置 null；envelope 未携带时原样透传。
    /// </summary>
    private static AgentLoopMeta? ParseProposalMeta(AgentLoopMeta? meta)
    {
        if (meta is null)
            return null;

        if (meta.GoalContractProposalRaw is not { } element
            || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return meta;

        if (element.ValueKind != JsonValueKind.Object)
        {
            return WithProposalOutcome(
                meta,
                null,
                $"goal_contract_proposal must be a json object, got {element.ValueKind}");
        }

        var parseResult = GoalContractProposalParser.Parse(element.GetRawText());
        return WithProposalOutcome(meta, parseResult.Proposal, parseResult.RejectionReason);
    }

    private static AgentLoopMeta WithProposalOutcome(
        AgentLoopMeta meta,
        GoalContractProposal? proposal,
        string? rejectionReason)
        => new()
        {
            Reason = meta.Reason,
            Confidence = meta.Confidence,
            GoalContractProposal = proposal,
            GoalContractProposalRejectionReason = rejectionReason,
        };

    private static string StripCodeFence(string s)
    {
        var searchFrom = 0;
        while (searchFrom < s.Length)
        {
            var fenceStart = s.IndexOf("```", searchFrom, StringComparison.Ordinal);
            if (fenceStart < 0)
                return s;

            var headerEnd = s.IndexOf('\n', fenceStart + 3);
            if (headerEnd < 0)
                return s;

            var fenceEnd = s.IndexOf("```", headerEnd + 1, StringComparison.Ordinal);
            if (fenceEnd < 0)
                return s;

            var language = s[(fenceStart + 3)..headerEnd].Trim();
            var candidate = s[(headerEnd + 1)..fenceEnd].Trim();
            if (language.Equals("json", StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith('{')
                || candidate.StartsWith('['))
            {
                return candidate;
            }

            searchFrom = fenceEnd + 3;
        }

        return s;
    }
}

/// <summary>响应元信息——附带原因说明和置信度。</summary>
public sealed class AgentLoopMeta
{
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; init; }

    /// <summary>
    /// A1（G92-1 S1-c 片6）：Runtime 结构化 envelope 的 hidden 合同提议原文
    /// （meta.goal_contract_proposal）。仅作解析输入；消费方请读
    /// <see cref="GoalContractProposal"/>（fail-closed 解析后的 typed 值）。
    /// </summary>
    [JsonPropertyName("goal_contract_proposal")]
    public JsonElement? GoalContractProposalRaw { get; init; }

    /// <summary>
    /// fail-closed 解析后的 typed proposal；envelope 未携带或解析被拒时为 null
    /// （拒绝原因见 <see cref="GoalContractProposalRejectionReason"/>）。
    /// </summary>
    [JsonIgnore]
    public GoalContractProposal? GoalContractProposal { get; init; }

    /// <summary>proposal 解析拒绝原因；null 表示未提交或解析成功（诊断用途，绝不参与合同生成）。</summary>
    [JsonIgnore]
    public string? GoalContractProposalRejectionReason { get; init; }
}

/// <summary>Agent 在响应 JSON 中声明的工具调用信息。</summary>
public sealed class AgentLoopToolCall
{
    /// <summary>工具 Skill ID（对应 IAgentSkill.SkillId）。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>工具参数（自由 JSON 对象）。对于 shell 工具，使用 {"command": "..."} 字段。</summary>
    [JsonPropertyName("args")]
    public JsonElement? Args { get; init; }
}

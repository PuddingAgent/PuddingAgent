using System.Text.Json;
using System.Text.Json.Serialization;

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

    // ── 解析 ──────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions _parseOptions =
        new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// 从 LLM 原始输出文本解析 AgentLoopResponse。
    /// 自动剥离 Markdown 代码围栏；解析失败时安全降级。
    /// </summary>
    public static AgentLoopResponse Parse(string text)
    {
        var json = StripCodeFence(text.Trim());
        try
        {
            var result = JsonSerializer.Deserialize<AgentLoopResponse>(json, _parseOptions);
            if (result is not null) return result;
        }
        catch { /* 解析失败，进入降级逻辑 */ }

        // 降级：通过关键词判断是否已完成
        var isDone = json.Contains("\"DONE\"", StringComparison.OrdinalIgnoreCase)
                  || json.Contains("DONE", StringComparison.Ordinal);
        return new AgentLoopResponse { Status = isDone ? "DONE" : "CONTINUE", Message = text };
    }

    private static string StripCodeFence(string s)
    {
        if (!s.StartsWith("```")) return s;
        var nl = s.IndexOf('\n');
        if (nl < 0) return s;
        s = s[(nl + 1)..];
        if (s.EndsWith("```")) s = s[..^3].TrimEnd();
        return s.Trim();
    }
}

/// <summary>响应元信息——附带原因说明和置信度。</summary>
public sealed class AgentLoopMeta
{
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; init; }
}

/// <summary>Agent 在响应 JSON 中声明的工具调用信息。</summary>
public sealed class AgentLoopToolCall
{
    /// <summary>工具 Skill ID（对应 IAgentSkill.SkillId）。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>工具参数（自由 JSON 对象）。对于 bash 工具，使用 {"command": "..."} 字段。</summary>
    [JsonPropertyName("args")]
    public JsonElement? Args { get; init; }
}

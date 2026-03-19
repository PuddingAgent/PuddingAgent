using System.Text;
using System.Text.RegularExpressions;
using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingRuntime.Services.Skills;

/// <summary>
/// SkillRuntime——管理可供 Agent 调用的 Skill 套件。
/// 职责：
///   1. 按能力策略过滤可用 Skill。
///   2. 调用前经 SandboxExecutor 二次权限校验。
///   3. 为 AgentExecutionService 提供系统提示片段（工具声明）和响应中工具调用的解析/执行。
/// </summary>
public sealed partial class SkillRuntime
{
    private readonly IReadOnlyDictionary<string, IAgentSkill> _skills;
    private readonly SandboxExecutor _sandbox;
    private readonly ILogger<SkillRuntime> _logger;

    // 工具调用块格式：
    // <TOOL_CALL>
    // <tool>bash</tool>
    // <input>命令内容</input>
    // </TOOL_CALL>
    [GeneratedRegex(
        @"<TOOL_CALL>\s*<tool>(?<name>[^<]+)</tool>\s*<input>(?<input>.*?)</input>\s*</TOOL_CALL>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ToolCallRegex();

    public SkillRuntime(
        IEnumerable<IAgentSkill> skills,
        SandboxExecutor sandbox,
        ILogger<SkillRuntime> logger)
    {
        _skills  = skills.ToDictionary(s => s.SkillId, StringComparer.OrdinalIgnoreCase);
        _sandbox = sandbox;
        _logger  = logger;
    }

    /// <summary>返回满足能力策略的可用 Skill 列表。</summary>
    public IReadOnlyList<IAgentSkill> GetAvailableSkills(CapabilityPolicy? policy) =>
        _skills.Values
            .Where(s => !s.RequiresShellExecution || (policy?.AllowShellExecution == true))
            .ToList();

    /// <summary>构造传给 LLM 的 function tools 定义。</summary>
    public IReadOnlyList<LlmToolDefinition> BuildLlmTools(CapabilityPolicy? policy)
    {
        var available = GetAvailableSkills(policy);
        var tools = new List<LlmToolDefinition>(available.Count);

        foreach (var skill in available)
        {
            var parameters = BuildDefaultParameters(skill.SkillId);
            tools.Add(new LlmToolDefinition
            {
                Name = skill.SkillId,
                Description = skill.Description,
                Parameters = parameters,
            });
        }

        return tools;
    }

    /// <summary>
    /// 按 ID 调用 Skill，先经能力策略 + SandboxExecutor 双重校验。
    /// </summary>
    public async Task<SkillResult> InvokeAsync(
        string skillId,
        SkillInvokeRequest request,
        CapabilityPolicy? policy,
        CancellationToken ct = default)
    {
        if (!_skills.TryGetValue(skillId, out var skill))
            return Fail($"Skill '{skillId}' not found.");

        if (!_sandbox.IsAllowed(skillId, policy, request.AgentInstanceId))
        {
            _logger.LogWarning("[SkillRuntime] Skill={Skill} blocked for agent={Agent}",
                skillId, request.AgentInstanceId);
            return Fail($"Skill '{skillId}' is not allowed by the agent's capability policy.");
        }

        _logger.LogInformation("[SkillRuntime] Invoke skill={Skill} agent={Agent}",
            skillId, request.AgentInstanceId);
        return await skill.ExecuteAsync(request, ct);
    }

    /// <summary>
    /// 从 LLM 响应文本中解析工具调用块列表。
    /// 返回 (toolName, input) 元组列表；列表为空表示没有工具调用。
    /// </summary>
    public static IReadOnlyList<(string ToolName, string Input)> ParseToolCalls(string text)
    {
        var matches = ToolCallRegex().Matches(text);
        if (matches.Count == 0) return [];

        return matches
            .Select(m => (m.Groups["name"].Value.Trim(), m.Groups["input"].Value.Trim()))
            .ToList();
    }

    /// <summary>
    /// 生成注入到系统提示中的 Agent Loop 运行说明：
    ///   - 要求 LLM 严格输出 JSON 格式（status / message / tool）
    ///   - 说明 DONE / CONTINUE 停止信号语义
    ///   - 若有可用工具，附加工具列表与调用示例
    /// </summary>
    public string BuildLoopInstructions(CapabilityPolicy? policy)
    {
        var available = GetAvailableSkills(policy);
        var sb = new StringBuilder();

        sb.AppendLine("\n\n---");
        sb.AppendLine("## Output Format (STRICT)");
        sb.AppendLine("You MUST output ONLY valid JSON. Do NOT output markdown, prose, or any text outside the JSON object.");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine("{");
        sb.AppendLine("  \"status\": \"CONTINUE | DONE | WAIT | FAILED\",");
        sb.AppendLine("  \"message\": \"your reasoning or final answer\",");
        sb.AppendLine("  \"tool\": {");
        sb.AppendLine("    \"name\": \"tool_id or null\",");
        sb.AppendLine("    \"args\": {}");
        sb.AppendLine("  },");
        sb.AppendLine("  \"meta\": { \"reason\": \"optional explanation\" }");
        sb.AppendLine("}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("1. Task not yet complete → `status = \"CONTINUE\"`, optionally set `tool`.");
        sb.AppendLine("2. Task is complete → `status = \"DONE\"`, set `tool` to `null`.");
        sb.AppendLine("3. Must wait for external event or approval → `status = \"WAIT\"`, explain in `meta.reason`.");
        sb.AppendLine("4. Cannot proceed (unrecoverable error) → `status = \"FAILED\"`, explain in `meta.reason`.");
        sb.AppendLine("5. Output `DONE` ONLY when you are certain everything is finished.");
        sb.AppendLine("6. NEVER output anything outside the JSON object.");

        if (available.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Available Tools");
            foreach (var s in available)
                sb.AppendLine($"- **{s.Name}** (id: `{s.SkillId}`): {s.Description}");

            sb.AppendLine();
            sb.AppendLine("To call a tool, set `tool.name` to the tool id and `tool.args` to the arguments.");
            sb.AppendLine("Example — run a bash command:");
            sb.AppendLine("```json");
            sb.AppendLine("{ \"status\": \"CONTINUE\", \"message\": \"Listing files\", \"tool\": { \"name\": \"bash\", \"args\": { \"command\": \"ls -la\" } } }");
            sb.AppendLine("```");
        }
        else
        {
            sb.AppendLine("5. No tools are available in this context. Set `tool` to `null` in every response.");
        }

        return sb.ToString();
    }

    // ── 辅助 ─────────────────────────────────────────────────────────────

    private static SkillResult Fail(string error) =>
        new() { Success = false, Output = string.Empty, Error = error };

    private static ToolParameterSchema BuildDefaultParameters(string skillId)
    {
        if (skillId.Equals("bash", StringComparison.OrdinalIgnoreCase))
            return new ToolParameterSchema(
                [new ToolParameter("command", "string", "Shell command to execute")],
                ["command"]);

        if (skillId.Equals("python", StringComparison.OrdinalIgnoreCase))
            return new ToolParameterSchema(
                [new ToolParameter("code", "string", "Python 3 code to execute")],
                ["code"]);

        if (skillId.Equals("read_file", StringComparison.OrdinalIgnoreCase))
            return new ToolParameterSchema(
                [new ToolParameter("path", "string", "Absolute or relative file path inside the container")],
                ["path"]);

        if (skillId.Equals("write_file", StringComparison.OrdinalIgnoreCase))
            return new ToolParameterSchema(
                [
                    new ToolParameter("path",    "string", "Absolute or relative file path inside the container"),
                    new ToolParameter("content", "string", "Text content to write to the file"),
                ],
                ["path", "content"]);

        if (skillId.Equals("http_fetch", StringComparison.OrdinalIgnoreCase))
            return new ToolParameterSchema(
                [
                    new ToolParameter("url",          "string", "The full HTTP/HTTPS URL to request"),
                    new ToolParameter("method",       "string", "HTTP method: GET or POST (default: GET)"),
                    new ToolParameter("body",         "string", "Request body for POST requests (optional)"),
                    new ToolParameter("content_type", "string", "Content-Type header for POST (default: application/json)"),
                ],
                ["url"]);

        return new ToolParameterSchema(
            [new ToolParameter("input", "string", "Tool input payload")],
            ["input"]);
    }
}

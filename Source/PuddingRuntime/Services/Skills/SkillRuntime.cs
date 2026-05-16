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

    /// <summary>
    /// 返回满足能力策略的可用 Skill 列表。
    /// V2 两级权限：
    ///   · Low (auto)     — 始终可用，不依赖 policy
    ///   · Medium         — 需在 policy.DefaultToolNames 中（默认授权）
    ///   · High           — 需在 policy.RequiresGrantToolNames 中（显式授权）
    /// V1 兼容：AllowedToolNames 不为空时作为全局白名单。
    /// </summary>
    public IReadOnlyList<IAgentSkill> GetAvailableSkills(CapabilityPolicy? policy, AgentTemplateDefinition? template = null)
    {
        var skills = _skills.Values;

        // Template 级别的 SkillId 白名单（最高优先级）
        if (template?.AllowedSkillIds?.Count > 0)
            skills = skills.Where(s => template.AllowedSkillIds.Contains(s.SkillId, StringComparer.OrdinalIgnoreCase));

        // Low 权限工具始终可用（子代理、只读等）
        var lowSkills = skills.Where(s => s.PermissionLevel == ToolPermissionLevel.Low).ToList();

        // V1 兼容：AllowedToolNames 为全局白名单 — 但 Low 权限绕过
        if (policy?.AllowedToolNames.Count > 0)
        {
            var whiteSet = new HashSet<string>(policy.AllowedToolNames, StringComparer.OrdinalIgnoreCase);
            var filtered = skills.Where(s =>
                whiteSet.Contains(s.SkillId) || s.PermissionLevel == ToolPermissionLevel.Low);
            return filtered.ToList();
        }

        // V2 两级权限：根据 PermissionLevel 分级
        var effectiveSet = policy?.GetAllEffectiveToolNames()
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        skills = skills.Where(s =>
        {
            // Low: 始终可用
            if (s.PermissionLevel == ToolPermissionLevel.Low)
                return true;

            // High: 需要在 RequiresGrantToolNames 中
            if (s.PermissionLevel == ToolPermissionLevel.High)
                return policy?.RequiresGrantToolNames.Contains(s.SkillId, StringComparer.OrdinalIgnoreCase) == true;

            // Medium (default): 需要在 effective set 中
            return effectiveSet.Contains(s.SkillId);
        });

        return skills.ToList();
    }

    /// <summary>按 SkillId 获取 Skill 实例，不存在返回 null。</summary>
    public IAgentSkill? TryGetSkill(string skillId) =>
        _skills.TryGetValue(skillId, out var skill) ? skill : null;

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

        if (skillId.Equals("search_memory", StringComparison.OrdinalIgnoreCase))
            return new ToolParameterSchema(
                [
                    new ToolParameter("query", "string", "Search keywords or question for memory retrieval"),
                    new ToolParameter("book", "string", "Optional book hint such as 用户档案 or 用户偏好"),
                    new ToolParameter("workspaceId", "string", "Optional workspace id for deeper memory exploration"),
                ],
                ["query"]);

        if (skillId.Equals("save_memory", StringComparison.OrdinalIgnoreCase))
            return new ToolParameterSchema(
                [
                    new ToolParameter("action", "string", "操作类型：upsert（写入/更新）或 delete（删除）"),
                    new ToolParameter("type", "string", "内容类型：fact（事实）、preference（偏好）、summary（摘要）、chapter（章节内容）"),
                    new ToolParameter("book", "string", "目标 Book 名称（可选，默认自动匹配）"),
                    new ToolParameter("content", "string", "正文内容（fact/summary/chapter 需要）"),
                    new ToolParameter("key", "string", "偏好键名（preference 类型需要）"),
                    new ToolParameter("value", "string", "偏好值（preference 类型需要）"),
                    new ToolParameter("title", "string", "章节标题（chapter 类型需要）"),
                    new ToolParameter("source_ref", "string", "溯源引用：session_id 或外部 URL（可选）"),
                    new ToolParameter("source_label", "string", "溯源标签，如 '原始会话'、'参考文档'（可选）"),
                    new ToolParameter("source_reference", "string", "引用来源：内部会话文件路径如 data/logs/sessions/2026-05-13/xxx.md，或外部URL"),
                    new ToolParameter("reference_type", "string", "引用类型：internal（内部）/ external（外部）/ none（无）"),
                ],
                ["action", "type"]);

        if (skillId.Equals("manage_memory", StringComparison.OrdinalIgnoreCase))
            return new ToolParameterSchema(
                [
                    new ToolParameter("action", "string", "操作：list_books, create_book, list_chapters, add_chapter, update_chapter, delete_book, add_pointer, list_pointers"),
                    new ToolParameter("book_id", "string", "目标 BookId（操作特定 Book/Chapter 时需要）"),
                    new ToolParameter("library_id", "string", "Library ID（可选，默认自动查找）"),
                    new ToolParameter("title", "string", "Book/Chapter 标题"),
                    new ToolParameter("content", "string", "Chapter 正文内容"),
                    new ToolParameter("summary", "string", "Book 摘要"),
                    new ToolParameter("chapter_id", "string", "目标 ChapterId"),
                    new ToolParameter("tags", "string", "逗号分隔的标签（创建 Book 时）"),
                    new ToolParameter("chapter_order", "number", "章节排序序号"),
                    new ToolParameter("source_reference", "string", "引用来源：内部会话文件路径或外部URL"),
                    new ToolParameter("reference_type", "string", "引用类型：internal（内部）/ external（外部）/ none（无）"),
                ],
                ["action"]);

        if (skillId.Equals("grep_memory", StringComparison.OrdinalIgnoreCase))
            return new ToolParameterSchema(
                [
                    new ToolParameter("action", "string", "操作：search（全文检索）、in_book（Book内检索）、list_books（列出Books）、toc（目录/章节列表）"),
                    new ToolParameter("query", "string", "搜索查询（search/in_book 需要）"),
                    new ToolParameter("mode", "string", "搜索模式：fts5（默认，基于全文索引）或 regex（正则匹配）"),
                    new ToolParameter("book", "string", "限定 Book 名称（in_book 需要）"),
                    new ToolParameter("top_k", "number", "返回条目数上限，默认 10"),
                ],
                ["action"]);

        if (skillId.Equals("event_subscribe", StringComparison.OrdinalIgnoreCase))
            return new ToolParameterSchema(
                [
                    new ToolParameter("operation", "string", "操作类型: subscribe / unsubscribe / list"),
                    new ToolParameter("event_type_patterns", "string", "事件类型模式，逗号分隔，支持通配符如 mqtt.sensor.*, cron.*"),
                    new ToolParameter("subscription_id", "string", "取消订阅时需要提供的订阅ID"),
                    new ToolParameter("filter_expression", "string", "可选的过滤表达式，如 priority>=5"),
                ],
                ["operation"]);

        if (skillId.Equals("search_files", StringComparison.OrdinalIgnoreCase))
            return new ToolParameterSchema(
                [
                    new ToolParameter("pattern", "string", "Glob pattern like *.cs or **/*.json. Ends with / to list directory."),
                    new ToolParameter("recursive", "string", "Search recursively: true/false"),
                    new ToolParameter("directory", "string", "Root directory to search in"),
                    new ToolParameter("max_results", "string", "Maximum number of results to return"),
                ],
                ["pattern"]);

        if (skillId.Equals("search_codebase", StringComparison.OrdinalIgnoreCase))
            return new ToolParameterSchema(
                [
                    new ToolParameter("query", "string", "Text or regex to search for in files"),
                    new ToolParameter("pattern", "string", "File glob pattern to filter files. Default: *.cs;*.ts;*.vue;*.py;*.js;*.json;*.md;*.yaml;*.yml;*.sql"),
                    new ToolParameter("case_sensitive", "string", "Case sensitive search: true/false"),
                    new ToolParameter("max_results", "string", "Maximum matching lines to return"),
                ],
                ["query"]);

        if (skillId.Equals("manage_tasks", StringComparison.OrdinalIgnoreCase))
            return new ToolParameterSchema(
                [
                    new ToolParameter("operation", "string", "Operation: create / update_status / list / delete"),
                    new ToolParameter("task_id", "string", "Task ID (required for update/delete)"),
                    new ToolParameter("title", "string", "Task title (required for create)"),
                    new ToolParameter("status", "string", "Task status: pending / in-progress / completed"),
                ],
                ["operation"]);

        return new ToolParameterSchema(
            [new ToolParameter("input", "string", "Tool input payload")],
            ["input"]);
    }
}

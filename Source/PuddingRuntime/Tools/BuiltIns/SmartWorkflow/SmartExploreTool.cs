using System.Text;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

[Tool(
    id: "smart_explore",
    name: "Smart Explore",
    description: "智能探索工具（统一入口）。合并了原 smart_search（代码搜索）和 smart_query_session_log（会话日志查询）的功能。" +
                 "用自然语言描述探索目标，内部委托 Explorer 子代理自动执行多轮探索。" +
                 "支持三种场景：\n" +
                 "① 代码/文件探索 — file_search + search_grep + file_read + code_outline\n" +
                 "② 会话日志查询 — query_session_logs（grep/messages/list_days，已自动过滤心跳噪音）\n" +
                 "③ 通用文件系统浏览 — list_dir + file_search + project_map\n" +
                 "参数：task（探索任务，自然语言描述）、scope（可选，目录/会话范围）、" +
                 "session_id（可选，目标会话ID）、focus（可选，重点关注哪些方面）、" +
                 "max_results（可选，默认 15）、timeout_seconds（可选，默认 600s）。" +
                 "模型由 Agent 配置的 Explorer_Model 决定。",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SubAgentExposure = SubAgentExposure.MainAgentOnly)]
public sealed class SmartExploreTool : SmartWorkflowToolBase<SmartExploreArgs>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SmartExploreTool> _logger;

    public SmartExploreTool(IServiceProvider serviceProvider, ILogger<SmartExploreTool> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override string RoleName => "explorer";
    protected override int DefaultTimeoutSeconds => 600;
    protected override int DefaultMaxRounds => 150;

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        SmartExploreArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Task?.Trim()))
            return ToolExecutionResult.Fail("task is required. Describe what you want to explore in natural language.");

        _logger.LogInformation("[SmartExplore] agent={Agent} task={Task} scope={Scope}",
            context.AgentInstanceId, args.Task, args.Scope ?? "(root)");

        return await RunSubAgentAsync(args, context, _serviceProvider, _logger, ct, args.TimeoutSeconds);
    }

    protected override string BuildTaskPrompt(SmartExploreArgs args, ToolExecutionContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🔍 EXPLORER — Find information. Do NOT converse. Do NOT stop early.");
        sb.AppendLine();
        sb.AppendLine("### CRITICAL");
        sb.AppendLine("- Complete the exploration and return a self-contained evidence package. Never output progress as the final answer.");
        sb.AppendLine("- The parent Agent MUST be able to answer the user from your result without repeating file_search, file_read, code_outline, or search_grep.");
        sb.AppendLine("- A discovered file name is not a finding. Inspect the relevant content and explain what the artifact, class, or function actually does.");
        sb.AppendLine();
        sb.AppendLine("### SCENARIO — Choose based on query:");
        sb.AppendLine("**A) CODE/FILE** → file_search + search_grep → code_outline → code_symbol_search → file_read top hits.");
        sb.AppendLine("   ⚡ Run file_search + search_grep in PARALLEL when keywords are independent.");
        sb.AppendLine("   file_search is discovery only. Validate every reported code hit with code_outline and/or file_read before including it.");
        sb.AppendLine("**B) SESSION LOGS** → query_session_logs grep (set exclude_heartbeat=true) → list_days if no session → messages (paginate).");
        sb.AppendLine("   ⚡ Run grep + list_days in PARALLEL for session discovery.");
        sb.AppendLine("**C) FILESYSTEM** → list_dir + project_map → file_read key files.");
        sb.AppendLine();
        sb.AppendLine("### ⚠️ RULES");
        sb.AppendLine("- Allowed: list_dir, file_search, search_grep, file_read, code_outline, code_symbol_search, code_explore, project_map, query_session_logs (grep/messages/list_days/list_sessions), smart_query_session_log.");
        sb.AppendLine("- NEVER: file_write, file_patch, shell, spawn_sub_agent.");
        sb.AppendLine("- If a tool fails: try alternative tool, then report the gap.");
        sb.AppendLine("- For code/files, every reported path MUST be the normalized absolute path returned by file_search or resolved from the inspected artifact.");
        sb.AppendLine("- Include exact symbols and line ranges when available. Never make the parent Agent rediscover where a class or function lives.");
        sb.AppendLine("- Explain responsibility, purpose, relevance to the task, and important callers/callees or data flow. Do not return a bare inventory.");
        sb.AppendLine("- Prefer a smaller set of verified, high-value findings over a long unverified file list.");
        sb.AppendLine();
        sb.AppendLine($"## 🎯 Task: {args.Task}");
        if (!string.IsNullOrWhiteSpace(args.Scope))
            sb.AppendLine($"## 📁 Scope: {args.Scope}");
        if (!string.IsNullOrWhiteSpace(args.SessionId))
            sb.AppendLine($"## 📋 Session: {args.SessionId}");
        if (!string.IsNullOrWhiteSpace(args.Focus))
            sb.AppendLine($"## 🔬 Focus: {args.Focus}");
        sb.AppendLine($"## 🔢 Maximum verified artifacts: {Math.Clamp(args.MaxResults ?? 15, 1, 30)}");
        sb.AppendLine();
        AppendCanonicalReportRules(sb);
        sb.AppendLine("## OUTPUT CONTRACT (use exactly this structure):");
        sb.AppendLine("```");
        sb.AppendLine("SUMMARY:");
        sb.AppendLine("  STATUS: found|partial|not_found");
        sb.AppendLine("  DIRECT_ANSWER: concise answer to the exploration task");
        sb.AppendLine("CHANGES:");
        sb.AppendLine("  none — read-only exploration; state the inspected scope");
        sb.AppendLine("EVIDENCE:");
        sb.AppendLine("  VERIFIED_ARTIFACTS:");
        sb.AppendLine("    - PATH_OR_SESSION: absolute file path, or session id plus timestamp for session-log evidence");
        sb.AppendLine("      SYMBOLS: class/function/member names, or n/a");
        sb.AppendLine("      LINES: exact line or range, or n/a");
        sb.AppendLine("      KIND: source|config|document|session_log|directory");
        sb.AppendLine("      RESPONSIBILITY: what this artifact or symbol does");
        sb.AppendLine("      RELEVANCE: why it answers the requested task");
        sb.AppendLine("      RELATIONSHIPS: important callers, callees, dependencies, data flow, or n/a");
        sb.AppendLine("      FACT: concrete fact verified from the content");
        sb.AppendLine("  FINDINGS:");
        sb.AppendLine("    - synthesized conclusion with PATH_OR_SESSION and SYMBOLS references");
        sb.AppendLine("  FLOW: end-to-end call/data flow when relevant, otherwise n/a");
        sb.AppendLine("RISKS:");
        sb.AppendLine("  GAPS: what was not verified, why, and the exact next query if further exploration is needed");
        sb.AppendLine("BLOCKERS:");
        sb.AppendLine("  none, or the exact tool/data/access blocker");
        sb.AppendLine("```");
        sb.AppendLine("START NOW. No preamble.");
        return sb.ToString();
    }
}

public sealed class SmartExploreArgs : ScopedSmartWorkflowArgs
{
    [ToolParam("重点关注哪些方面")]
    public string? Focus { get; set; }

    [ToolParam("最多返回多少结果，1-30，默认 15")]
    public int? MaxResults { get; set; }

    [ToolParam("目标会话 ID（可选），用于查询特定会话日志")]
    public string? SessionId { get; set; }

}

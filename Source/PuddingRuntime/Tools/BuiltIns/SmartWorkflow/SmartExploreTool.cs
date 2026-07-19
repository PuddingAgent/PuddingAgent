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
                 "max_results（可选，默认 15）、timeout_seconds（可选，默认 120s）。" +
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
        sb.AppendLine("### CRITICAL: You MUST complete exploration AND output final results. Never output progress as final answer.");
        sb.AppendLine();
        sb.AppendLine("### SCENARIO — Choose based on query:");
        sb.AppendLine("**A) CODE/FILE** → file_search + search_grep → code_outline → code_symbol_search → file_read top hits.");
        sb.AppendLine("   ⚡ Run file_search + search_grep in PARALLEL when keywords are independent.");
        sb.AppendLine("**B) SESSION LOGS** → query_session_logs grep (set exclude_heartbeat=true) → list_days if no session → messages (paginate).");
        sb.AppendLine("   ⚡ Run grep + list_days in PARALLEL for session discovery.");
        sb.AppendLine("**C) FILESYSTEM** → list_dir + project_map → file_read key files.");
        sb.AppendLine();
        sb.AppendLine("### ⚠️ RULES");
        sb.AppendLine("- Allowed: list_dir, file_search, search_grep, file_read, code_outline, code_symbol_search, code_explore, project_map, query_session_logs (grep/messages/list_days/list_sessions), smart_query_session_log.");
        sb.AppendLine("- NEVER: file_write, file_patch, shell, spawn_sub_agent.");
        sb.AppendLine("- If a tool fails: try alternative tool, then report the gap.");
        sb.AppendLine();
        sb.AppendLine($"## 🎯 Task: {args.Task}");
        if (!string.IsNullOrWhiteSpace(args.Scope))
            sb.AppendLine($"## 📁 Scope: {args.Scope}");
        if (!string.IsNullOrWhiteSpace(args.SessionId))
            sb.AppendLine($"## 📋 Session: {args.SessionId}");
        if (!string.IsNullOrWhiteSpace(args.Focus))
            sb.AppendLine($"## 🔬 Focus: {args.Focus}");
        sb.AppendLine();
        sb.AppendLine("## OUTPUT (use exactly this format):");
        sb.AppendLine("```");
        sb.AppendLine("FOUND: yes|partial|no");
        sb.AppendLine("FILES/SESSIONS:");
        sb.AppendLine("  - path:line — description");
        sb.AppendLine("FINDINGS:");
        sb.AppendLine("  - finding (source:line)");
        sb.AppendLine("SUMMARY: 2-3 sentences");
        sb.AppendLine("MISSING: what was NOT found");
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

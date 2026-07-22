using System.Text;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

[Tool(
    id: "smart_plan",
    name: "Smart Plan",
    description: "智能任务规划。用自然语言描述目标，内部委托 Planner 子代理分解为可执行的" +
                 "结构化任务计划，包含步骤、依赖、预估工作量。" +
                 "⚠️ task 必须 ≥500 字，需包含：任务背景、项目地址、相关文件路径、项目约束等。" +
                 "上下文越充足，规划质量越高。参数：task（规划任务）、scope（范围）、" +
                 "context（可选，已有的上下文/约束）、timeout_seconds（可选）。" +
                 "模型由 Agent 配置的 Planner_Model 决定。",
    category: ToolCategory.Orchestration,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SubAgentExposure = SubAgentExposure.MainAgentOnly)]
public sealed class SmartPlanTool : SmartWorkflowToolBase<SmartPlanArgs>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SmartPlanTool> _logger;

    public SmartPlanTool(IServiceProvider serviceProvider, ILogger<SmartPlanTool> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override string RoleName => "planner";
    protected override int DefaultTimeoutSeconds => 120 * 60;
    protected override int DefaultMaxRounds => 96;

    /// <summary>Planner is read-only. The descriptor and runtime capability set must agree.</summary>
    protected override string? AllowedTools =>
        "file_read,file_search,code_outline,search_grep,list_dir,project_map," +
        "code_explore,code_summary,code_symbol_search,code_callers,code_callees," +
        "code_impact,query_session_logs,query_sessions,grep_memory,search_memory,agent_status";

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        SmartPlanArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Task?.Trim()))
            return ToolExecutionResult.Fail("task is required. Describe the goal you want to plan.");

        if ((args.Task?.Length ?? 0) < 500)
            return ToolExecutionResult.Fail(
                "task 描述不足（当前 " + (args.Task?.Length ?? 0) + " 字，最少 500 字）。" +
                "请补充任务细节：\n" +
                "  - 📖 任务背景：为什么要做这个任务？\n" +
                "  - 📁 项目地址/工作区：涉及的代码仓库路径\n" +
                "  - 📄 相关文件路径：需要改动的文件或模块\n" +
                "  - ⚙️ 项目约束：技术栈、框架、现有架构、兼容性要求\n" +
                "  - 🔗 已有发现/上下文：前期调研结果、已知根因、已确认的事实");

        _logger.LogInformation("[SmartPlan] agent={Agent} task={Task}", context.AgentInstanceId, args.Task);

        return await RunSubAgentAsync(args, context, _serviceProvider, _logger, ct, args.TimeoutSeconds);
    }

    protected override string BuildTaskPrompt(SmartPlanArgs args, ToolExecutionContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 📋 PLANNER (K3) — Produce a comprehensive, evidence-backed engineering design plan.");
        sb.AppendLine();
        sb.AppendLine("You are a software architect operating in a strictly read-only planning role.");
        sb.AppendLine("Your output must be a **Software Design Specification + ADR** that an engineer can execute without asking a single question.");
        sb.AppendLine();
        sb.AppendLine("### PROCESS");
        sb.AppendLine("1. Read `code_map.md` to understand the project structure.");
        sb.AppendLine("2. Use `search_memory` to retrieve relevant historical context or prior plans.");
        sb.AppendLine("3. Explore the codebase thoroughly — read files, search for patterns, analyze architecture.");
        sb.AppendLine("   Use the available read-only search and code-intelligence tools directly.");
        sb.AppendLine("   Do NOT guess. Verify claims with evidence from the actual code.");
        sb.AppendLine("4. Apply ADR format for every major decision: Context → Decision → Rationale → Alternatives Rejected.");
        sb.AppendLine("5. Break work into SEQUENTIAL + PARALLEL tasks with exact file paths, class names, and method signatures.");
        sb.AppendLine("6. Identify risks with probability (low/medium/high) and concrete mitigation.");
        sb.AppendLine("7. Return the complete plan directly in the five-section report below.");
        sb.AppendLine();
        sb.AppendLine("### ⚠️ RULES");
        sb.AppendLine("- Read-only means no file writes, patches, shell commands, terminal sessions, or sub-agent delegation.");
        sb.AppendLine("- Finish within the bounded round and timeout budget; prioritize verified high-value evidence.");
        sb.AppendLine("- Do NOT call any Smart workflow tool recursively.");
        sb.AppendLine("- The task description above already contains background and constraints from the caller — trust it as a starting point, but verify critical claims.");
        sb.AppendLine();
        sb.AppendLine($"## 🎯 Task: {args.Task}");
        if (!string.IsNullOrWhiteSpace(args.Scope))
            sb.AppendLine($"## 📁 Scope: {args.Scope}");
        if (!string.IsNullOrWhiteSpace(args.Context))
            sb.AppendLine($"## 📋 Context: {args.Context}");
        sb.AppendLine();
        AppendCanonicalReportRules(sb);
        sb.AppendLine("## OUTPUT CONTRACT (return as plain text):");
        sb.AppendLine();
        sb.AppendLine("SUMMARY: <goals, scope, success criteria, effort estimate — 3-5 lines>");
        sb.AppendLine("CHANGES:");
        sb.AppendLine("  ARCHITECTURE_DECISIONS: <Context, Decision, Rationale, Alternatives Rejected>");
        sb.AppendLine("  DETAILED_TASKS: <number each task with exact files/actions/dependencies/deliverables/verify>");
        sb.AppendLine("EVIDENCE: <confirmed constraints, affected file paths and symbols, verification plan>");
        sb.AppendLine("RISKS: <each: description, probability, impact, mitigation>");
        sb.AppendLine("BLOCKERS: <assumptions to resolve before implementation, or \"none\">");
        sb.AppendLine();
        sb.AppendLine("Keep each section concise. Return as plain text, not JSON.");
        return sb.ToString();
    }
}

public sealed class SmartPlanArgs : ScopedSmartWorkflowArgs
{
    [ToolParam("已有上下文或约束条件")]
    public string? Context { get; set; }
}

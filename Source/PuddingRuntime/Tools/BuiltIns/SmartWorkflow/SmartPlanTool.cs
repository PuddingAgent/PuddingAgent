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
    protected override int DefaultTimeoutSeconds => 600;
    protected override int DefaultMaxRounds => 150;

    /// <summary>Planner 只暴露只读工具，禁止代码探索和写操作。</summary>
    protected override string? AllowedTools => "file_read,search_grep,list_dir,project_map,search_memory,query_session_logs";

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
        sb.AppendLine("## 📋 PLANNER — Produce a detailed engineering design plan.");
        sb.AppendLine();
        sb.AppendLine("Your output should resemble a **Software Design Specification + ADR (Architecture Decision Record)**.");
        sb.AppendLine("Do NOT produce a one-line-per-task skeleton. Produce a plan an engineer can execute without asking questions.");
        sb.AppendLine();
        sb.AppendLine("### PROCESS");
        sb.AppendLine("1. Restate the problem and constraints to confirm understanding.");
        sb.AppendLine("2. Use search_memory to check for relevant historical plans or known context.");
        sb.AppendLine("3. For each major decision point, apply ADR format: Context → Decision → Rationale → Alternatives Rejected.");
        sb.AppendLine("4. Break into SEQUENTIAL (depends) + PARALLEL (independent) tasks with precise deliverables.");
        sb.AppendLine("5. For each task: what files are touched, what the change is, how to verify success.");
        sb.AppendLine("6. Identify risks with probability/impact, not just a list.");
        sb.AppendLine();
        sb.AppendLine("### ⚠️ CONSTRAINTS");
        sb.AppendLine("- The task description already contains background, file paths, and constraints gathered by the caller. Trust it.");
        sb.AppendLine("- Use search_memory first. Use file tools only to verify specific claims in the task.");
        sb.AppendLine("- If you believe critical context is missing, note it in RISKS — do NOT go exploring yourself.");
        sb.AppendLine();
        sb.AppendLine($"## 🎯 Task: {args.Task}");
        if (!string.IsNullOrWhiteSpace(args.Scope))
            sb.AppendLine($"## 📁 Scope: {args.Scope}");
        if (!string.IsNullOrWhiteSpace(args.Context))
            sb.AppendLine($"## 📋 Context: {args.Context}");
        sb.AppendLine();
        AppendCanonicalReportRules(sb);
        sb.AppendLine("## OUTPUT CONTRACT (implementation-ready specification):");
        sb.AppendLine();
        sb.AppendLine("SUMMARY:");
        sb.AppendLine("  PROBLEM_STATEMENT: what is being solved and why now");
        sb.AppendLine("  GOALS_AND_NON_GOALS: explicit scope boundaries");
        sb.AppendLine("  SUCCESS_CRITERIA: observable acceptance outcomes");
        sb.AppendLine("  ESTIMATED_TOTAL: effort and critical path");
        sb.AppendLine("CHANGES:");
        sb.AppendLine("  ARCHITECTURE_DECISIONS:");
        sb.AppendLine("    - DECISION: what is chosen");
        sb.AppendLine("      CONTEXT: constraints and forces");
        sb.AppendLine("      RATIONALE: why this option");
        sb.AppendLine("      ALTERNATIVES_REJECTED: alternatives and concrete rejection reasons");
        sb.AppendLine("  DETAILED_TASKS:");
        sb.AppendLine("    - ID: T1");
        sb.AppendLine("      NAME: executable task");
        sb.AppendLine("      ACTIONS: ADD|MODIFY|DELETE|MOVE with exact files, classes, methods, fields, or schemas");
        sb.AppendLine("      LOGIC: expected behavior and component responsibility after the change");
        sb.AppendLine("      DEPENDENCIES: none or task IDs");
        sb.AppendLine("      DELIVERABLES: concrete code/document/config artifacts");
        sb.AppendLine("      VERIFY: exact build, test, API, or UI acceptance checks");
        sb.AppendLine("  EXECUTION_ORDER:");
        sb.AppendLine("    DEPENDENCY_GRAPH: text diagram");
        sb.AppendLine("    PARALLEL_GROUPS: [[T1,T2],[T3]]");
        sb.AppendLine("EVIDENCE:");
        sb.AppendLine("  CONFIRMED_CONSTRAINTS: facts from the supplied task, memory, or specifically verified files");
        sb.AppendLine("  AFFECTED_COMPONENTS: exact paths and symbols when provided or verified");
        sb.AppendLine("  VERIFICATION_PLAN: integration scenarios, edge cases, diagnostics, and rollback proof");
        sb.AppendLine("RISKS:");
        sb.AppendLine("  - RISK: description");
        sb.AppendLine("    PROBABILITY: low|medium|high");
        sb.AppendLine("    IMPACT: low|medium|high");
        sb.AppendLine("    MITIGATION: preventive and recovery actions");
        sb.AppendLine("BLOCKERS:");
        sb.AppendLine("  ASSUMPTIONS_AND_MISSING_INPUTS: none, or items that must be resolved before implementation");
        return sb.ToString();
    }
}

public sealed class SmartPlanArgs : ScopedSmartWorkflowArgs
{
    [ToolParam("已有上下文或约束条件")]
    public string? Context { get; set; }
}

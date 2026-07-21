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
    protected override int DefaultMaxRounds => 150;
    protected override bool AllowNestedSmartDelegation => true;

    /// <summary>K3 Planner 拥有全量工具（读+写+终端），用于深度探索和直接产出报告文件。</summary>
    protected override string? AllowedTools => null;

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
        sb.AppendLine("## 📋 PLANNER (K3) — Produce a comprehensive engineering design plan and WRITE IT TO FILE.");
        sb.AppendLine();
        sb.AppendLine("You are a world-class software architect. You have FULL ACCESS to the codebase — read, search, explore, and WRITE.");
        sb.AppendLine("Your output must be a **Software Design Specification + ADR** that an engineer can execute without asking a single question.");
        sb.AppendLine();
        sb.AppendLine("### 🔑 CRITICAL: Write the final report to file");
        sb.AppendLine("- After you finish research and reasoning, use `file_write` to save the COMPLETE plan to `memory/plans/{topic-slug}.md`.");
        sb.AppendLine("- The written file must contain ALL five sections (SUMMARY/CHANGES/EVIDENCE/RISKS/BLOCKERS) in full detail.");
        sb.AppendLine("- Then return a brief summary in your reply pointing to the file.");
        sb.AppendLine();
        sb.AppendLine("### PROCESS");
        sb.AppendLine("1. Read `code_map.md` to understand the project structure.");
        sb.AppendLine("2. Use `search_memory` to retrieve relevant historical context or prior plans.");
        sb.AppendLine("3. Explore the codebase thoroughly — read files, search for patterns, analyze architecture.");
        sb.AppendLine("   You have `file_read`, `search_grep`, `list_dir`, `project_map`, `smart_explore` — USE THEM.");
        sb.AppendLine("   Do NOT guess. Verify claims with evidence from the actual code.");
        sb.AppendLine("4. Apply ADR format for every major decision: Context → Decision → Rationale → Alternatives Rejected.");
        sb.AppendLine("5. Break work into SEQUENTIAL + PARALLEL tasks with exact file paths, class names, and method signatures.");
        sb.AppendLine("6. Identify risks with probability (low/medium/high) and concrete mitigation.");
        sb.AppendLine("7. Write the complete plan to `memory/plans/` using `file_write`.");
        sb.AppendLine();
        sb.AppendLine("### ⚠️ RULES");
        sb.AppendLine("- You have FULL tools: read, write, search, terminal. Use them to gather evidence.");
        sb.AppendLine("- Do NOT rush. Take the time you need (you have up to 150 rounds).");
        sb.AppendLine("- Do NOT call smart_plan (recursive planning is forbidden).");
        sb.AppendLine("- The task description above already contains background and constraints from the caller — trust it as a starting point, but verify critical claims.");
        sb.AppendLine();
        sb.AppendLine($"## 🎯 Task: {args.Task}");
        if (!string.IsNullOrWhiteSpace(args.Scope))
            sb.AppendLine($"## 📁 Scope: {args.Scope}");
        if (!string.IsNullOrWhiteSpace(args.Context))
            sb.AppendLine($"## 📋 Context: {args.Context}");
        sb.AppendLine();
        AppendCanonicalReportRules(sb);
        sb.AppendLine("## OUTPUT CONTRACT (five-section specification — MUST be in written file):");
        sb.AppendLine();
        sb.AppendLine("SUMMARY:");
        sb.AppendLine("  PROBLEM_STATEMENT: what is being solved and why now");
        sb.AppendLine("  GOALS_AND_NON_GOALS: explicit scope boundaries");
        sb.AppendLine("  SUCCESS_CRITERIA: observable acceptance outcomes");
        sb.AppendLine("  ESTIMATED_TOTAL: effort and critical path");
        sb.AppendLine("CHANGES:");
        sb.AppendLine("  ARCHITECTURE_DECISIONS: for each — Decision, Context, Rationale, Alternatives Rejected");
        sb.AppendLine("  DETAILED_TASKS: each with ID, NAME, ACTIONS (exact files/classes/methods), LOGIC, DEPENDENCIES, DELIVERABLES, VERIFY");
        sb.AppendLine("  EXECUTION_ORDER: dependency graph + parallel groups");
        sb.AppendLine("EVIDENCE:");
        sb.AppendLine("  CONFIRMED_CONSTRAINTS: verified facts from code, memory, or exploration");
        sb.AppendLine("  AFFECTED_COMPONENTS: exact file paths and symbols");
        sb.AppendLine("  VERIFICATION_PLAN: integration scenarios, edge cases, rollback proof");
        sb.AppendLine("RISKS:");
        sb.AppendLine("  Each: RISK description, PROBABILITY (low/medium/high), IMPACT, MITIGATION");
        sb.AppendLine("BLOCKERS:");
        sb.AppendLine("  ASSUMPTIONS_AND_MISSING_INPUTS: what must be resolved before implementation");
        return sb.ToString();
    }
}

public sealed class SmartPlanArgs : ScopedSmartWorkflowArgs
{
    [ToolParam("已有上下文或约束条件")]
    public string? Context { get; set; }
}

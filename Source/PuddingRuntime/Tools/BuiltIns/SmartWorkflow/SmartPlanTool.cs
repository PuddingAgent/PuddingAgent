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
        sb.AppendLine("## OUTPUT (use this exact structure):");
        sb.AppendLine();
        sb.AppendLine("### 1. PROBLEM STATEMENT");
        sb.AppendLine("- What are we solving, and why now?");
        sb.AppendLine("- Success criteria: how do we know it's done?");
        sb.AppendLine();
        sb.AppendLine("### 2. ARCHITECTURE DECISIONS (ADR per major choice)");
        sb.AppendLine("For each decision:");
        sb.AppendLine("  DECISION: what we chose");
        sb.AppendLine("  CONTEXT: what constraints/forces shaped this");
        sb.AppendLine("  RATIONALE: why this over alternatives");
        sb.AppendLine("  ALTERNATIVES_REJECTED: what else we considered and why not");
        sb.AppendLine();
        sb.AppendLine("### 3. DETAILED TASK BREAKDOWN");
        sb.AppendLine("For each task:");
        sb.AppendLine("  #. [Task Name] (effort: quick|medium|thorough, deps: none|#[,#])");
        sb.AppendLine("     Files: path/to/file.cs (NEW|MODIFY|DELETE)");
        sb.AppendLine("     What: specific change — classes, methods, fields");
        sb.AppendLine("     Why: purpose in the overall design");
        sb.AppendLine("     Verify: how to confirm this step succeeded");
        sb.AppendLine();
        sb.AppendLine("### 4. DEPENDENCY & EXECUTION ORDER");
        sb.AppendLine("  DEPENDENCY_GRAPH: text diagram showing task flow");
        sb.AppendLine("  PARALLEL_GROUPS: [[1,2], [3], [4,5]] — same group = concurrent");
        sb.AppendLine();
        sb.AppendLine("### 5. RISK ASSESSMENT");
        sb.AppendLine("  | Risk | Probability | Impact | Mitigation |");
        sb.AppendLine("  |------|-------------|--------|------------|");
        sb.AppendLine();
        sb.AppendLine("### 6. VERIFICATION PLAN");
        sb.AppendLine("- Integration test scenarios");
        sb.AppendLine("- Edge cases to verify");
        sb.AppendLine("- Rollback plan if applicable");
        sb.AppendLine();
        sb.AppendLine("ESTIMATED_TOTAL: total effort across all tasks");
        return sb.ToString();
    }
}

public sealed class SmartPlanArgs : ScopedSmartWorkflowArgs
{
    [ToolParam("已有上下文或约束条件")]
    public string? Context { get; set; }
}

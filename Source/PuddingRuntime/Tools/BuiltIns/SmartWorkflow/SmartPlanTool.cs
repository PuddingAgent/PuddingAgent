using System.Text;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

[Tool(
    id: "smart_plan",
    name: "Smart Plan",
    description: "智能任务规划。用自然语言描述目标，内部委托 Planner 子代理分解为可执行的" +
                 "结构化任务计划，包含步骤、依赖、预估工作量。" +
                 "参数：goal（目标描述）、context（可选，已有的上下文/约束）、" +
                 "timeout_seconds（可选，默认 120s）。模型由 Agent 配置的 Planner_Model 决定。",
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
    protected override int DefaultTimeoutSeconds => 120;

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        SmartPlanArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Goal?.Trim()))
            return ToolExecutionResult.Fail("goal is required. Describe the goal you want to plan.");

        _logger.LogInformation("[SmartPlan] agent={Agent} goal={Goal}", context.AgentInstanceId, args.Goal);

        return await RunSubAgentAsync(args, context, _serviceProvider, _logger, ct, args.TimeoutSeconds);
    }

    protected override string BuildTaskPrompt(SmartPlanArgs args, ToolExecutionContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 📋 PLANNER — Decompose goal into actionable task plan.");
        sb.AppendLine();
        sb.AppendLine("### PROCESS");
        sb.AppendLine("1. Understand the goal and constraints.");
        sb.AppendLine("2. Gather context: file_read + search_memory in PARALLEL if both needed.");
        sb.AppendLine("3. Break into SEQUENTIAL (depends) + PARALLEL (independent) tasks.");
        sb.AppendLine("4. Estimate effort per task: quick (<5min), medium (5-30min), thorough (30min+).");
        sb.AppendLine("5. Identify top 3 risks + mitigation.");
        sb.AppendLine();
        sb.AppendLine("### ⚠️ Read-only only: file_read, search_memory, query_session_logs, list_dir.");
        sb.AppendLine();
        sb.AppendLine($"## 🎯 Goal: {args.Goal}");
        if (!string.IsNullOrWhiteSpace(args.Context))
            sb.AppendLine($"## 📋 Context: {args.Context}");
        sb.AppendLine();
        sb.AppendLine("## OUTPUT:");
        sb.AppendLine("```");
        sb.AppendLine("GOAL_SUMMARY: one sentence");
        sb.AppendLine("TASKS:");
        sb.AppendLine("  1. [Name] (effort, deps: none|[#])");
        sb.AppendLine("     → what it does, deliverable");
        sb.AppendLine("DEPENDENCY_GRAPH: text diagram");
        sb.AppendLine("PARALLEL_GROUPS: [[1,2],[3],[4,5]] — tasks in same group run concurrently");
        sb.AppendLine("RISKS: risk → mitigation");
        sb.AppendLine("ESTIMATED_TOTAL: total effort");
        sb.AppendLine("```");
        return sb.ToString();
    }
}

public sealed class SmartPlanArgs
{
    [ToolParam("目标描述 — 自然语言")]
    public string? Goal { get; set; }

    [ToolParam("已有上下文或约束条件")]
    public string? Context { get; set; }

    [ToolParam("子代理超时秒数，默认 120s")]
    public int? TimeoutSeconds { get; set; }
}

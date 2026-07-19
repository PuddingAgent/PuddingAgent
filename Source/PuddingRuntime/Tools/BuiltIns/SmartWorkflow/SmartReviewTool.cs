using System.Text;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

[Tool(
    id: "smart_review",
    name: "Smart Review",
    description: "智能代码审查。用自然语言描述审查范围，内部委托 Reviewer 子代理自动审查代码" +
                 "质量、安全性、最佳实践，返回结构化的审查报告。" +
                 "参数：task（审查任务）、scope（文件/目录范围）、" +
                 "aspects（可选，关注的安全/质量/性能方面）、" +
                 "timeout_seconds（可选，默认 600s）。模型由 Agent 配置的 Reviewer_Model 决定。",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SubAgentExposure = SubAgentExposure.MainAgentOnly)]
public sealed class SmartReviewTool : SmartWorkflowToolBase<SmartReviewArgs>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SmartReviewTool> _logger;

    public SmartReviewTool(IServiceProvider serviceProvider, ILogger<SmartReviewTool> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override string RoleName => "reviewer";
    // K3 Reviewer: 600s timeout, read-only tools only — avoid exploration, focus on review
    protected override int DefaultTimeoutSeconds => 600;
    protected override int DefaultMaxRounds => 150;
    protected override string? AllowedTools => "file_read,file_search,code_outline,search_grep,list_dir,project_map,code_explore,code_summary";

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        SmartReviewArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Task?.Trim()))
            return ToolExecutionResult.Fail("task is required. Describe what to review.");

        _logger.LogInformation("[SmartReview] agent={Agent} task={Task} scope={Scope}",
            context.AgentInstanceId, args.Task, args.Scope);

        return await RunSubAgentAsync(args, context, _serviceProvider, _logger, ct, args.TimeoutSeconds);
    }

    protected override string BuildTaskPrompt(SmartReviewArgs args, ToolExecutionContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## ✅ REVIEWER — Review code quality. Be thorough, constructive, concrete.");
        sb.AppendLine();
        sb.AppendLine("### PHASE 1 — Discover (run independent lookups in PARALLEL)");
        sb.AppendLine("1. file_search + list_dir to find targets.");
        sb.AppendLine("2. code_outline + file_read on key files — run in parallel for multiple files.");
        sb.AppendLine();
        sb.AppendLine("### PHASE 2 — Analyze: correctness, security, performance, maintainability, best-practices.");
        sb.AppendLine("- Critical: bug, crash, data loss, security hole → MUST fix");
        sb.AppendLine("- Warning: bad practice, tech debt, fragile → SHOULD fix");
        sb.AppendLine("- Suggestion: style, naming, minor improvements → nice to have");
        sb.AppendLine();
        sb.AppendLine("### ⚠️ Read-only only. Focus issues on: correctness → security → performance.");
        sb.AppendLine();
        sb.AppendLine($"## 🎯 Task: {args.Task}");
        if (!string.IsNullOrWhiteSpace(args.Scope))
            sb.AppendLine($"## 📁 Scope: {args.Scope}");
        if (!string.IsNullOrWhiteSpace(args.Aspects))
            sb.AppendLine($"## 🔬 Focus: {args.Aspects}");
        sb.AppendLine();
        AppendCanonicalReportRules(sb);
        sb.AppendLine("## OUTPUT CONTRACT:");
        sb.AppendLine("```");
        sb.AppendLine("SUMMARY:");
        sb.AppendLine("  OVERALL: PASS|NEEDS_WORK|BLOCKED");
        sb.AppendLine("  VERDICT: what is safe, what is not, and the highest-priority action");
        sb.AppendLine("  REVIEWED_SCOPE: exact absolute paths, symbols, commit/diff, and aspects actually inspected");
        sb.AppendLine("CHANGES:");
        sb.AppendLine("  none — read-only review; list recommended changes by finding ID");
        sb.AppendLine("EVIDENCE:");
        sb.AppendLine("  FINDINGS:");
        sb.AppendLine("    - ID: R1");
        sb.AppendLine("      SEVERITY: critical|warning|suggestion");
        sb.AppendLine("      LOCATION: absolute path:specific line or range");
        sb.AppendLine("      SYMBOL: class/function/member");
        sb.AppendLine("      ISSUE: precise defect, not a generic best-practice statement");
        sb.AppendLine("      IMPACT: concrete failure/security/performance/maintenance consequence");
        sb.AppendLine("      PROOF: code behavior, call/data flow, or reproducible scenario");
        sb.AppendLine("      RECOMMENDATION: code-level fix and expected result");
        sb.AppendLine("      CONFIDENCE: high|medium|low with reason");
        sb.AppendLine("  POSITIVE_OBSERVATIONS: verified design strengths worth preserving");
        sb.AppendLine("RISKS:");
        sb.AppendLine("  RESIDUAL_RISKS: unreviewed paths, missing tests, assumptions, and false-positive risk");
        sb.AppendLine("BLOCKERS:");
        sb.AppendLine("  none, or exact files/data/build context that could not be inspected");
        sb.AppendLine("```");
        return sb.ToString();
    }
}

public sealed class SmartReviewArgs : ScopedSmartWorkflowArgs
{
    [ToolParam("关注方面: security, performance, correctness, maintainability, best-practices")]
    public string? Aspects { get; set; }
}

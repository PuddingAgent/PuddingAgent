using System.Text;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

[Tool(
    id: "smart_review",
    name: "Smart Review",
    description: "智能代码审查。用自然语言描述审查范围，内部委托 Reviewer 子代理自动审查代码" +
                 "质量、安全性、最佳实践，返回结构化的审查报告。" +
                 "参数：what（审查目标）、scope（文件/目录范围）、" +
                 "aspects（可选，关注的安全/质量/性能方面）、" +
                 "timeout_seconds（可选，默认 180s）。模型由 Agent 配置的 Reviewer_Model 决定。",
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
    protected override int DefaultTimeoutSeconds => 180;

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        SmartReviewArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.What?.Trim()) && string.IsNullOrWhiteSpace(args.Scope?.Trim()))
            return ToolExecutionResult.Fail("what or scope is required. Describe what to review.");

        _logger.LogInformation("[SmartReview] agent={Agent} what={What} scope={Scope}",
            context.AgentInstanceId, args.What, args.Scope);

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
        sb.AppendLine($"## 🎯 Target: {args.What ?? args.Scope}");
        if (!string.IsNullOrWhiteSpace(args.Scope))
            sb.AppendLine($"## 📁 Scope: {args.Scope}");
        if (!string.IsNullOrWhiteSpace(args.Aspects))
            sb.AppendLine($"## 🔬 Focus: {args.Aspects}");
        sb.AppendLine();
        sb.AppendLine("## OUTPUT:");
        sb.AppendLine("```");
        sb.AppendLine("OVERALL: PASS|NEEDS_WORK|BLOCKED");
        sb.AppendLine("CRITICAL:");
        sb.AppendLine("  - file:line — issue → fix");
        sb.AppendLine("WARNINGS:");
        sb.AppendLine("  - file:line — issue → fix");
        sb.AppendLine("SUGGESTIONS:");
        sb.AppendLine("  - file:line — improvement → why");
        sb.AppendLine("SUMMARY: 2-3 sentence verdict");
        sb.AppendLine("```");
        return sb.ToString();
    }
}

public sealed class SmartReviewArgs
{
    [ToolParam("审查目标 — 自然语言描述")]
    public string? What { get; set; }

    [ToolParam("审查范围 — 文件或目录")]
    public string? Scope { get; set; }

    [ToolParam("关注方面: security, performance, correctness, maintainability, best-practices")]
    public string? Aspects { get; set; }

    [ToolParam("子代理超时秒数，默认 180s")]
    public int? TimeoutSeconds { get; set; }
}

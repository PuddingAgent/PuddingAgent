using System.Text;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

[Tool(
    id: "smart_research",
    name: "Smart Research",
    description: "智能深度研究。用自然语言描述研究课题，内部委托 Researcher 子代理自动执行" +
                 "anysearch_search + http_fetch + file_read 多源信息收集，返回综合分析报告。" +
                 "参数：task（研究任务）、scope（可选，研究边界）、" +
                 "domain（可选，搜索领域如 code/academic/news）、" +
                 "timeout_seconds（可选，默认 600s）。模型由 Agent 配置的 Researcher_Model 决定。",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SubAgentExposure = SubAgentExposure.MainAgentOnly)]
public sealed class SmartResearchTool : SmartWorkflowToolBase<SmartResearchArgs>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SmartResearchTool> _logger;

    public SmartResearchTool(IServiceProvider serviceProvider, ILogger<SmartResearchTool> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override string RoleName => "researcher";
    protected override int DefaultMaxRounds => 200;
    protected override IReadOnlyList<string>? FallbackModelIds =>
        new[] { "deepseek/deepseek-v4-pro", "deepseek/deepseek-v4-flash" };

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        SmartResearchArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Task?.Trim()))
            return ToolExecutionResult.Fail("task is required. Describe your research question in natural language.");

        _logger.LogInformation("[SmartResearch] agent={Agent} task={Task}",
            context.AgentInstanceId, args.Task);

        return await RunSubAgentAsync(args, context, _serviceProvider, _logger, ct, args.TimeoutSeconds);
    }

    protected override string BuildTaskPrompt(SmartResearchArgs args, ToolExecutionContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🔬 RESEARCHER — Multi-source research + synthesis. Do NOT stop early.");
        sb.AppendLine();
        sb.AppendLine("### PHASE 1 — Gather (run independent searches in PARALLEL)");
        sb.AppendLine("1. anysearch_search — multiple angle queries in parallel.");
        if (!string.IsNullOrWhiteSpace(args.Domain))
            sb.AppendLine($"   Domain: {args.Domain}");
        sb.AppendLine("2. http_fetch top 2-3 results for depth.");
        sb.AppendLine("3. file_read + search_grep for local context. Run with search_memory in parallel.");
        sb.AppendLine();
        sb.AppendLine("### PHASE 2 — Synthesize");
        sb.AppendLine("4. Cross-reference: consensus? conflicts? gaps?");
        sb.AppendLine("5. Form conclusions backed by evidence (cite source).");
        sb.AppendLine();
        sb.AppendLine("### ⚠️ Read/search only. If http_fetch fails: note gap and continue.");
        sb.AppendLine();
        sb.AppendLine($"## 🎯 Task: {args.Task}");
        if (!string.IsNullOrWhiteSpace(args.Scope))
            sb.AppendLine($"## 📁 Scope: {args.Scope}");
        sb.AppendLine();
        AppendCanonicalReportRules(sb);
        sb.AppendLine("## OUTPUT CONTRACT:");
        sb.AppendLine("```");
        sb.AppendLine("SUMMARY:");
        sb.AppendLine("  STATUS: complete|partial|not_found");
        sb.AppendLine("  DIRECT_ANSWER: evidence-backed answer to the research question");
        sb.AppendLine("  CONCLUSIONS: ranked conclusions and confidence");
        sb.AppendLine("CHANGES:");
        sb.AppendLine("  none — read-only research; state the source set and scope examined");
        sb.AppendLine("EVIDENCE:");
        sb.AppendLine("  SOURCES:");
        sb.AppendLine("    - SOURCE: exact URL or absolute local path");
        sb.AppendLine("      TITLE: source title");
        sb.AppendLine("      PUBLISHED_OR_VERSION: date/version when known");
        sb.AppendLine("      AUTHORITY: primary|official|secondary");
        sb.AppendLine("      CLAIMS_SUPPORTED: which numbered findings this source supports");
        sb.AppendLine("  FINDINGS:");
        sb.AppendLine("    - ID: F1");
        sb.AppendLine("      CLAIM: concrete finding");
        sb.AppendLine("      SUPPORT: source identifiers and exact quoted fact or local path:line");
        sb.AppendLine("      CONFIDENCE: high|medium|low with reason");
        sb.AppendLine("  SYNTHESIS: agreements, conflicts, causal relationships, and implications");
        sb.AppendLine("RISKS:");
        sb.AppendLine("  LIMITATIONS: freshness, source quality, unresolved conflicts, and assumptions");
        sb.AppendLine("  GAPS: unknowns plus exact next queries or evidence needed");
        sb.AppendLine("BLOCKERS:");
        sb.AppendLine("  none, or unavailable sources/access/tool failures and their impact");
        sb.AppendLine("```");
        return sb.ToString();
    }
}

public sealed class SmartResearchArgs : ScopedSmartWorkflowArgs
{
    [ToolParam("搜索领域: general, code, academic, news, finance, health")]
    public string? Domain { get; set; }
}

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
                 "timeout_seconds（可选，默认 180s）。模型由 Agent 配置的 Researcher_Model 决定。",
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
    protected override int DefaultTimeoutSeconds => 600;
    protected override int DefaultMaxRounds => 150;

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
        sb.AppendLine("## OUTPUT:");
        sb.AppendLine("```");
        sb.AppendLine("SOURCES: url/path — title");
        sb.AppendLine("FINDINGS: finding (source)");
        sb.AppendLine("SYNTHESIS: cross-referenced analysis");
        sb.AppendLine("CONCLUSIONS: evidence-backed");
        sb.AppendLine("GAPS: unknown + next queries");
        sb.AppendLine("```");
        return sb.ToString();
    }
}

public sealed class SmartResearchArgs : ScopedSmartWorkflowArgs
{
    [ToolParam("搜索领域: general, code, academic, news, finance, health")]
    public string? Domain { get; set; }
}

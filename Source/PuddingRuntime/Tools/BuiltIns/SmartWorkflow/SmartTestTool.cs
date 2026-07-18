using System.Text;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

[Tool(
    id: "smart_test",
    name: "Smart Test",
    description: "智能测试执行。用自然语言描述测试需求，内部委托 Tester 子代理自动运行测试、" +
                 "分析失败原因、生成测试报告。需要显式授权（High 权限）。" +
                 "参数：what（测试什么）、scope（可选，测试范围/项目）、" +
                 "timeout_seconds（可选，默认 300s）。模型由 Agent 配置的 Tester_Model 决定。",
    category: ToolCategory.Orchestration,
    permission: ToolPermissionLevel.High,
    safety: ToolSafetyFlags.None,
    SubAgentExposure = SubAgentExposure.MainAgentOnly)]
public sealed class SmartTestTool : SmartWorkflowToolBase<SmartTestArgs>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SmartTestTool> _logger;

    public SmartTestTool(IServiceProvider serviceProvider, ILogger<SmartTestTool> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override string RoleName => "tester";
    protected override int DefaultTimeoutSeconds => 300;

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        SmartTestArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.What?.Trim()))
            return ToolExecutionResult.Fail("what is required. Describe what to test.");

        _logger.LogInformation("[SmartTest] agent={Agent} what={What}", context.AgentInstanceId, args.What);

        return await RunSubAgentAsync(args, context, _serviceProvider, _logger, ct, args.TimeoutSeconds);
    }

    protected override string BuildTaskPrompt(SmartTestArgs args, ToolExecutionContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🧪 TESTER — Run tests, diagnose failures, report.");
        sb.AppendLine();
        sb.AppendLine("### PROCESS");
        sb.AppendLine("1. Discover: list_dir + file_search for test projects (run in PARALLEL).");
        sb.AppendLine("2. Run: `dotnet test` via terminal_start → terminal_wait. Run independent test projects in parallel.");
        sb.AppendLine("3. Analyze: for each failure → read source (search_grep + file_read) → root cause (file:line).");
        sb.AppendLine("4. Report: pattern analysis + actionable fix recommendations.");
        sb.AppendLine();
        sb.AppendLine("### ⚠️ terminal_start/terminal_wait + dotnet test only. No spawn_sub_agent.");
        sb.AppendLine();
        sb.AppendLine($"## 🎯 Target: {args.What}");
        if (!string.IsNullOrWhiteSpace(args.Scope))
            sb.AppendLine($"## 📁 Scope: {args.Scope}");
        sb.AppendLine();
        sb.AppendLine("## OUTPUT:");
        sb.AppendLine("```");
        sb.AppendLine("TEST_RESULT: PASS|FAIL|PARTIAL");
        sb.AppendLine("TOTAL:N PASSED:N FAILED:N SKIPPED:N");
        sb.AppendLine("FAILURES:");
        sb.AppendLine("  - TestName: reason → root cause (file:line)");
        sb.AppendLine("ANALYSIS: pattern — related failures?");
        sb.AppendLine("RECOMMENDATIONS: how to fix");
        sb.AppendLine("```");
        return sb.ToString();
    }
}

public sealed class SmartTestArgs
{
    [ToolParam("测试目标 — 自然语言描述，如 '运行所有单元测试' 或 '测试 UserService'")]
    public string? What { get; set; }

    [ToolParam("测试范围/项目路径")]
    public string? Scope { get; set; }

    [ToolParam("子代理超时秒数，默认 300s")]
    public int? TimeoutSeconds { get; set; }
}

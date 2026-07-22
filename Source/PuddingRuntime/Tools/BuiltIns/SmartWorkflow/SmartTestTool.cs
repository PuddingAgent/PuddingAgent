using System.Text;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

[Tool(
    id: "smart_test",
    name: "Smart Test",
    description: "智能测试执行。用自然语言描述测试需求，内部委托 Tester 子代理自动运行测试、" +
                 "分析失败原因、生成测试报告。需要显式授权（High 权限）。" +
                 "参数：task（测试任务）、scope（可选，测试范围/项目）、" +
                 "timeout_seconds（可选，默认 600s）。模型由 Agent 配置的 Tester_Model 决定。",
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
    protected override int DefaultMaxRounds => 200;
    protected override IReadOnlyList<string>? FallbackModelIds =>
        new[] { "deepseek/deepseek-v4-pro", "deepseek/deepseek-v4-flash" };

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        SmartTestArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Task?.Trim()))
            return ToolExecutionResult.Fail("task is required. Describe what to test.");

        _logger.LogInformation("[SmartTest] agent={Agent} task={Task}", context.AgentInstanceId, args.Task);

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
        sb.AppendLine($"## 🎯 Task: {args.Task}");
        if (!string.IsNullOrWhiteSpace(args.Scope))
            sb.AppendLine($"## 📁 Scope: {args.Scope}");
        sb.AppendLine();
        AppendCanonicalReportRules(sb);
        sb.AppendLine("## OUTPUT CONTRACT:");
        sb.AppendLine("```");
        sb.AppendLine("SUMMARY:");
        sb.AppendLine("  TEST_RESULT: PASS|FAIL|PARTIAL|BLOCKED");
        sb.AppendLine("  SCOPE_TESTED: projects/suites/features actually exercised");
        sb.AppendLine("  TOTALS: total, passed, failed, skipped, duration");
        sb.AppendLine("  VERDICT: what the results prove and do not prove");
        sb.AppendLine("CHANGES:");
        sb.AppendLine("  none — testing is read/execute only; list generated logs/reports as artifacts");
        sb.AppendLine("EVIDENCE:");
        sb.AppendLine("  ENVIRONMENT: runtime, configuration, working directory, relevant versions");
        sb.AppendLine("  COMMANDS:");
        sb.AppendLine("    - COMMAND: exact command");
        sb.AppendLine("      EXIT_CODE: numeric exit code");
        sb.AppendLine("      RESULT: passed/failed/skipped counts and duration");
        sb.AppendLine("      ARTIFACTS: absolute paths to logs/TRX/coverage files, or none");
        sb.AppendLine("  FAILURES:");
        sb.AppendLine("    - TEST: fully-qualified test name");
        sb.AppendLine("      OBSERVED: assertion/error and concise log evidence");
        sb.AppendLine("      REPRODUCTION: exact command/filter");
        sb.AppendLine("      ROOT_CAUSE: absolute path:line and causal explanation, or unconfirmed with reason");
        sb.AppendLine("      RECOMMENDATION: code-level correction and regression test");
        sb.AppendLine("  PATTERN_ANALYSIS: relationships among failures, or none");
        sb.AppendLine("RISKS:");
        sb.AppendLine("  COVERAGE_GAPS: tests not run, untested behavior, flakiness, environment differences");
        sb.AppendLine("BLOCKERS:");
        sb.AppendLine("  none, or missing dependency/permission/environment problem and exact remediation");
        sb.AppendLine("```");
        return sb.ToString();
    }
}

public sealed class SmartTestArgs : ScopedSmartWorkflowArgs
{
}

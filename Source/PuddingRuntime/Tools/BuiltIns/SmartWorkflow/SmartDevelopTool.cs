using System.Text;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

[Tool(
    id: "smart_develop",
    name: "Smart Develop",
    description: "智能代码实现。用自然语言描述开发任务，内部委托 Developer 子代理自动实现代码，" +
                 "包括文件编辑、构建验证。需要显式授权（High 权限）。" +
                 "参数：task（开发任务描述）、scope（可选，工作目录）、" +
                 "timeout_seconds（可选，默认 1200s）。模型由 Agent 配置的 Developer_Model 决定。",
    category: ToolCategory.Orchestration,
    permission: ToolPermissionLevel.High,
    safety: ToolSafetyFlags.None,
    SubAgentExposure = SubAgentExposure.MainAgentOnly)]
public sealed class SmartDevelopTool : SmartWorkflowToolBase<SmartDevelopArgs>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SmartDevelopTool> _logger;

    public SmartDevelopTool(IServiceProvider serviceProvider, ILogger<SmartDevelopTool> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override string RoleName => "developer";
    protected override int DefaultMaxRounds => 200;
    protected override IReadOnlyList<string>? FallbackModelIds =>
        new[] { "deepseek/deepseek-v4-pro", "deepseek/deepseek-v4-flash" };
    protected override string AllowedTools => "file_read,file_search,list_dir,search_grep,code_outline,code_symbol_search,code_explore,code_callers,code_callees,code_summary,project_map,file_patch,apply_patch,file_write,shell,terminal_start,terminal_wait,terminal_read,terminal_status,terminal_cancel,search_memory";

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        SmartDevelopArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Task?.Trim()))
            return ToolExecutionResult.Fail("task is required. Describe the development task.");

        _logger.LogInformation("[SmartDevelop] agent={Agent} task={Task}", context.AgentInstanceId, args.Task);

        return await RunSubAgentAsync(args, context, _serviceProvider, _logger, ct, args.TimeoutSeconds);
    }

    protected override string BuildTaskPrompt(SmartDevelopArgs args, ToolExecutionContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 💻 DEVELOPER — Implement code changes. Build, verify, iterate.");
        sb.AppendLine();
        sb.AppendLine("### PROCESS (follow strictly)");
        sb.AppendLine("1. **Baseline**: Run `dotnet build` FIRST to establish clean baseline.");
        sb.AppendLine("2. **Understand**: file_read + code_outline on affected files (run in parallel).");
        sb.AppendLine("3. **Implement**: file_patch for existing files, file_write for new files. Minimal, focused edits.");
        sb.AppendLine("4. **Build**: `dotnet build`. If FAIL → fix (max 3 retries on same error).");
        sb.AppendLine("5. **Test**: `dotnet test` on affected projects. Report results.");
        sb.AppendLine();
        sb.AppendLine("### ⚠️ RULES");
        sb.AppendLine("- Edit: file_patch, apply_patch, file_write. Verify: terminal_start + terminal_wait.");
        sb.AppendLine("- Read: file_read, code_outline, search_grep, list_dir.");
        sb.AppendLine("- NEVER: spawn_sub_agent. If stuck: report what you tried and why.");
        sb.AppendLine();
        sb.AppendLine($"## 🎯 Task: {args.Task}");
        if (!string.IsNullOrWhiteSpace(args.Scope))
            sb.AppendLine($"## 📁 CWD: {args.Scope}");
        sb.AppendLine();
        AppendCanonicalReportRules(sb);
        sb.AppendLine("## OUTPUT CONTRACT:");
        sb.AppendLine("```");
        sb.AppendLine("SUMMARY:");
        sb.AppendLine("  STATUS: complete|partial|blocked");
        sb.AppendLine("  OUTCOME: user-visible and architectural behavior now achieved");
        sb.AppendLine("  SCOPE_COMPLETED: what was implemented versus requested");
        sb.AppendLine("CHANGES:");
        sb.AppendLine("  FILES:");
        sb.AppendLine("    - PATH: absolute path");
        sb.AppendLine("      ACTION: added|modified|deleted|moved");
        sb.AppendLine("      SYMBOLS: classes/functions/members/config keys");
        sb.AppendLine("      LINES: resulting line/range when available");
        sb.AppendLine("      LOGIC: exact behavior changed and why");
        sb.AppendLine("  CONTRACT_CHANGES: API/schema/config/event changes, or none");
        sb.AppendLine("EVIDENCE:");
        sb.AppendLine("  BASELINE: exact command, exit code, and relevant result before editing");
        sb.AppendLine("  BUILD: exact command, exit code, error/warning counts");
        sb.AppendLine("  TESTS:");
        sb.AppendLine("    - COMMAND: exact command");
        sb.AppendLine("      RESULT: pass|fail|skipped with passed/failed/skipped counts and duration");
        sb.AppendLine("      COVERAGE: behavior or regression protected");
        sb.AppendLine("  MANUAL_VERIFICATION: API/UI/runtime checks and observed results, or not_run with reason");
        sb.AppendLine("RISKS:");
        sb.AppendLine("  REMAINING: known limitations, untested paths, compatibility or operational risks");
        sb.AppendLine("  DEVIATIONS: differences from the requested design and why");
        sb.AppendLine("BLOCKERS:");
        sb.AppendLine("  none, or unresolved error plus attempts made and exact next action");
        sb.AppendLine("```");
        return sb.ToString();
    }
}

public sealed class SmartDevelopArgs : ScopedSmartWorkflowArgs
{
}

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
                 "timeout_seconds（可选，默认 300s）。模型由 Agent 配置的 Developer_Model 决定。",
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
    protected override int DefaultTimeoutSeconds => 300;
    protected override int DefaultMaxRounds => 24;

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
        sb.AppendLine("## OUTPUT:");
        sb.AppendLine("```");
        sb.AppendLine("FILES_CHANGED: path:line-range — what + why");
        sb.AppendLine("BUILD_RESULT: PASS|FAIL");
        sb.AppendLine("TEST_RESULT: PASS|FAIL|SKIPPED");
        sb.AppendLine("SUMMARY: 2-3 sentences on what was done");
        sb.AppendLine("```");
        return sb.ToString();
    }
}

public sealed class SmartDevelopArgs : ScopedSmartWorkflowArgs
{
}

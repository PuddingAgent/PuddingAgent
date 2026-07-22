using System.Text;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

[Tool(
    id: "smart_deploy",
    name: "Smart Deploy",
    description: "智能部署运维。用自然语言描述部署任务，内部委托 Deployer 子代理执行部署操作。" +
                 "需要显式授权（High 权限）。" +
                 "参数：task（部署任务描述）、scope（可选，工作目录）、environment（可选，目标环境）、" +
                 "timeout_seconds（可选，默认 600s）。模型由 Agent 配置的 Deployer_Model 决定。",
    category: ToolCategory.Orchestration,
    permission: ToolPermissionLevel.High,
    safety: ToolSafetyFlags.None,
    SubAgentExposure = SubAgentExposure.MainAgentOnly)]
public sealed class SmartDeployTool : SmartWorkflowToolBase<SmartDeployArgs>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SmartDeployTool> _logger;

    public SmartDeployTool(IServiceProvider serviceProvider, ILogger<SmartDeployTool> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override string RoleName => "deployer";
    protected override int DefaultMaxRounds => 300;

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        SmartDeployArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Task?.Trim()))
            return ToolExecutionResult.Fail("task is required. Describe the deployment task.");

        _logger.LogInformation("[SmartDeploy] agent={Agent} task={Task}", context.AgentInstanceId, args.Task);

        return await RunSubAgentAsync(args, context, _serviceProvider, _logger, ct, args.TimeoutSeconds);
    }

    protected override string BuildTaskPrompt(SmartDeployArgs args, ToolExecutionContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🚀 DEPLOYER — Execute deployment safely. Stop on first error.");
        sb.AppendLine();
        sb.AppendLine("### PROCESS");
        sb.AppendLine("1. Verify: read configs, check prerequisites.");
        sb.AppendLine("2. Plan: identify exact steps + rollback path.");
        sb.AppendLine("3. Execute: terminal_start → terminal_wait. Verify each step before next.");
        sb.AppendLine("4. Verify: health check, smoke test.");
        sb.AppendLine("5. Report: what, version, status.");
        sb.AppendLine();
        sb.AppendLine("### ⚠️ SAFETY — rollback plan BEFORE execution. Stop on error. Log everything.");
        sb.AppendLine();
        sb.AppendLine($"## 🎯 Task: {args.Task}");
        if (!string.IsNullOrWhiteSpace(args.Scope))
            sb.AppendLine($"## 📁 CWD: {args.Scope}");
        if (!string.IsNullOrWhiteSpace(args.Environment))
            sb.AppendLine($"## 🌐 Env: {args.Environment}");
        sb.AppendLine();
        AppendCanonicalReportRules(sb);
        sb.AppendLine("## OUTPUT CONTRACT:");
        sb.AppendLine("```");
        sb.AppendLine("SUMMARY:");
        sb.AppendLine("  DEPLOYMENT_STATUS: SUCCESS|PARTIAL|FAILED|BLOCKED");
        sb.AppendLine("  ENVIRONMENT: exact target");
        sb.AppendLine("  VERSION: artifact version, image digest, tag, or commit hash");
        sb.AppendLine("  OUTCOME: externally observable deployment result");
        sb.AppendLine("CHANGES:");
        sb.AppendLine("  EXECUTED_STEPS:");
        sb.AppendLine("    - STEP: ordered action");
        sb.AppendLine("      COMMAND_OR_OPERATION: exact command/API/operation with secrets redacted");
        sb.AppendLine("      RESULT: success|failed|skipped plus exit/status code");
        sb.AppendLine("      ARTIFACT: deployed artifact/config and absolute local path or remote identifier");
        sb.AppendLine("  CONFIG_CHANGES: keys/resources changed without secret values, or none");
        sb.AppendLine("EVIDENCE:");
        sb.AppendLine("  PRECHECKS: prerequisite checks and observed results");
        sb.AppendLine("  HEALTH_CHECKS: endpoint/probe/metric, expected value, observed value, timestamp");
        sb.AppendLine("  SMOKE_TESTS: exact command/request and observed result");
        sb.AppendLine("  LOGS: relevant deployment/runtime log identifiers or absolute paths");
        sb.AppendLine("RISKS:");
        sb.AppendLine("  ROLLBACK: exact rollback trigger, procedure, and whether it was tested");
        sb.AppendLine("  RESIDUAL_RISKS: partial rollout, propagation, monitoring, or data migration risks");
        sb.AppendLine("BLOCKERS:");
        sb.AppendLine("  none, or failed step/prerequisite with impact and safe next action");
        sb.AppendLine("```");
        return sb.ToString();
    }
}

public sealed class SmartDeployArgs : ScopedSmartWorkflowArgs
{
    [ToolParam("目标环境: dev, staging, production")]
    public string? Environment { get; set; }
}

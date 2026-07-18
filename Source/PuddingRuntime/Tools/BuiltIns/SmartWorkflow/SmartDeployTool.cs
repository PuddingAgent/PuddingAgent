using System.Text;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

[Tool(
    id: "smart_deploy",
    name: "Smart Deploy",
    description: "智能部署运维。用自然语言描述部署任务，内部委托 Deployer 子代理执行部署操作。" +
                 "需要显式授权（High 权限）。" +
                 "参数：task（部署任务描述）、environment（可选，目标环境）、" +
                 "timeout_seconds（可选，默认 300s）。模型由 Agent 配置的 Deployer_Model 决定。",
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
    protected override int DefaultTimeoutSeconds => 300;

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
        if (!string.IsNullOrWhiteSpace(args.Environment))
            sb.AppendLine($"## 🌐 Env: {args.Environment}");
        sb.AppendLine();
        sb.AppendLine("## OUTPUT:");
        sb.AppendLine("```");
        sb.AppendLine("DEPLOYMENT_STATUS: SUCCESS|PARTIAL|FAILED");
        sb.AppendLine("STEPS: step → result");
        sb.AppendLine("VERSION: hash/tag");
        sb.AppendLine("ROLLBACK: how to revert");
        sb.AppendLine("HEALTH: status");
        sb.AppendLine("```");
        return sb.ToString();
    }
}

public sealed class SmartDeployArgs
{
    [ToolParam("部署任务 — 自然语言描述")]
    public string? Task { get; set; }

    [ToolParam("目标环境: dev, staging, production")]
    public string? Environment { get; set; }

    [ToolParam("子代理超时秒数，默认 300s")]
    public int? TimeoutSeconds { get; set; }
}

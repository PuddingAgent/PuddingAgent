using PuddingCode.Models;
using PuddingRuntime.Services.Sandbox;

namespace PuddingRuntime.Services.Skills;

/// <summary>
/// Python Skill——在 Agent 专属 Docker 容器内执行 Python 3 代码。
/// 代码通过 base64 管道传入，避免引号或特殊字符的转义问题。
/// </summary>
public sealed class PythonSkill : ContainerSkillBase
{
    private readonly ILogger<PythonSkill> _logger;

    public PythonSkill(
        AgentContainerRegistry registry,
        ISandboxProvider sandbox,
        AgentSkillPackageRegistry skillPackageRegistry,
        ILogger<PythonSkill> logger)
        : base(registry, sandbox, skillPackageRegistry, logger)
    {
        _logger = logger;
    }

    public override string SkillId => "python";
    public override string Name => "python";
    public override string Description =>
        "Execute Python 3 code inside the agent's isolated Docker container. " +
        "Returns stdout and stderr. Ideal for data analysis, math, text processing, and scripting.";
    public override bool RequiresShellExecution => true;
    public override ToolPermissionLevel PermissionLevel => ToolPermissionLevel.Medium;

    public override async Task<SkillResult> ExecuteAsync(SkillInvokeRequest request, CancellationToken ct = default)
    {
        var binding = await EnsureContainerRunningAsync(request.AgentInstanceId, request.WorkspaceId, ct);
        if (binding is null)
            return Fail("Failed to provision Docker container for agent.");

        // base64 encode the Python code to avoid shell escaping issues
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(request.Input));
        var cmd = $"echo '{encoded}' | base64 -d | python3";

        _logger.LogInformation("[PythonSkill] agent={Agent} code_len={Len}",
            request.AgentInstanceId, request.Input.Length);

        var exec = await Sandbox.ExecAsync(binding.ContainerId, cmd, timeoutSeconds: 30, ct);

        if (exec.ExitCode == -1 && exec.Error is not null)
            Registry.UpdateStatus(request.AgentInstanceId, AgentContainerStatus.Error, exec.Error);

        var output = (exec.Stdout + (exec.Stderr.Length > 0 ? "\n[stderr]: " + exec.Stderr : "")).TrimEnd();

        return new SkillResult
        {
            Success  = exec.ExitCode == 0,
            Output   = output,
            ExitCode = exec.ExitCode,
            Error    = exec.ExitCode != 0 ? $"exit code {exec.ExitCode}" : null,
        };
    }
}

using System.Diagnostics;
using PuddingCode.Models;
using PuddingRuntime.Services.Sandbox;

namespace PuddingRuntime.Services.Skills;

/// <summary>
/// Bash Skill——在 Agent 对应的 Docker 容器内执行 bash 命令。
/// 若容器尚未启动，自动按需创建。
/// </summary>
public sealed class BashSkill : ContainerSkillBase
{
    private readonly ILogger<BashSkill> _logger;

    public BashSkill(
        AgentContainerRegistry registry,
        ISandboxProvider sandbox,
        AgentSkillPackageRegistry skillPackageRegistry,
        ILogger<BashSkill> logger)
        : base(registry, sandbox, skillPackageRegistry, logger)
    {
        _logger = logger;
    }

    public override string SkillId => "bash";
    public override string Name => "bash";
    public override string Description =>
        "Execute a bash shell command inside the agent's isolated Docker container. " +
        "Returns stdout and stderr. Use for file operations, code execution, data processing, etc.";
    public override bool RequiresShellExecution => true;
    public override ToolPermissionLevel PermissionLevel => ToolPermissionLevel.Medium;

    public override async Task<SkillResult> ExecuteAsync(SkillInvokeRequest request, CancellationToken ct = default)
    {
        var command = request.Input?.Trim();
        if (string.IsNullOrEmpty(command))
            return Fail("Command is required.");

        // 尝试 Docker 沙箱模式
        try
        {
            var binding = await EnsureContainerRunningAsync(request.AgentInstanceId, request.WorkspaceId, ct);
            if (binding is not null)
            {
                _logger.LogInformation("[BashSkill] agent={Agent} container={CId} cmd={Cmd}",
                    request.AgentInstanceId, Short(binding.ContainerId),
                    command.Length > 80 ? command[..80] + "…" : command);

                var exec = await Sandbox.ExecAsync(binding.ContainerId, command,
                    timeoutSeconds: 30, ct);

                // 容器意外退出
                if (exec.Error is "Timeout" || (exec.ExitCode == -1 && exec.Error is not null))
                    Registry.UpdateStatus(request.AgentInstanceId, AgentContainerStatus.Error, exec.Error);

                // 合并 stdout / stderr
                var output = (exec.Stdout + (exec.Stderr.Length > 0 ? "\n[stderr]: " + exec.Stderr : ""))
                    .TrimEnd();

                return new SkillResult
                {
                    Success  = exec.ExitCode == 0,
                    Output   = output,
                    ExitCode = exec.ExitCode,
                    Error    = exec.ExitCode != 0 ? $"exit code {exec.ExitCode}" : null,
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[BashSkill] Docker sandbox unavailable, falling back to host mode cmd={Cmd}", command);
        }

        // ═══ 降级：宿主模式 ═══
        return ExecuteBashHostMode(command, ct);
    }

    private static string Short(string id) => id.Length >= 12 ? id[..12] : id;
}

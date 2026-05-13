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

    /// <summary>
    /// 宿主机模式：优先用 bash -c，回退到 cmd /c 执行命令。
    /// 限制：30秒超时，单次执行。
    /// </summary>
    private static SkillResult ExecuteBashHostMode(string command, CancellationToken ct)
    {
        // 优先尝试 bash（WSL / Git Bash），回退到 cmd
        var (fileName, args) = DetectShell();
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args.Replace("{cmd}", command),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi);
        if (proc is null)
            return Fail("Failed to start shell process on host.");

        if (!proc.WaitForExit(30000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return Fail("Bash execution timed out after 30 seconds.");
        }

        var stdout = proc.StandardOutput.ReadToEnd().TrimEnd();
        var stderr = proc.StandardError.ReadToEnd().TrimEnd();
        var output = (stdout + (stderr.Length > 0 ? "\n[stderr]: " + stderr : "")).TrimEnd();

        return new SkillResult
        {
            Success  = proc.ExitCode == 0,
            Output   = output,
            ExitCode = proc.ExitCode,
            Error    = proc.ExitCode != 0 ? $"exit code {proc.ExitCode}" : null,
        };
    }

    /// <summary>
    /// 检测可用 shell：bash > cmd。
    /// </summary>
    private static (string fileName, string args) DetectShell()
    {
        // 简单检测：如果 bash 在 PATH 中可用则使用
        try
        {
            var check = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "where" : "which",
                Arguments = "bash",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(check);
            p?.WaitForExit(3000);
            if (p?.ExitCode == 0)
                return ("bash", "-c \"{cmd}\"");
        }
        catch { /* fall through */ }

        return OperatingSystem.IsWindows()
            ? ("cmd.exe", "/c {cmd}")
            : ("sh", "-c \"{cmd}\"");
    }
}

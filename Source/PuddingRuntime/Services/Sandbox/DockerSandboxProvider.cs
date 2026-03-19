using CliWrap;
using CliWrap.Buffered;

namespace PuddingRuntime.Services.Sandbox;

/// <summary>
/// Docker 实现的沙箱提供者。
/// 通过宿主机 Docker CLI（挂载 /var/run/docker.sock）在隔离容器内执行命令。
/// </summary>
public sealed class DockerSandboxProvider : ISandboxProvider
{
    private readonly ILogger<DockerSandboxProvider> _logger;

    public DockerSandboxProvider(ILogger<DockerSandboxProvider> logger)
    {
        _logger = logger;
    }

    public string ProviderType => "docker";

    /// <inheritdoc/>
    public async Task<SandboxStartResult> StartAsync(SandboxStartRequest request, CancellationToken ct = default)
    {
        // 容器名：pudding-agent-{agentId 前 12 位}
        var name = request.ContainerName
            ?? $"pudding-agent-{SanitizeId(request.AgentInstanceId, 12)}";

        var args = new List<string>
        {
            "run", "-d",
            "--name", name,
            "--label", $"pudding.agentInstanceId={request.AgentInstanceId}",
            "--label", $"pudding.workspaceId={request.WorkspaceId}",
            // 限制资源占用
            "--memory", "512m",
            "--cpus",   "1.0",
        };

        foreach (var (k, v) in request.EnvironmentVars)
            args.AddRange(["-e", $"{k}={v}"]);

        foreach (var mount in request.Mounts)
            args.AddRange(["-v", mount]);

        if (request.WorkingDirectory is not null)
            args.AddRange(["-w", request.WorkingDirectory]);

        // 镜像 + 保持前台进程的命令
        args.Add(request.Image);
        args.AddRange(["tail", "-f", "/dev/null"]);

        _logger.LogInformation(
            "[DockerSandbox] Starting container name={Name} image={Image} agent={Agent}",
            name, request.Image, request.AgentInstanceId);

        try
        {
            var result = await Cli.Wrap("docker")
                .WithArguments(args)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(ct);

            if (result.ExitCode != 0)
            {
                var err = result.StandardError.Trim();
                _logger.LogWarning("[DockerSandbox] Start failed agent={Agent}: {Err}",
                    request.AgentInstanceId, err);
                return new SandboxStartResult { Success = false, Error = err };
            }

            var containerId = result.StandardOutput.Trim();
            _logger.LogInformation("[DockerSandbox] Started id={Id} name={Name}",
                Short(containerId), name);

            return new SandboxStartResult
            {
                Success = true,
                ContainerId = containerId,
                ContainerName = name,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DockerSandbox] Exception starting container for agent={Agent}",
                request.AgentInstanceId);
            return new SandboxStartResult { Success = false, Error = ex.Message };
        }
    }

    /// <inheritdoc/>
    public async Task<SandboxStopResult> StopAsync(string containerId, CancellationToken ct = default)
    {
        try
        {
            var result = await Cli.Wrap("docker")
                .WithArguments(["stop", containerId])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(ct);

            if (result.ExitCode != 0)
                return new SandboxStopResult { Success = false, Error = result.StandardError.Trim() };

            return new SandboxStopResult { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DockerSandbox] StopAsync failed container={Id}", Short(containerId));
            return new SandboxStopResult { Success = false, Error = ex.Message };
        }
    }

    /// <inheritdoc/>
    public async Task<SandboxExecResult> ExecAsync(
        string containerId, string command,
        int timeoutSeconds = 30, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var result = await Cli.Wrap("docker")
                .WithArguments(["exec", containerId, "bash", "-c", command])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cts.Token);

            return new SandboxExecResult
            {
                Success   = result.ExitCode == 0,
                Stdout    = result.StandardOutput,
                Stderr    = result.StandardError,
                ExitCode  = result.ExitCode,
            };
        }
        catch (OperationCanceledException)
        {
            return new SandboxExecResult
            {
                Success  = false,
                Stdout   = string.Empty,
                Stderr   = $"Command timed out after {timeoutSeconds}s",
                ExitCode = -1,
                Error    = "Timeout",
            };
        }
        catch (Exception ex)
        {
            return new SandboxExecResult
            {
                Success  = false,
                Stdout   = string.Empty,
                Stderr   = ex.Message,
                ExitCode = -1,
                Error    = ex.Message,
            };
        }
    }

    /// <inheritdoc/>
    public async Task<bool> IsRunningAsync(string containerId, CancellationToken ct = default)
    {
        try
        {
            var result = await Cli.Wrap("docker")
                .WithArguments(["inspect", "--format={{.State.Running}}", containerId])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(ct);

            return result.StandardOutput.Trim() == "true";
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task RemoveAsync(string containerId, CancellationToken ct = default)
    {
        try
        {
            await Cli.Wrap("docker")
                .WithArguments(["rm", "-f", containerId])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(ct);

            _logger.LogInformation("[DockerSandbox] Removed container={Id}", Short(containerId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DockerSandbox] RemoveAsync failed container={Id}", Short(containerId));
        }
    }

    // ── 辅助方法 ─────────────────────────────────────────────────────────

    private static string SanitizeId(string id, int maxLen) =>
        new string(id.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray())
            .ToLowerInvariant()[..Math.Min(maxLen, id.Length)];

    private static string Short(string id) =>
        id.Length >= 12 ? id[..12] : id;
}

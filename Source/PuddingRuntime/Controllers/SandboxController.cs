using Microsoft.AspNetCore.Mvc;
using PuddingRuntime.Services.Sandbox;

namespace PuddingRuntime.Controllers;

/// <summary>
/// Sandbox 管理 API——对外暴露 Agent 容器的启动、停止、执行和清理操作。
/// </summary>
[ApiController]
[Route("api/sandbox")]
public class SandboxController : ControllerBase
{
    private readonly AgentContainerRegistry _registry;
    private readonly ISandboxProvider _sandbox;
    private readonly ILogger<SandboxController> _logger;

    public SandboxController(
        AgentContainerRegistry registry,
        ISandboxProvider sandbox,
        ILogger<SandboxController> logger)
    {
        _registry = registry;
        _sandbox  = sandbox;
        _logger   = logger;
    }

    /// <summary>GET /api/sandbox — 列出所有绑定记录。</summary>
    [HttpGet]
    public ActionResult<IReadOnlyList<AgentContainerBinding>> ListAll() =>
        Ok(_registry.GetAll());

    /// <summary>GET /api/sandbox/agent/{agentInstanceId}</summary>
    [HttpGet("agent/{agentInstanceId}")]
    public ActionResult<AgentContainerBinding> GetByAgent(string agentInstanceId)
    {
        var binding = _registry.GetByAgent(agentInstanceId);
        return binding is null ? NotFound() : Ok(binding);
    }

    /// <summary>GET /api/sandbox/workspace/{workspaceId}</summary>
    [HttpGet("workspace/{workspaceId}")]
    public ActionResult<IReadOnlyList<AgentContainerBinding>> GetByWorkspace(string workspaceId) =>
        Ok(_registry.GetByWorkspace(workspaceId));

    /// <summary>
    /// POST /api/sandbox/agent/{agentInstanceId}/start
    /// 为指定 Agent 启动或复用 Docker 容器。
    /// </summary>
    [HttpPost("agent/{agentInstanceId}/start")]
    public async Task<ActionResult<SandboxStartResult>> StartContainer(
        string agentInstanceId,
        [FromBody] StartContainerRequest req,
        CancellationToken ct)
    {
        // 如已有运行中容器则直接复用
        var existing = _registry.GetByAgent(agentInstanceId);
        if (existing is { Status: AgentContainerStatus.Running })
        {
            if (await _sandbox.IsRunningAsync(existing.ContainerId, ct))
            {
                _logger.LogInformation("[SandboxAPI] Reuse existing container for agent={Agent}", agentInstanceId);
                return Ok(new SandboxStartResult
                {
                    Success       = true,
                    ContainerId   = existing.ContainerId,
                    ContainerName = existing.ContainerName,
                });
            }
            // 容器已意外退出，更新状态后重新启动
            _registry.UpdateStatus(agentInstanceId, AgentContainerStatus.Error, "Container exited unexpectedly");
        }

        var image = req.Image ?? "ubuntu:22.04";
        var result = await _sandbox.StartAsync(new SandboxStartRequest
        {
            AgentInstanceId = agentInstanceId,
            WorkspaceId     = req.WorkspaceId,
            Image           = image,
        }, ct);

        if (result.Success)
        {
            var binding = new AgentContainerBinding
            {
                AgentInstanceId = agentInstanceId,
                WorkspaceId     = req.WorkspaceId,
                ContainerId     = result.ContainerId!,
                ContainerName   = result.ContainerName!,
                Image           = image,
                Status          = AgentContainerStatus.Running,
            };
            _registry.Register(binding);
            var cid = result.ContainerId ?? string.Empty;
            _logger.LogInformation("[SandboxAPI] Started container for agent={Agent} id={Id}",
                agentInstanceId, cid.Length >= 12 ? cid[..12] : cid);
        }

        return Ok(result);
    }

    /// <summary>
    /// POST /api/sandbox/agent/{agentInstanceId}/stop
    /// </summary>
    [HttpPost("agent/{agentInstanceId}/stop")]
    public async Task<ActionResult<SandboxStopResult>> StopContainer(
        string agentInstanceId, CancellationToken ct)
    {
        var binding = _registry.GetByAgent(agentInstanceId);
        if (binding is null) return NotFound("No container binding found for agent.");

        var result = await _sandbox.StopAsync(binding.ContainerId, ct);
        if (result.Success)
            _registry.UpdateStatus(agentInstanceId, AgentContainerStatus.Stopped);

        return Ok(result);
    }

    /// <summary>
    /// POST /api/sandbox/agent/{agentInstanceId}/exec
    /// 在 Agent 容器内执行 bash 命令。
    /// </summary>
    [HttpPost("agent/{agentInstanceId}/exec")]
    public async Task<ActionResult<SandboxExecResult>> ExecInContainer(
        string agentInstanceId,
        [FromBody] ExecRequest req,
        CancellationToken ct)
    {
        var binding = _registry.GetByAgent(agentInstanceId);
        if (binding is null)
            return NotFound("No container binding found for agent.");

        if (binding.Status != AgentContainerStatus.Running)
            return BadRequest($"Container is not running (status: {binding.Status}).");

        var result = await _sandbox.ExecAsync(binding.ContainerId, req.Command,
            req.TimeoutSeconds ?? 30, ct);

        if (result.Error == "Timeout")
            _logger.LogWarning("[SandboxAPI] Exec timeout agent={Agent} cmd={Cmd}",
                agentInstanceId, req.Command[..Math.Min(60, req.Command.Length)]);

        return Ok(result);
    }

    /// <summary>
    /// DELETE /api/sandbox/agent/{agentInstanceId}
    /// 停止并删除 Agent 容器。
    /// </summary>
    [HttpDelete("agent/{agentInstanceId}")]
    public async Task<IActionResult> RemoveContainer(string agentInstanceId, CancellationToken ct)
    {
        var binding = _registry.GetByAgent(agentInstanceId);
        if (binding is null) return NotFound();

        await _sandbox.StopAsync(binding.ContainerId, ct);
        await _sandbox.RemoveAsync(binding.ContainerId, ct);
        _registry.UpdateStatus(agentInstanceId, AgentContainerStatus.Removed);
        _registry.Remove(agentInstanceId);

        _logger.LogInformation("[SandboxAPI] Removed container for agent={Agent}", agentInstanceId);
        return NoContent();
    }

    /// <summary>
    /// POST /api/sandbox/workspace/{workspaceId}/stop-all
    /// 停止某 Workspace 下所有 Agent 容器。
    /// </summary>
    [HttpPost("workspace/{workspaceId}/stop-all")]
    public async Task<ActionResult<StopAllResult>> StopAllInWorkspace(
        string workspaceId, CancellationToken ct)
    {
        var bindings = _registry.GetByWorkspace(workspaceId);
        var stopped = 0;
        var errors  = new List<string>();

        foreach (var b in bindings.Where(b => b.Status == AgentContainerStatus.Running))
        {
            var r = await _sandbox.StopAsync(b.ContainerId, ct);
            if (r.Success)
            {
                _registry.UpdateStatus(b.AgentInstanceId, AgentContainerStatus.Stopped);
                stopped++;
            }
            else
            {
                errors.Add($"agent={b.AgentInstanceId}: {r.Error}");
            }
        }

        _logger.LogInformation("[SandboxAPI] StopAll workspace={Ws} stopped={N}", workspaceId, stopped);
        return Ok(new StopAllResult { Stopped = stopped, Errors = errors });
    }
}

// ── 请求/响应 DTO ─────────────────────────────────────────────────────

public sealed record StartContainerRequest
{
    public required string WorkspaceId { get; init; }
    public string? Image { get; init; }
}

public sealed record ExecRequest
{
    public required string Command { get; init; }
    public int? TimeoutSeconds { get; init; }
}

public sealed record StopAllResult
{
    public required int Stopped { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }
}

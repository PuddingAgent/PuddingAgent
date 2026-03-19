namespace PuddingRuntime.Services.Sandbox;

/// <summary>
/// 沙箱提供者抽象——隔离具体沙箱实现（Docker / 本地进程 / 远程）。
/// </summary>
public interface ISandboxProvider
{
    /// <summary>提供者类型标识，如 "docker"。</summary>
    string ProviderType { get; }

    /// <summary>为 Agent 启动一个新沙箱容器。</summary>
    Task<SandboxStartResult> StartAsync(SandboxStartRequest request, CancellationToken ct = default);

    /// <summary>停止指定容器（不删除）。</summary>
    Task<SandboxStopResult> StopAsync(string containerId, CancellationToken ct = default);

    /// <summary>在指定容器内执行 bash 命令。</summary>
    Task<SandboxExecResult> ExecAsync(string containerId, string command,
        int timeoutSeconds = 30, CancellationToken ct = default);

    /// <summary>检查容器是否处于运行状态。</summary>
    Task<bool> IsRunningAsync(string containerId, CancellationToken ct = default);

    /// <summary>强制删除容器（stop + rm）。</summary>
    Task RemoveAsync(string containerId, CancellationToken ct = default);
}

// ── 请求/结果对象 ──────────────────────────────────────────────────

public sealed record SandboxStartRequest
{
    public required string AgentInstanceId { get; init; }
    public required string WorkspaceId { get; init; }
    public string Image { get; init; } = "ubuntu:22.04";
    public string? ContainerName { get; init; }
    public IReadOnlyDictionary<string, string> EnvironmentVars { get; init; }
        = new Dictionary<string, string>();
    /// <summary>挂载路径，格式 "host:container" 或 "host:container:ro"。</summary>
    public IReadOnlyList<string> Mounts { get; init; } = [];
    public string? WorkingDirectory { get; init; }
}

public sealed record SandboxStartResult
{
    public required bool Success { get; init; }
    public string? ContainerId { get; init; }
    public string? ContainerName { get; init; }
    public string? Error { get; init; }
}

public sealed record SandboxStopResult
{
    public required bool Success { get; init; }
    public string? Error { get; init; }
}

public sealed record SandboxExecResult
{
    public required bool Success { get; init; }
    public required string Stdout { get; init; }
    public required string Stderr { get; init; }
    public required int ExitCode { get; init; }
    public string? Error { get; init; }
}

namespace PuddingCode.Abstractions;

/// <summary>
/// 终端进程管理器——管理独立于 Agent/前端连接的 OS 级进程。
/// 进程与会话生命周期解耦：前端断开后进程继续运行，重连后可查看/终止。
/// </summary>
public interface ITerminalProcessManager
{
    /// <summary>启动进程，返回进程信息（含 ProcessId）。</summary>
    Task<TerminalProcessInfo> StartAsync(
        string sessionId, string command, string workingDir,
        CancellationToken ct = default);

    /// <summary>订阅进程实时输出（逐行 yield），用于 SSE 推送。</summary>
    IAsyncEnumerable<string> SubscribeAsync(string processId, CancellationToken ct = default);

    /// <summary>终止进程（包括整个进程树）。</summary>
    Task<bool> KillAsync(string processId);

    /// <summary>列出进程。可按 sessionId 过滤；不传则返回全部。</summary>
    IReadOnlyList<TerminalProcessInfo> ListProcesses(string? sessionId = null);

    /// <summary>清理已退出的僵尸进程记录，返回清理数量。</summary>
    Task<int> ReapAsync();
}

/// <summary>终端进程信息快照。</summary>
public sealed record TerminalProcessInfo
{
    public string ProcessId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public string WorkingDir { get; init; } = string.Empty;
    public DateTimeOffset StartedAt { get; init; }
    public int? ExitCode { get; init; }
    public TerminalProcessStatus Status { get; init; }
}

/// <summary>终端进程状态。</summary>
public enum TerminalProcessStatus
{
    Running,
    Exited,
    Killed,
    Failed
}

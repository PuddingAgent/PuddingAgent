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

    /// <summary>读取进程已缓冲输出。offset 为 0-based 行号，用于 Agent 轮询增量输出。</summary>
    Task<TerminalOutputSnapshot?> ReadOutputAsync(
        string processId,
        int offset = 0,
        int? maxLines = null,
        int? maxChars = null,
        CancellationToken ct = default);

    /// <summary>向仍在运行的进程写入标准输入。</summary>
    Task<bool> WriteInputAsync(string processId, string input, CancellationToken ct = default);

    /// <summary>终止进程（包括整个进程树）。</summary>
    Task<bool> KillAsync(string processId);

    /// <summary>列出进程。可按 sessionId 过滤；不传则返回全部。</summary>
    IReadOnlyList<TerminalProcessInfo> ListProcesses(string? sessionId = null);

    /// <summary>清理已退出的僵尸进程记录，返回清理数量。</summary>
    Task<int> ReapAsync();
}

/// <summary>终端进程输出快照。</summary>
public sealed record TerminalOutputSnapshot
{
    public required TerminalProcessInfo Process { get; init; }
    public int Offset { get; init; }
    public int NextOffset { get; init; }
    public int TotalLines { get; init; }
    public bool Truncated { get; init; }
    public IReadOnlyList<string> Lines { get; init; } = [];
}

/// <summary>终端进程信息快照。</summary>
public sealed record TerminalProcessInfo
{
    /// <summary>Pudding 内部 terminal job id，用于 terminal_wait / terminal_status / terminal_cancel。</summary>
    public string ProcessId { get; init; } = string.Empty;
    /// <summary>真实操作系统进程号。进程启动失败或不可用时为空。</summary>
    public int? OsProcessId { get; init; }
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

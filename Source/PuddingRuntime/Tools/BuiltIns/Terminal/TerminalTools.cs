using System.Text.Json;
using System.Text.Json.Serialization;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

internal static class TerminalToolJson
{
    public const int DefaultPreviewLines = 120;
    public const int DefaultPreviewChars = 4_000;
    public const int DefaultReadLines = 500;
    public const int DefaultReadChars = 40_000;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);

    public static TerminalJobDto ToJobDto(TerminalProcessInfo info) => new()
    {
        JobId = info.ProcessId,
        ProcessId = info.ProcessId,
        OsProcessId = info.OsProcessId,
        SessionId = info.SessionId,
        Command = info.Command,
        Cwd = info.WorkingDir,
        StartedAt = info.StartedAt,
        Status = info.Status.ToString(),
        ExitCode = info.ExitCode,
    };

    public static TerminalOutputDto ToOutputDto(TerminalOutputSnapshot snapshot) => new()
    {
        Job = ToJobDto(snapshot.Process),
        Offset = snapshot.Offset,
        NextOffset = snapshot.NextOffset,
        TotalLines = snapshot.TotalLines,
        Truncated = snapshot.Truncated,
        CommandFailed = IsCommandFailed(snapshot.Process),
        Output = string.Join(Environment.NewLine, snapshot.Lines),
        Lines = snapshot.Lines,
        Handle = snapshot.Truncated ? ToOutputHandle(snapshot) : null,
        Recovery = ToRecoveryDto(snapshot.Process),
    };

    public static TerminalOutputHandleDto ToOutputHandle(TerminalOutputSnapshot snapshot) => new()
    {
        Kind = "terminal_output",
        JobId = snapshot.Process.ProcessId,
        Offset = snapshot.Offset,
        NextOffset = snapshot.NextOffset,
        TotalLines = snapshot.TotalLines,
        ReadTool = "terminal_read",
        ReadArgs = new TerminalReadArgsDto
        {
            JobId = snapshot.Process.ProcessId,
            FromOffset = snapshot.NextOffset,
        },
    };

    public static TerminalProcessInfo? FindJob(
        ITerminalProcessManager manager,
        ToolExecutionContext context,
        string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return null;

        return manager.ListProcesses(context.SessionId)
            .FirstOrDefault(p => p.ProcessId.Equals(jobId.Trim(), StringComparison.Ordinal));
    }

    public static string NextAction(TerminalOutputSnapshot snapshot, string runningAction, string completedAction)
    {
        if (snapshot.Truncated)
            return "Output was truncated. Use terminal_read with handle.read_args to read the next slice without rerunning the command.";

        if (IsCommandFailed(snapshot.Process))
            return "Command exited with a non-zero exit_code. Do not blindly rerun the same command unchanged. Diagnose the output first; rerun only when retry/restart is intentional or after changing command, inputs, cwd, environment, or timing.";

        return snapshot.Process.Status == TerminalProcessStatus.Running
            ? runningAction
            : completedAction;
    }

    private static bool IsCommandFailed(TerminalProcessInfo process)
        => process.Status == TerminalProcessStatus.Failed
        || process.ExitCode is int exitCode && exitCode != 0;

    private static TerminalRecoveryDto? ToRecoveryDto(TerminalProcessInfo process)
    {
        if (!IsCommandFailed(process))
            return null;

        return new TerminalRecoveryDto
        {
            BlindRerunSameCommand = false,
            RepeatSameCommandRequiresReason = true,
            Reason = "The terminal command failed. Repeating the identical command without new information is unlikely to make progress.",
            Instruction = "Explain the failure from the output. Retry the same command only when the task requires a restart/retry or state may have changed; otherwise correct the command or inputs, or stop with FAILED if blocked.",
        };
    }
}

/// <summary>Starts a terminal command as a background job and returns immediately.</summary>
[Tool(
    id: "terminal_start",
    name: "Terminal start",
    description: "以后台终端任务方式启动 shell 命令并立即返回。适用于构建、测试、搜索、服务器等可能耗时数秒以上的命令，随后用 terminal_wait 轮询。Start a shell command as a background terminal job and return immediately",
    category: ToolCategory.Execute,
    permission: ToolPermissionLevel.High,
    safety: ToolSafetyFlags.RequiresShell,
    SortOrder = 30)]
public sealed class TerminalStartTool : PuddingToolBase<TerminalStartArgs, TerminalStartResult>
{
    private readonly ITerminalProcessManager _processManager;
    private readonly ITerminalCommandPolicy _commandPolicy;
    private readonly ILogger<TerminalStartTool> _logger;

    public TerminalStartTool(
        ITerminalProcessManager processManager,
        ILogger<TerminalStartTool> logger,
        ITerminalCommandPolicy? commandPolicy = null)
    {
        _processManager = processManager;
        _commandPolicy = commandPolicy ?? DefaultTerminalCommandPolicy.Instance;
        _logger = logger;
    }

    protected override async Task<TerminalStartResult> ExecuteCoreAsync(
        TerminalStartArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Command))
            throw new InvalidOperationException("command is required.");

        try
        {
            _commandPolicy.EnsureAllowed(args.Command, context.IsYoloMode);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(
                "[TerminalStartTool] Security blocked session={Session} yolo={Yolo} cmd={Cmd}: {Reason}",
                context.SessionId,
                context.IsYoloMode,
                args.Command[..Math.Min(args.Command.Length, 100)],
                ex.Message);
            throw;
        }

                var cwd = string.IsNullOrWhiteSpace(args.Cwd)
            ? (context.WorkingDirectory ?? Directory.GetCurrentDirectory())
            : args.Cwd.Trim();

        if (!Directory.Exists(cwd))
            throw new InvalidOperationException($"working directory does not exist: {cwd}");

        TerminalProcessInfo info;
        try
        {
            info = await _processManager.StartAsync(context.SessionId, args.Command.Trim(), cwd, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[TerminalStartTool] Start failed session={Session} cwd={Cwd} cmd={Cmd}",
                context.SessionId,
                cwd,
                args.Command[..Math.Min(args.Command.Length, 100)]);
            throw new InvalidOperationException($"failed to start terminal job: {ex.Message}");
        }

        var snapshot = await _processManager.ReadOutputAsync(
            info.ProcessId,
            offset: 0,
            maxLines: args.MaxOutputLines ?? TerminalToolJson.DefaultPreviewLines,
            maxChars: args.MaxOutputChars ?? TerminalToolJson.DefaultPreviewChars,
            ct);

        return new TerminalStartResult
        {
            Job = TerminalToolJson.ToJobDto(info),
            Output = snapshot is null ? null : TerminalToolJson.ToOutputDto(snapshot),
            NextAction = snapshot is null
                ? "Use terminal_wait with job_id to poll incremental output. Do not block the agent loop waiting for this process."
                : TerminalToolJson.NextAction(
                    snapshot,
                    "Use terminal_wait with job_id to poll incremental output. Do not block the agent loop waiting for this process.",
                    "Job already completed. Use the exit_code and output to continue."),
        };
    }
}

/// <summary>Polls incremental terminal job output without owning the process lifetime.</summary>
[Tool(
    id: "terminal_wait",
    name: "Terminal wait",
    description: "轮询后台终端任务的增量输出。取消等待不会杀死任务，请用 terminal_cancel 停止任务。Poll a background terminal job for incremental output",
    category: ToolCategory.Execute,
    permission: ToolPermissionLevel.High,
    safety: ToolSafetyFlags.RequiresShell,
    SortOrder = 31)]
public sealed class TerminalWaitTool : PuddingToolBase<TerminalWaitArgs, TerminalWaitResult>
{
    private readonly ITerminalProcessManager _processManager;

    public TerminalWaitTool(ITerminalProcessManager processManager)
    {
        _processManager = processManager;
    }

    protected override async Task<TerminalWaitResult> ExecuteCoreAsync(
        TerminalWaitArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var job = TerminalToolJson.FindJob(_processManager, context, args.JobId);
        if (job is null)
            throw new InvalidOperationException($"terminal job not found in this session: {args.JobId}");

        var fromOffset = Math.Max(0, args.FromOffset ?? 0);
        var waitSeconds = Math.Clamp(args.WaitSeconds ?? 1, 0, 30);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(waitSeconds);
        TerminalOutputSnapshot? snapshot = null;

        do
        {
            snapshot = await _processManager.ReadOutputAsync(
                job.ProcessId,
                fromOffset,
                args.MaxLines ?? TerminalToolJson.DefaultPreviewLines,
                args.MaxChars ?? TerminalToolJson.DefaultPreviewChars,
                ct);

            if (snapshot is null)
                throw new InvalidOperationException($"terminal job disappeared before output could be read: {args.JobId}");

            if (snapshot.NextOffset > fromOffset || snapshot.Process.Status != TerminalProcessStatus.Running)
                break;

            if (DateTimeOffset.UtcNow >= deadline)
                break;

            await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
        } while (true);

        return new TerminalWaitResult
        {
            Result = TerminalToolJson.ToOutputDto(snapshot),
            NextAction = TerminalToolJson.NextAction(
                snapshot,
                "Job is still running. Call terminal_wait again with from_offset set to next_offset for more output, or terminal_cancel to stop it.",
                "Job is no longer running. Use the exit_code and output to continue."),
        };
    }
}

/// <summary>Reads a terminal output slice without waiting for new process output.</summary>
[Tool(
    id: "terminal_read",
    name: "Terminal read",
    description: "按 job_id 和 from_offset 读取缓冲的终端输出切片。当 terminal_wait 返回截断句柄时使用。Read a slice of buffered terminal output by job_id and from_offset",
    category: ToolCategory.Execute,
    permission: ToolPermissionLevel.High,
    safety: ToolSafetyFlags.RequiresShell,
    SortOrder = 32)]
public sealed class TerminalReadTool : PuddingToolBase<TerminalReadArgs, TerminalWaitResult>
{
    private readonly ITerminalProcessManager _processManager;

    public TerminalReadTool(ITerminalProcessManager processManager)
    {
        _processManager = processManager;
    }

    protected override async Task<TerminalWaitResult> ExecuteCoreAsync(
        TerminalReadArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var job = TerminalToolJson.FindJob(_processManager, context, args.JobId);
        if (job is null)
            throw new InvalidOperationException($"terminal job not found in this session: {args.JobId}");

        var snapshot = await _processManager.ReadOutputAsync(
            job.ProcessId,
            Math.Max(0, args.FromOffset ?? 0),
            args.MaxLines ?? TerminalToolJson.DefaultReadLines,
            args.MaxChars ?? TerminalToolJson.DefaultReadChars,
            ct);

        if (snapshot is null)
            throw new InvalidOperationException($"terminal job disappeared before output could be read: {args.JobId}");

        return new TerminalWaitResult
        {
            Result = TerminalToolJson.ToOutputDto(snapshot),
            NextAction = TerminalToolJson.NextAction(
                snapshot,
                "Buffered output slice is complete for now. Use terminal_wait to wait for future output.",
                "Buffered output slice is complete and the job is no longer running."),
        };
    }
}

/// <summary>Lists terminal job status for the current session.</summary>
[Tool(
    id: "terminal_status",
    name: "Terminal status",
    description: "列出当前会话的后台终端任务，或按 job_id 检查单个任务。List background terminal jobs for this session, or inspect one job by job_id",
    category: ToolCategory.Execute,
    permission: ToolPermissionLevel.High,
    safety: ToolSafetyFlags.RequiresShell,
    SortOrder = 33)]
public sealed class TerminalStatusTool : PuddingToolBase<TerminalStatusArgs>
{
    private readonly ITerminalProcessManager _processManager;

    public TerminalStatusTool(ITerminalProcessManager processManager)
    {
        _processManager = processManager;
    }

    protected override Task<ToolExecutionResult> ExecuteCoreAsync(
        TerminalStatusArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var jobs = _processManager.ListProcesses(context.SessionId);
        if (!string.IsNullOrWhiteSpace(args.JobId))
            jobs = jobs
                .Where(p => p.ProcessId.Equals(args.JobId.Trim(), StringComparison.Ordinal))
                .ToList();

        return Task.FromResult(ToolExecutionResult.Ok(TerminalToolJson.Serialize(new
        {
            jobs = jobs.Select(TerminalToolJson.ToJobDto).ToList(),
            count = jobs.Count,
        })));
    }
}

/// <summary>Cancels a running terminal job.</summary>
[Tool(
    id: "terminal_cancel",
    name: "Terminal cancel",
    description: "按 job_id 取消正在运行的后台终端任务。【何时用】terminal_start 启动的后台任务失控/卡死或确认不再需要运行时，真正终止它时使用；注意 terminal_wait 超时或取消只是「停止等待」，并不会杀掉任务。【怎么用】传 job_id（terminal_start 返回值或 terminal_status 查询结果）；取消后用 terminal_status 确认任务已退出。【坑】是强杀，未保存进度会丢失；job 必须属于当前会话，否则报 not found；先 terminal_status 确认 job_id 再取消，避免误杀。Cancel a running background terminal job by job_id — use to truly kill a runaway/hung job (terminal_wait cancellation only stops polling, it does NOT kill the job); pass job_id from terminal_start/terminal_status and confirm with terminal_status afterwards; kill is forceful (unsaved work lost) and job_ids outside the current session are rejected.",
    category: ToolCategory.Execute,
    permission: ToolPermissionLevel.High,
    safety: ToolSafetyFlags.RequiresShell | ToolSafetyFlags.Destructive,
    SortOrder = 34)]
public sealed class TerminalCancelTool : PuddingToolBase<TerminalCancelArgs>
{
    private readonly ITerminalProcessManager _processManager;

    public TerminalCancelTool(ITerminalProcessManager processManager)
    {
        _processManager = processManager;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        TerminalCancelArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var job = TerminalToolJson.FindJob(_processManager, context, args.JobId);
        if (job is null)
            return ToolExecutionResult.Fail($"terminal job not found in this session: {args.JobId}");

        var cancelled = await _processManager.KillAsync(job.ProcessId);
        return ToolExecutionResult.Ok(TerminalToolJson.Serialize(new
        {
            job_id = job.ProcessId,
            cancelled,
        }));
    }
}

/// <summary>Sends standard input to a running terminal job.</summary>
[Tool(
    id: "terminal_input",
    name: "Terminal input",
    description: "向运行中的后台终端任务发送一行标准输入（stdin）。【何时用】后台命令在等待交互输入（y/n 确认、提示符、脚本要求输入时）时，向其发送应答时使用。【怎么用】传 job_id（terminal_start 返回或 terminal_status 查询）+ input（要发送的一行文本）；发送后用 terminal_read/terminal_wait 看任务响应。【坑】只发送一行（自动带换行），不支持多行粘贴与方向键/Ctrl 序列等复杂交互；输入会进入进程 stdin 并可能被记录，不要发送密钥等敏感内容；任务已退出或 job 不属于当前会话会报 not found。Send one line of stdin to a running background terminal job by job_id — use to answer interactive prompts (y/n confirmations, scripted input) of a background job; pass job_id + input, then read terminal_read/terminal_wait for the response; a single newline-terminated line only (no multi-line paste, arrow keys, or Ctrl sequences), input may be logged so avoid secrets, and dead or other-session jobs are rejected.",
    category: ToolCategory.Execute,
    permission: ToolPermissionLevel.High,
    safety: ToolSafetyFlags.RequiresShell,
    SortOrder = 35)]
public sealed class TerminalInputTool : PuddingToolBase<TerminalInputArgs>
{
    private readonly ITerminalProcessManager _processManager;

    public TerminalInputTool(ITerminalProcessManager processManager)
    {
        _processManager = processManager;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        TerminalInputArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var job = TerminalToolJson.FindJob(_processManager, context, args.JobId);
        if (job is null)
            return ToolExecutionResult.Fail($"terminal job not found in this session: {args.JobId}");

        var sent = await _processManager.WriteInputAsync(job.ProcessId, args.Input ?? string.Empty, ct);
        return ToolExecutionResult.Ok(TerminalToolJson.Serialize(new
        {
            job_id = job.ProcessId,
            sent,
        }));
    }
}

public sealed record TerminalStartArgs
{
    [ToolParam("Command line to start as a background terminal job.")]
    public required string Command { get; init; }

    [ToolParam("Working directory. Default: current runtime directory.")]
    public string? Cwd { get; init; }

    [ToolParam("Maximum output lines to include from the immediate start snapshot. Default: 200.")]
    public int? MaxOutputLines { get; init; }

    [ToolParam("Maximum output characters to include from the immediate start snapshot. Default: 20000.")]
    public int? MaxOutputChars { get; init; }
}

public sealed record TerminalWaitArgs
{
    [ToolParam("Terminal job id returned by terminal_start or terminal_execute.")]
    public required string JobId { get; init; }

    [ToolParam("0-based output line offset to read from. Use next_offset from the previous result.")]
    public int? FromOffset { get; init; }

    [ToolParam("Maximum seconds to wait for new output before returning. Range: 0-30. Default: 1.")]
    public int? WaitSeconds { get; init; }

    [ToolParam("Maximum output lines to return. Default: 200.")]
    public int? MaxLines { get; init; }

    [ToolParam("Maximum output characters to return. Default: 20000.")]
    public int? MaxChars { get; init; }
}

public sealed record TerminalStatusArgs
{
    [ToolParam("Optional terminal job id to inspect. When omitted, lists jobs in the current session.")]
    public string? JobId { get; init; }
}

public sealed record TerminalReadArgs
{
    [ToolParam("Terminal job id returned by terminal_start or terminal_execute.")]
    public required string JobId { get; init; }

    [ToolParam("0-based output line offset to read from. Use result.next_offset from terminal_wait or terminal_read.")]
    public int? FromOffset { get; init; }

    [ToolParam("Maximum output lines to return. Default: 500.")]
    public int? MaxLines { get; init; }

    [ToolParam("Maximum output characters to return. Default: 40000.")]
    public int? MaxChars { get; init; }
}

public sealed record TerminalCancelArgs
{
    [ToolParam("Terminal job id to cancel.")]
    public required string JobId { get; init; }
}

public sealed record TerminalInputArgs
{
    [ToolParam("Terminal job id to write to.")]
    public required string JobId { get; init; }

    [ToolParam("One line of stdin to send to the running job.")]
    public string? Input { get; init; }
}

public sealed record TerminalJobDto
{
    [JsonPropertyName("job_id")]
    public required string JobId { get; init; }

    [JsonPropertyName("process_id")]
    public required string ProcessId { get; init; }

    [JsonPropertyName("os_process_id")]
    public int? OsProcessId { get; init; }

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("cwd")]
    public required string Cwd { get; init; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset StartedAt { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("exit_code")]
    public int? ExitCode { get; init; }
}

public sealed record TerminalOutputDto
{
    [JsonPropertyName("job")]
    public required TerminalJobDto Job { get; init; }

    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    [JsonPropertyName("next_offset")]
    public int NextOffset { get; init; }

    [JsonPropertyName("total_lines")]
    public int TotalLines { get; init; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    [JsonPropertyName("command_failed")]
    public bool CommandFailed { get; init; }

    [JsonPropertyName("output")]
    public required string Output { get; init; }

    [JsonPropertyName("lines")]
    public required IReadOnlyList<string> Lines { get; init; }

    [JsonPropertyName("handle")]
    public TerminalOutputHandleDto? Handle { get; init; }

    [JsonPropertyName("recovery")]
    public TerminalRecoveryDto? Recovery { get; init; }
}

public sealed record TerminalOutputHandleDto
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("job_id")]
    public required string JobId { get; init; }

    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    [JsonPropertyName("next_offset")]
    public int NextOffset { get; init; }

    [JsonPropertyName("total_lines")]
    public int TotalLines { get; init; }

    [JsonPropertyName("read_tool")]
    public required string ReadTool { get; init; }

    [JsonPropertyName("read_args")]
    public required TerminalReadArgsDto ReadArgs { get; init; }
}

public sealed record TerminalReadArgsDto
{
    [JsonPropertyName("job_id")]
    public required string JobId { get; init; }

    [JsonPropertyName("from_offset")]
    public int FromOffset { get; init; }
}

public sealed record TerminalRecoveryDto
{
    [JsonPropertyName("blind_rerun_same_command")]
    public bool BlindRerunSameCommand { get; init; }

    [JsonPropertyName("repeat_same_command_requires_reason")]
    public bool RepeatSameCommandRequiresReason { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("instruction")]
    public required string Instruction { get; init; }
}

public sealed record TerminalStartResult
{
    [JsonPropertyName("job")]
    public required TerminalJobDto Job { get; init; }

    [JsonPropertyName("output")]
    public TerminalOutputDto? Output { get; init; }

    [JsonPropertyName("next_action")]
    public required string NextAction { get; init; }
}

public sealed record TerminalWaitResult
{
    [JsonPropertyName("result")]
    public required TerminalOutputDto Result { get; init; }

    [JsonPropertyName("next_action")]
    public required string NextAction { get; init; }
}

using System.Text.Json;
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

    public static object ToJobDto(TerminalProcessInfo info) => new
    {
        job_id = info.ProcessId,
        process_id = info.ProcessId,
        os_process_id = info.OsProcessId,
        session_id = info.SessionId,
        command = info.Command,
        cwd = info.WorkingDir,
        started_at = info.StartedAt,
        status = info.Status.ToString(),
        exit_code = info.ExitCode,
    };

    public static object ToOutputDto(TerminalOutputSnapshot snapshot) => new
    {
        job = ToJobDto(snapshot.Process),
        offset = snapshot.Offset,
        next_offset = snapshot.NextOffset,
        total_lines = snapshot.TotalLines,
        truncated = snapshot.Truncated,
        command_failed = IsCommandFailed(snapshot.Process),
        output = string.Join(Environment.NewLine, snapshot.Lines),
        lines = snapshot.Lines,
        handle = snapshot.Truncated ? ToOutputHandle(snapshot) : null,
        recovery = ToRecoveryDto(snapshot.Process),
    };

    public static object ToOutputHandle(TerminalOutputSnapshot snapshot) => new
    {
        kind = "terminal_output",
        job_id = snapshot.Process.ProcessId,
        offset = snapshot.Offset,
        next_offset = snapshot.NextOffset,
        total_lines = snapshot.TotalLines,
        read_tool = "terminal_read",
        read_args = new
        {
            job_id = snapshot.Process.ProcessId,
            from_offset = snapshot.NextOffset,
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

    private static object? ToRecoveryDto(TerminalProcessInfo process)
    {
        if (!IsCommandFailed(process))
            return null;

        return new
        {
            blind_rerun_same_command = false,
            repeat_same_command_requires_reason = true,
            reason = "The terminal command failed. Repeating the identical command without new information is unlikely to make progress.",
            instruction = "Explain the failure from the output. Retry the same command only when the task requires a restart/retry or state may have changed; otherwise correct the command or inputs, or stop with FAILED if blocked.",
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
public sealed class TerminalStartTool : PuddingToolBase<TerminalStartArgs>
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

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        TerminalStartArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Command))
            return ToolExecutionResult.Fail("command is required.");

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
            return ToolExecutionResult.Fail(ex.Message);
        }

                var cwd = string.IsNullOrWhiteSpace(args.Cwd)
            ? (context.WorkingDirectory ?? Directory.GetCurrentDirectory())
            : args.Cwd.Trim();

        if (!Directory.Exists(cwd))
            return ToolExecutionResult.Fail($"working directory does not exist: {cwd}");

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
            return ToolExecutionResult.Fail($"failed to start terminal job: {ex.Message}");
        }

        var snapshot = await _processManager.ReadOutputAsync(
            info.ProcessId,
            offset: 0,
            maxLines: args.MaxOutputLines ?? TerminalToolJson.DefaultPreviewLines,
            maxChars: args.MaxOutputChars ?? TerminalToolJson.DefaultPreviewChars,
            ct);

        return ToolExecutionResult.Ok(TerminalToolJson.Serialize(new
        {
            job = TerminalToolJson.ToJobDto(info),
            output = snapshot is null ? null : TerminalToolJson.ToOutputDto(snapshot),
            next_action = snapshot is null
                ? "Use terminal_wait with job_id to poll incremental output. Do not block the agent loop waiting for this process."
                : TerminalToolJson.NextAction(
                    snapshot,
                    "Use terminal_wait with job_id to poll incremental output. Do not block the agent loop waiting for this process.",
                    "Job already completed. Use the exit_code and output to continue."),
        }));
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
public sealed class TerminalWaitTool : PuddingToolBase<TerminalWaitArgs>
{
    private readonly ITerminalProcessManager _processManager;

    public TerminalWaitTool(ITerminalProcessManager processManager)
    {
        _processManager = processManager;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        TerminalWaitArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var job = TerminalToolJson.FindJob(_processManager, context, args.JobId);
        if (job is null)
            return ToolExecutionResult.Fail($"terminal job not found in this session: {args.JobId}");

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
                return ToolExecutionResult.Fail($"terminal job disappeared before output could be read: {args.JobId}");

            if (snapshot.NextOffset > fromOffset || snapshot.Process.Status != TerminalProcessStatus.Running)
                break;

            if (DateTimeOffset.UtcNow >= deadline)
                break;

            await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
        } while (true);

        return ToolExecutionResult.Ok(TerminalToolJson.Serialize(new
        {
            result = TerminalToolJson.ToOutputDto(snapshot),
            next_action = TerminalToolJson.NextAction(
                snapshot,
                "Job is still running. Call terminal_wait again with from_offset set to next_offset for more output, or terminal_cancel to stop it.",
                "Job is no longer running. Use the exit_code and output to continue."),
        }));
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
public sealed class TerminalReadTool : PuddingToolBase<TerminalReadArgs>
{
    private readonly ITerminalProcessManager _processManager;

    public TerminalReadTool(ITerminalProcessManager processManager)
    {
        _processManager = processManager;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        TerminalReadArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var job = TerminalToolJson.FindJob(_processManager, context, args.JobId);
        if (job is null)
            return ToolExecutionResult.Fail($"terminal job not found in this session: {args.JobId}");

        var snapshot = await _processManager.ReadOutputAsync(
            job.ProcessId,
            Math.Max(0, args.FromOffset ?? 0),
            args.MaxLines ?? TerminalToolJson.DefaultReadLines,
            args.MaxChars ?? TerminalToolJson.DefaultReadChars,
            ct);

        if (snapshot is null)
            return ToolExecutionResult.Fail($"terminal job disappeared before output could be read: {args.JobId}");

        return ToolExecutionResult.Ok(TerminalToolJson.Serialize(new
        {
            result = TerminalToolJson.ToOutputDto(snapshot),
            next_action = TerminalToolJson.NextAction(
                snapshot,
                "Buffered output slice is complete for now. Use terminal_wait to wait for future output.",
                "Buffered output slice is complete and the job is no longer running."),
        }));
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
    description: "按 job_id 取消正在运行的后台终端任务。Cancel a running background terminal job by job_id",
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
    description: "向运行中的后台终端任务发送一行标准输入（stdin）。Send one line of stdin to a running background terminal job by job_id",
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

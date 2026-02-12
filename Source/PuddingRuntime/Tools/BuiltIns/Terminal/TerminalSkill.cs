using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services;

/// <summary>
/// 终端执行工具——Agent 可调用的终端命令执行工具。
/// 不等待进程完成，立即返回 ProcessId。进程输出通过 SSE terminal 事件流式推送。
/// </summary>
[Tool(
    id: "terminal_execute",
    name: "终端执行",
    description: "在服务器上执行终端命令（dotnet/git/python/node/npm/ls/cat/echo/mkdir/curl 等安全命令）。命令在独立进程中运行，即使连接断开也继续执行。返回进程 PID，输出通过 SSE 实时推送。",
    category: ToolCategory.Shell,
    permission: ToolPermissionLevel.High)]
public sealed class TerminalSkill : PuddingToolBase<TerminalSkillArgs>
{
    private readonly ITerminalProcessManager _processManager;
    private readonly ITerminalCommandPolicy _commandPolicy;
    private readonly ILogger<TerminalSkill> _logger;

    public TerminalSkill(
        ITerminalProcessManager processManager,
        ILogger<TerminalSkill> logger,
        ITerminalCommandPolicy? commandPolicy = null)
    {
        _processManager = processManager;
        _commandPolicy = commandPolicy ?? DefaultTerminalCommandPolicy.Instance;
        _logger = logger;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        TerminalSkillArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        var command = args.Command?.Trim();
        if (string.IsNullOrWhiteSpace(command))
            return ToolExecutionResult.Fail("command 参数不能为空。");

        var workingDir = args.Cwd ?? Directory.GetCurrentDirectory();

        try { _commandPolicy.EnsureAllowed(command, context.IsYoloMode); }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(
                "[TerminalSkill] Security blocked session={Session} yolo={Yolo} cmd={Cmd}: {Reason}",
                context.SessionId,
                context.IsYoloMode,
                command[..Math.Min(command.Length, 100)],
                ex.Message);
            return ToolExecutionResult.Fail(ex.Message);
        }

        try
        {
            var info = await _processManager.StartAsync(context.SessionId, command, workingDir, ct);
            _logger.LogInformation("[TerminalSkill] Started pid={Pid} session={Session}", info.ProcessId, context.SessionId);
            return ToolExecutionResult.Ok(info.ProcessId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TerminalSkill] Start failed session={Session}", context.SessionId);
            return ToolExecutionResult.Fail($"启动进程失败: {ex.Message}");
        }
    }
}

public sealed record TerminalSkillArgs
{
    [ToolParam("Command line to execute.")]
    public string? Command { get; init; }
    [ToolParam("Working directory. Default: /workspace.")]
    public string? Cwd { get; init; }
}

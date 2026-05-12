using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntime.Services;

/// <summary>
/// 终端执行 Skill——Agent 可调用的终端命令执行工具。
/// 
/// 不等待进程完成，立即返回 ProcessId。进程输出通过 SSE terminal 事件流式推送。
/// 安全校验：调用前通过 TerminalSecurity.IsAllowed 进行白名单+危险模式双重校验。
/// </summary>
public sealed class TerminalSkill : IAgentSkill
{
    private readonly ITerminalProcessManager _processManager;
    private readonly ILogger<TerminalSkill> _logger;

    public string SkillId => "terminal_execute";
    public string Name => "终端执行";
    public string Description =>
        "在服务器上执行终端命令（dotnet/git/python/node/npm/docker/ls/cat/echo/mkdir/curl 等安全命令）。" +
        "命令在独立进程中运行，即使连接断开也继续执行。" +
        "参数：command（必填，完整命令行）、cwd（可选，工作目录，默认 /workspace）。" +
        "返回进程 PID，输出通过 SSE 实时推送。";
    public bool RequiresShellExecution => true;
    public ToolPermissionLevel PermissionLevel => ToolPermissionLevel.High;

    public TerminalSkill(ITerminalProcessManager processManager, ILogger<TerminalSkill> logger)
    {
        _processManager = processManager;
        _logger = logger;
    }

    public async Task<SkillResult> ExecuteAsync(SkillInvokeRequest request, CancellationToken ct = default)
    {
        var command = request.Input?.Trim();
        if (string.IsNullOrWhiteSpace(command))
            return Fail("command 参数不能为空。");

        // 解析 cwd 参数
        var workingDir = "/workspace";
        if (request.Parameters.TryGetValue("cwd", out var cwd) && !string.IsNullOrWhiteSpace(cwd))
            workingDir = cwd;

        // 安全校验（第二层白名单 + 第三层危险模式拦截）
        try
        {
            TerminalSecurity.IsAllowed(command);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning("[TerminalSkill] Security blocked session={Session} cmd={Cmd} reason={Reason}",
                request.SessionId, command[..Math.Min(command.Length, 100)], ex.Message);
            return Fail(ex.Message);
        }

        try
        {
            var info = await _processManager.StartAsync(
                request.SessionId, command, workingDir, ct);

            _logger.LogInformation(
                "[TerminalSkill] Started pid={Pid} session={Session} cmd={Cmd}",
                info.ProcessId, request.SessionId, command[..Math.Min(command.Length, 80)]);

            return new SkillResult
            {
                Success = true,
                Output = info.ProcessId,
                ExitCode = 0,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TerminalSkill] Start failed session={Session} cmd={Cmd}",
                request.SessionId, command[..Math.Min(command.Length, 80)]);
            return Fail($"启动进程失败: {ex.Message}");
        }
    }

    private static SkillResult Fail(string error) => new()
    {
        Success = false,
        Output = string.Empty,
        Error = error,
        ExitCode = -1,
    };
}

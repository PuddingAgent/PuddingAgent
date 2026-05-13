using System.Diagnostics;
using PuddingCode.Models;
using PuddingRuntime.Services.Sandbox;

namespace PuddingRuntime.Services.Skills;

/// <summary>
/// Python Skill——优先在 Agent 专属 Docker 容器内执行 Python 3 代码，Docker 不可用时降级为宿主机执行。
/// 代码通过 base64 管道传入，避免引号或特殊字符的转义问题。
/// </summary>
public sealed class PythonSkill : ContainerSkillBase
{
    private readonly ILogger<PythonSkill> _logger;

    public PythonSkill(
        AgentContainerRegistry registry,
        ISandboxProvider sandbox,
        AgentSkillPackageRegistry skillPackageRegistry,
        ILogger<PythonSkill> logger)
        : base(registry, sandbox, skillPackageRegistry, logger)
    {
        _logger = logger;
    }

    public override string SkillId => "python";
    public override string Name => "python";
    public override string Description =>
        "Execute Python 3 code inside the agent's isolated Docker container. " +
        "Returns stdout and stderr. Ideal for data analysis, math, text processing, and scripting.";
    public override bool RequiresShellExecution => true;
    public override ToolPermissionLevel PermissionLevel => ToolPermissionLevel.Medium;

    public override async Task<SkillResult> ExecuteAsync(SkillInvokeRequest request, CancellationToken ct = default)
    {
        // 尝试 Docker 沙箱模式
        try
        {
            var binding = await EnsureContainerRunningAsync(request.AgentInstanceId, request.WorkspaceId, ct);
            if (binding is not null)
            {
                // base64 encode the Python code to avoid shell escaping issues
                var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(request.Input));
                var cmd = $"echo '{encoded}' | base64 -d | python3";

                _logger.LogInformation("[PythonSkill] agent={Agent} container={CId} code_len={Len}",
                    request.AgentInstanceId, Short(binding.ContainerId), request.Input.Length);

                var exec = await Sandbox.ExecAsync(binding.ContainerId, cmd, timeoutSeconds: 30, ct);

                if (exec.ExitCode == -1 && exec.Error is not null)
                    Registry.UpdateStatus(request.AgentInstanceId, AgentContainerStatus.Error, exec.Error);

                var output = (exec.Stdout + (exec.Stderr.Length > 0 ? "\n[stderr]: " + exec.Stderr : "")).TrimEnd();

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
            _logger.LogWarning(ex, "[PythonSkill] Docker sandbox unavailable, falling back to host mode code_len={Len}",
                request.Input.Length);
        }

        // ═══ 降级：宿主模式 ═══
        return await ExecutePythonHostMode(request.Input, ct);
    }

    /// <summary>
    /// 宿主机模式：直接调用本机 Python3/python 解释器执行代码。
    /// 限制：30秒超时，单次执行，不允许交互。
    /// </summary>
    private async Task<SkillResult> ExecutePythonHostMode(string code, CancellationToken ct)
    {
        var tmpFile = Path.GetTempFileName() + ".py";
        try
        {
            await File.WriteAllTextAsync(tmpFile, code, ct);

            // 优先 python3，回退 python
            var pythonExe = "python3";
            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = $"\"{tmpFile}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            string stdout, stderr;
            int exitCode;

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                // python3 不存在，尝试 python
                psi.FileName = "python";
                proc.Dispose();
                var proc2 = Process.Start(psi);
                if (proc2 is null)
                    return Fail("Neither python3 nor python is available on the host.");
                using var procRef = proc2;
                return await WaitForProcessExitAsync(procRef, ct);
            }

            return await WaitForProcessExitAsync(proc, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PythonSkill] Host mode execution failed");
            return Fail($"Host mode execution error: {ex.Message}");
        }
        finally
        {
            try { File.Delete(tmpFile); } catch { /* best effort */ }
        }
    }

    private static async Task<SkillResult> WaitForProcessExitAsync(Process proc, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return Fail("Python execution timed out after 30 seconds.");
        }

        var stdout = (await proc.StandardOutput.ReadToEndAsync()).TrimEnd();
        var stderr = (await proc.StandardError.ReadToEndAsync()).TrimEnd();
        var output = (stdout + (stderr.Length > 0 ? "\n[stderr]: " + stderr : "")).TrimEnd();

        return new SkillResult
        {
            Success  = proc.ExitCode == 0,
            Output   = output,
            ExitCode = proc.ExitCode,
            Error    = proc.ExitCode != 0 ? $"exit code {proc.ExitCode}" : null,
        };
    }

    private static string Short(string id) => id.Length >= 12 ? id[..12] : id;
}

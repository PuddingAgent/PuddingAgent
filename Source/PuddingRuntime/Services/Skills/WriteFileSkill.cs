using PuddingRuntime.Services.Sandbox;

namespace PuddingRuntime.Services.Skills;

/// <summary>
/// Write File Skill——在 Agent 沙箱容器内创建或覆写文件。
/// 路径通过 Parameters["path"] 传入，内容通过 Input（"content" 字段）传入。
/// 文件内容通过 base64 编码写入，避免特殊字符问题。
/// </summary>
public sealed class WriteFileSkill : ContainerSkillBase
{
    private readonly ILogger<WriteFileSkill> _logger;

    public WriteFileSkill(
        AgentContainerRegistry registry,
        ISandboxProvider sandbox,
        ILogger<WriteFileSkill> logger)
        : base(registry, sandbox, logger)
    {
        _logger = logger;
    }

    public override string SkillId => "write_file";
    public override string Name => "write_file";
    public override string Description =>
        "Create or overwrite a file in the agent's isolated container filesystem. " +
        "Provide 'path' (file path) and 'content' (text to write) as arguments.";
    public override bool RequiresShellExecution => true;

    public override async Task<SkillResult> ExecuteAsync(SkillInvokeRequest request, CancellationToken ct = default)
    {
        // Path comes from Parameters["path"]; content from Input (mapped from "content" key in args JSON)
        if (!request.Parameters.TryGetValue("path", out var path) || string.IsNullOrWhiteSpace(path))
            return Fail("Missing required parameter 'path'.");

        path = path.Trim();
        if (path.Contains('\0') || path.Contains(';') || path.Contains('|')
            || path.Contains('&') || path.Contains('`') || path.Contains('$'))
            return Fail("Invalid file path: contains forbidden characters.");

        var binding = await EnsureContainerRunningAsync(request.AgentInstanceId, request.WorkspaceId, ct);
        if (binding is null)
            return Fail("Failed to provision Docker container for agent.");

        var content = request.Input;
        _logger.LogInformation("[WriteFileSkill] agent={Agent} path={Path} bytes={Len}",
            request.AgentInstanceId, path, content.Length);

        // base64 encode both path and content.  python3 decodes and writes.
        var encodedPath    = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(path));
        var encodedContent = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));
        var cmd = $"python3 -c \"import base64; " +
                  $"p=base64.b64decode('{encodedPath}').decode(); " +
                  $"c=base64.b64decode('{encodedContent}').decode(); " +
                  $"import os; os.makedirs(os.path.dirname(p) or '.', exist_ok=True); " +
                  $"open(p,'w').write(c); print('Written',len(c),'chars to',p)\"";

        var exec = await Sandbox.ExecAsync(binding.ContainerId, cmd, timeoutSeconds: 15, ct);

        return new SkillResult
        {
            Success  = exec.ExitCode == 0,
            Output   = exec.ExitCode == 0 ? exec.Stdout.Trim() : exec.Stderr.Trim(),
            ExitCode = exec.ExitCode,
            Error    = exec.ExitCode != 0 ? $"exit code {exec.ExitCode}: {exec.Stderr}" : null,
        };
    }
}

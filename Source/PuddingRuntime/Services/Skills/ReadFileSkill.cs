using PuddingRuntime.Services.Sandbox;

namespace PuddingRuntime.Services.Skills;

/// <summary>
/// Read File Skill——读取 Agent 沙箱容器内的文件内容。
/// 对路径进行基本安全校验，防止注入。
/// </summary>
public sealed class ReadFileSkill : ContainerSkillBase
{
    private readonly ILogger<ReadFileSkill> _logger;

    public ReadFileSkill(
        AgentContainerRegistry registry,
        ISandboxProvider sandbox,
        AgentSkillPackageRegistry skillPackageRegistry,
        ILogger<ReadFileSkill> logger)
        : base(registry, sandbox, skillPackageRegistry, logger)
    {
        _logger = logger;
    }

    public override string SkillId => "read_file";
    public override string Name => "read_file";
    public override string Description =>
        "Read the content of a file from the agent's isolated container filesystem. " +
        "Provide an absolute or relative path and get the file content as text.";
    public override bool RequiresShellExecution => true;

    public override async Task<SkillResult> ExecuteAsync(SkillInvokeRequest request, CancellationToken ct = default)
    {
        var path = request.Input.Trim();

        if (string.IsNullOrEmpty(path))
            return Fail("File path is required.");

        // Basic injection guard — these chars have no place in a plain file path
        if (path.Contains('\0') || path.Contains(';') || path.Contains('|')
            || path.Contains('&') || path.Contains('`') || path.Contains('$'))
            return Fail("Invalid file path: contains forbidden characters.");

        var binding = await EnsureContainerRunningAsync(request.AgentInstanceId, request.WorkspaceId, ct);
        if (binding is null)
            return Fail("Failed to provision Docker container for agent.");

        _logger.LogInformation("[ReadFileSkill] agent={Agent} path={Path}",
            request.AgentInstanceId, path);

        // Escape single quotes in path for the shell command
        var safePath = path.Replace("'", "'\\''");
        var exec = await Sandbox.ExecAsync(binding.ContainerId, $"cat '{safePath}'",
            timeoutSeconds: 10, ct);

        return new SkillResult
        {
            Success  = exec.ExitCode == 0,
            Output   = exec.Stdout.TrimEnd(),
            ExitCode = exec.ExitCode,
            Error    = exec.ExitCode != 0 ? exec.Stderr.Trim() : null,
        };
    }
}

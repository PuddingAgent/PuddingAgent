using System.IO;
using System.Text;
using PuddingCode.Models;
using PuddingRuntime.Services.Sandbox;

namespace PuddingRuntime.Services.Skills;

/// <summary>
/// Read File Skill——读取 Agent 沙箱容器内的文件内容。
/// 优先使用 Docker 沙箱模式；Docker 不可用时自动降级为宿主机文件 I/O。
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
    public override ToolPermissionLevel PermissionLevel => ToolPermissionLevel.Low;

    public override async Task<SkillResult> ExecuteAsync(SkillInvokeRequest request, CancellationToken ct = default)
    {
        var path = request.Input.Trim();

        if (string.IsNullOrEmpty(path))
            return Fail("File path is required.");

        // Basic injection guard — these chars have no place in a plain file path
        if (path.Contains('\0') || path.Contains(';') || path.Contains('|')
            || path.Contains('&') || path.Contains('`') || path.Contains('$'))
            return Fail("Invalid file path: contains forbidden characters.");

        // 尝试 Docker 沙箱模式
        try
        {
            var binding = await EnsureContainerRunningAsync(request.AgentInstanceId, request.WorkspaceId, ct);
            if (binding is not null)
            {
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ReadFileSkill] Docker sandbox unavailable, falling back to host mode for path={Path}", path);
        }

        // 降级：宿主模式
        return ReadFileHostMode(path);
    }

    /// <summary>
    /// 宿主机文件读取——Docker 不可用时的降级路径。
    /// 仅允许读取当前工作目录及子目录下的文件，并截断超过 100K 字符的内容。
    /// </summary>
    private SkillResult ReadFileHostMode(string filePath)
    {
        var workDir = Path.GetFullPath(Directory.GetCurrentDirectory());
        var fullPath = Path.GetFullPath(filePath);
        if (!fullPath.StartsWith(workDir + Path.DirectorySeparatorChar) && fullPath != workDir)
            return Fail($"Access denied: '{filePath}' is outside the workspace directory.");

        if (!File.Exists(fullPath))
            return Fail($"File not found: {filePath}");

        try
        {
            var content = File.ReadAllText(fullPath, Encoding.UTF8);
            if (content.Length > 100_000)
                content = content[..100_000] + $"\n... (truncated at 100K chars, total {content.Length} chars)";
            _logger.LogInformation("[ReadFileSkill] Host read: {Path} ({Size} chars)", filePath, content.Length);
            return new SkillResult { Success = true, Output = content };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ReadFileSkill] Host read failed: {Path}", filePath);
            return Fail($"Failed to read file: {ex.Message}");
        }
    }
}

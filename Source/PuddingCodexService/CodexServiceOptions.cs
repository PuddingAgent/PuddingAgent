using System.Text.Json;

namespace PuddingCodexService;

public sealed class CodexServiceOptions
{
    public string DataRoot { get; set; } = string.Empty;
    public string RepositoryRoot { get; set; } = string.Empty;
    public string SupervisorRunDirectory { get; set; } = string.Empty;
    public string CodexCommand { get; set; } = OperatingSystem.IsWindows() ? "codex.cmd" : "codex";
    public string[] CodexArguments { get; set; } = ["mcp-server"];
    public string TaskSandbox { get; set; } = "danger-full-access";
    public string TaskApprovalPolicy { get; set; } = "never";
    public int ConnectionTimeoutSeconds { get; set; } = 30;
    public int CallTimeoutSeconds { get; set; } = 3_600;
    public int ShutdownTimeoutSeconds { get; set; } = 5;
    public int RestartDelaySeconds { get; set; } = 10;

    public string TaskStoreDirectory => Path.Combine(DataRoot, "codex-service", "tasks");

    public static CodexServiceOptions FromConfiguration(IConfiguration configuration)
    {
        var result = new CodexServiceOptions();
        configuration.GetSection("CodexService").Bind(result);

        result.DataRoot = FirstNonEmpty(
            result.DataRoot,
            Environment.GetEnvironmentVariable("PUDDING_DATA_ROOT"),
            Path.Combine(AppContext.BaseDirectory, "data"));
        result.RepositoryRoot = FirstNonEmpty(
            result.RepositoryRoot,
            Environment.GetEnvironmentVariable("PUDDING_REPOSITORY_ROOT"),
            Directory.GetCurrentDirectory());
        result.SupervisorRunDirectory = FirstNonEmpty(
            result.SupervisorRunDirectory,
            Environment.GetEnvironmentVariable("PUDDING_SUPERVISOR_RUN_DIR"),
            Path.Combine(result.RepositoryRoot, "tmp", "dev"));
        result.CodexCommand = FirstNonEmpty(
            configuration["CodexService:CodexCommand"],
            Environment.GetEnvironmentVariable("PUDDING_CODEX_COMMAND"),
            result.CodexCommand);
        if (Environment.GetEnvironmentVariable("PUDDING_CODEX_ARGUMENTS_JSON") is { Length: > 0 } argumentsJson)
        {
            result.CodexArguments = JsonSerializer.Deserialize<string[]>(argumentsJson)
                                    ?? throw new InvalidOperationException(
                                        "PUDDING_CODEX_ARGUMENTS_JSON must be a JSON string array.");
        }
        return result;
    }

    public void Validate()
    {
        DataRoot = Path.GetFullPath(DataRoot);
        RepositoryRoot = Path.GetFullPath(RepositoryRoot);
        SupervisorRunDirectory = Path.GetFullPath(SupervisorRunDirectory);

        if (!Directory.Exists(RepositoryRoot))
            throw new DirectoryNotFoundException($"CodexService repository root does not exist: {RepositoryRoot}");
        if (string.IsNullOrWhiteSpace(CodexCommand))
            throw new InvalidOperationException("CodexService:CodexCommand is required.");
        if (CodexArguments.Length == 0)
            throw new InvalidOperationException("CodexService:CodexArguments must not be empty.");
        if (!string.Equals(TaskSandbox, "danger-full-access", StringComparison.Ordinal)
            || !string.Equals(TaskApprovalPolicy, "never", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "PuddingCodexService currently requires Yolo execution: " +
                "TaskSandbox=danger-full-access and TaskApprovalPolicy=never.");
        }
        if (ConnectionTimeoutSeconds is < 1 or > 300)
            throw new InvalidOperationException("CodexService:ConnectionTimeoutSeconds must be between 1 and 300.");
        if (CallTimeoutSeconds is < 1 or > 86_400)
            throw new InvalidOperationException("CodexService:CallTimeoutSeconds must be between 1 and 86400.");
        if (ShutdownTimeoutSeconds is < 1 or > 30)
            throw new InvalidOperationException("CodexService:ShutdownTimeoutSeconds must be between 1 and 30.");
        if (RestartDelaySeconds is < 5 or > 120)
            throw new InvalidOperationException("CodexService:RestartDelaySeconds must be between 5 and 120.");

        Directory.CreateDirectory(TaskStoreDirectory);
        Directory.CreateDirectory(SupervisorRunDirectory);
    }

    public string NormalizeWorkingDirectory(string? value)
    {
        var fullPath = Path.GetFullPath(string.IsNullOrWhiteSpace(value) ? RepositoryRoot : value);
        var relative = Path.GetRelativePath(RepositoryRoot, fullPath);
        if (relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException(
                $"Codex working directory must remain inside repository root '{RepositoryRoot}'.");
        }

        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Codex working directory does not exist: {fullPath}");
        return fullPath;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value))!;
}

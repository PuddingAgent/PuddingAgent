using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PuddingCode.Configuration;
using PuddingCode.Runtime;

namespace PuddingRuntime.Services;

/// <summary>
/// 文件化运行时执行配置服务。
/// 子代理并发、超时和默认权限模式会影响调度安全边界，必须由统一配置文件承载；
/// 不能散落在工具实现、Controller 或测试辅助代码中，否则后续调度策略会再次腐烂。
/// </summary>
public sealed class RuntimeExecutionConfigService : IRuntimeExecutionConfigService
{
    public const string FileName = "runtime.execution.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly PuddingDataPaths _paths;
    private readonly ILogger<RuntimeExecutionConfigService> _logger;
    private readonly object _sync = new();
    private RuntimeExecutionOptions? _cached;

    public RuntimeExecutionConfigService(
        PuddingDataPaths paths,
        ILogger<RuntimeExecutionConfigService> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public RuntimeExecutionOptions GetOptions()
    {
        lock (_sync)
        {
            if (_cached is not null)
                return _cached;

            Directory.CreateDirectory(_paths.ConfigRoot);
            var path = _paths.SystemConfigFile(FileName);
            var loaded = Load(path);
            var normalized = Normalize(loaded);

            if (!File.Exists(path) || !Equals(loaded, normalized))
            {
                File.WriteAllText(path, JsonSerializer.Serialize(normalized, JsonOptions));
                _logger.LogInformation("[RuntimeExecutionConfig] Seeded or repaired {Path}", path);
            }

            _cached = normalized;
            return normalized;
        }
    }

    private RuntimeExecutionOptions Load(string path)
    {
        if (!File.Exists(path))
            return new RuntimeExecutionOptions();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<RuntimeExecutionOptions>(json, JsonOptions)
                   ?? new RuntimeExecutionOptions();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Runtime execution config '{path}' is invalid or unreadable. Fix the JSON before starting sub-agent execution.",
                ex);
        }
    }

    private static RuntimeExecutionOptions Normalize(RuntimeExecutionOptions options)
    {
        var subAgents = options.SubAgents ?? new SubAgentExecutionOptions();
        var maxPerTemplate = Math.Max(1, subAgents.MaxConcurrentPerTemplate);
        var maxPerWorkspace = Math.Max(maxPerTemplate, subAgents.MaxConcurrentPerWorkspace);
        var maxTimeout = Math.Max(1, subAgents.MaxTimeoutSeconds);
        var defaultTimeout = subAgents.DefaultTimeoutSeconds <= 0
            ? maxTimeout
            : Math.Min(subAgents.DefaultTimeoutSeconds, maxTimeout);
        var permissionMode = string.Equals(subAgents.DefaultPermissionMode, SubAgentPermissionModes.Low, StringComparison.OrdinalIgnoreCase)
            ? SubAgentPermissionModes.Low
            : SubAgentPermissionModes.Inherit;

        return options with
        {
            SubAgents = subAgents with
            {
                MaxConcurrentPerTemplate = maxPerTemplate,
                MaxConcurrentPerWorkspace = maxPerWorkspace,
                DefaultTimeoutSeconds = defaultTimeout,
                MaxTimeoutSeconds = maxTimeout,
                DefaultPermissionMode = permissionMode,
            },
        };
    }
}

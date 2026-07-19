using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Runtime;

namespace PuddingCode.Tools;

/// <summary>Tool 的后台与运行时分类，用于展示、过滤和默认策略。</summary>
public enum ToolCategory
{
    General,
    Query,
    Memory,
    FileSystem,
    Network,
    Execute,
    Messaging,
    Orchestration,
    Security,
    Shell,
}

/// <summary>Tool 的安全特征。未声明时按保守策略处理。</summary>
[Flags]
public enum ToolSafetyFlags
{
    None = 0,
    ReadOnly = 1 << 0,
    ConcurrencySafe = 1 << 1,
    Destructive = 1 << 2,
    RequiresShell = 1 << 3,
    RequiresFileWrite = 1 << 4,
    RequiresNetwork = 1 << 5,
}

/// <summary>Controls whether a tool is exposed to sub-agents. Smart* wrappers that
/// internally delegate to sub-agents must be marked MainAgentOnly to prevent circular calls.</summary>
public enum SubAgentExposure
{
    /// <summary>Default — tool is exposed to both main agents and sub-agents.</summary>
    Default,
    /// <summary>Tool is excluded from sub-agent tool lists. Use for Smart* wrappers that
    /// internally use spawn_sub_agent to avoid sub-agents calling back into SmartSearch.</summary>
    MainAgentOnly,
}

/// <summary>声明一个可由 Agent 调用的 Pudding Tool。</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ToolAttribute(
    string id,
    string name,
    string description,
    ToolCategory category = ToolCategory.General,
    ToolPermissionLevel permission = ToolPermissionLevel.Medium,
    ToolSafetyFlags safety = ToolSafetyFlags.None) : Attribute
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public ToolCategory Category { get; } = category;
    public ToolPermissionLevel Permission { get; } = permission;
    public ToolSafetyFlags Safety { get; } = safety;
    public SubAgentExposure SubAgentExposure { get; set; } = SubAgentExposure.Default;
    public bool IsEnabledByDefault { get; set; } = true;
    public int SortOrder { get; set; } = 100;
}

/// <summary>声明 Tool 参数字段描述。</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class ToolParamAttribute(string description) : Attribute
{
    public string Description { get; } = description;
}

/// <summary>Tool 的稳定描述符。Admin、模板授权、LLM schema 和执行服务都读取它。</summary>
public sealed record ToolDescriptor
{
    public required string ToolId { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public ToolCategory Category { get; init; } = ToolCategory.General;
    public ToolPermissionLevel PermissionLevel { get; init; } = ToolPermissionLevel.Medium;
    public ToolSafetyFlags Safety { get; init; } = ToolSafetyFlags.None;
    public SubAgentExposure SubAgentExposure { get; init; } = SubAgentExposure.Default;
    public ToolParameterSchema Parameters { get; init; } = new([], []);
    public bool IsEnabledByDefault { get; init; } = true;
    public int SortOrder { get; init; } = 100;

    /// <summary>
    /// Tool source category for catalog and diagnostics. Built-in tools keep the default; plugin
    /// tools use this field so admin surfaces can show where a capability came from without
    /// learning about Runtime plugin internals.
    /// </summary>
    public string SourceKind { get; init; } = "BuiltIn";

    /// <summary>Stable source id such as a plugin id. Null for built-in tools.</summary>
    public string? SourceId { get; init; }

    /// <summary>
    /// Runtime availability status. It is intentionally a string so plugin lifecycle states can
    /// evolve without forcing admin DTO and template storage migrations for every new state.
    /// </summary>
    public string RuntimeStatus { get; init; } = "Available";
}

/// <summary>Tool 在模板和运行时授权链路中的统一权限层级。</summary>
public enum ToolPermissionTier
{
    AutoAllowed,
    TemplateGranted,
    RuntimeGranted,
    Blocked,
}

/// <summary>Tool 权限策略判定结果。</summary>
public sealed record ToolPermissionDecision
{
    public required string ToolId { get; init; }
    public required ToolPermissionTier Tier { get; init; }
    public bool IsExposedToAgent { get; init; }
    public bool RequiresRuntimeAuthorization { get; init; }
    public bool RequiresShellExecution { get; init; }
    public bool RequiresFileWrite { get; init; }
    public bool RequiresNetworkAccess { get; init; }
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// 统一 Tool 权限策略服务。
/// Tool 描述符是风险事实源；模板、指令、执行器和后台 API 都应通过该服务做权限判定。
/// </summary>
public interface IToolPermissionPolicyService
{
    ToolPermissionDecision Classify(ToolDescriptor descriptor);
    bool RequiresRuntimeAuthorization(ToolDescriptor descriptor);
    bool CanExposeToAgent(ToolDescriptor descriptor, PuddingCode.Platform.CapabilityPolicy? policy);
    PuddingCode.Platform.CapabilityPolicy BuildCapabilityPolicy(
        IEnumerable<ToolDescriptor> descriptors,
        IEnumerable<string> selectedToolNames,
        bool isTaskRole);
}

/// <summary>Tool 执行上下文，由平台注入，Tool 实现不需要自行解析会话和权限。</summary>
public sealed record ToolExecutionContext
{
    public required string WorkspaceId { get; init; }
    public required string SessionId { get; init; }
    public required string AgentInstanceId { get; init; }
    /// <summary>
    /// 本次执行快照确定的文件工具根目录。它是文件系统边界，不等同于 WorkspaceId。
    /// </summary>
    public string? WorkingDirectory { get; init; }
    public string? AgentTemplateId { get; init; }
    public RuntimeTraceContext? Trace { get; init; }
    /// <summary>
    /// 当前 Tool 所属执行身份。ToolCallId 在 ToolInvocationService 进入工具前冻结。
    /// </summary>
    public RuntimeExecutionIdentity? ExecutionIdentity { get; init; }
    public bool IsYoloMode { get; init; }
}

/// <summary>平台传给 Tool 的执行请求。</summary>
public sealed record ToolExecutionRequest
{
    public required string ToolCallId { get; init; }
    public required string ArgumentsJson { get; init; }
    public required ToolExecutionContext Context { get; init; }
}

/// <summary>Tool 的结构化执行结果。</summary>
public sealed record ToolExecutionResult
{
    public required bool Success { get; init; }
    public string Output { get; init; } = string.Empty;
    public string? Error { get; init; }
    public int ExitCode { get; init; }

    public static ToolExecutionResult Ok(string output) => new()
    {
        Success = true,
        Output = output,
        ExitCode = 0,
    };

    public static ToolExecutionResult Fail(string error, int exitCode = 1) => new()
    {
        Success = false,
        Output = string.Empty,
        Error = error,
        ExitCode = exitCode,
    };
}

/// <summary>统一 Tool 抽象。新 Tool 应优先实现此接口。</summary>
public interface IPuddingTool
{
    ToolDescriptor Descriptor { get; }
    Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request, CancellationToken ct = default);
}

/// <summary>Tool 注册表接口。它是运行时、后台和模板授权读取 Tool 清单的统一边界。</summary>
public interface IPuddingToolRegistry
{
    IPuddingTool? GetTool(string toolId);
    ToolDescriptor? GetDescriptor(string toolId);
    IReadOnlyList<ToolDescriptor> ListDescriptors();
    IReadOnlyList<ToolDescriptor> ListAvailable(PuddingCode.Platform.CapabilityPolicy? policy);
}

/// <summary>
/// Supplies tools from a non-DI source such as manifest-discovered plugins.
/// The registry still performs the final id validation and duplicate detection, so every source
/// contributes to the same capability and execution boundary.
/// </summary>
public interface IPuddingToolSource
{
    string SourceId { get; }
    IReadOnlyList<IPuddingTool> ListTools();
}

/// <summary>Tool Catalog 服务。后台 UI 应读取它，而不是维护独立的硬编码能力清单。</summary>
public interface IPuddingToolCatalogService
{
    IReadOnlyList<ToolDescriptor> ListTools(bool enabledByDefaultOnly = false);
}

/// <summary>统一 Tool 执行入口。调用方只传入 ToolId、参数和能力策略。</summary>
public interface IPuddingToolExecutionService
{
    Task<ToolExecutionResult> ExecuteAsync(
        string toolId,
        string argumentsJson,
        ToolExecutionContext context,
        PuddingCode.Platform.CapabilityPolicy? policy,
        CancellationToken ct = default);
}

/// <summary>强类型参数 Tool 基类。负责元数据和参数反序列化，派生类只实现业务逻辑。</summary>
public abstract class PuddingToolBase<TArgs> : IPuddingTool
    where TArgs : class
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public ToolDescriptor Descriptor { get; }

    protected PuddingToolBase()
    {
        Descriptor = ToolDescriptorFactory.Create(GetType(), typeof(TArgs));
    }

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request, CancellationToken ct = default)
    {
        try
        {
            var args = DeserializeArgs(request.ArgumentsJson);
            return await ExecuteCoreAsync(args, request.Context, ct);
        }
        catch (JsonException ex)
        {
            return ToolExecutionResult.Fail(BuildInvalidArgumentsJsonError(ex, request.ArgumentsJson));
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Fail(ex.Message);
        }
    }

    protected abstract Task<ToolExecutionResult> ExecuteCoreAsync(
        TArgs args,
        ToolExecutionContext context,
        CancellationToken ct);

    private static TArgs DeserializeArgs(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return Activator.CreateInstance<TArgs>();

        using var doc = JsonDocument.Parse(argumentsJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return JsonSerializer.Deserialize<TArgs>(argumentsJson, s_jsonOptions)
                   ?? Activator.CreateInstance<TArgs>();

        var normalized = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            normalized[prop.Name] = prop.Value.Clone();
            normalized[ToPascalCase(prop.Name)] = prop.Value.Clone();
        }

        var json = JsonSerializer.Serialize(normalized, s_jsonOptions);
        return JsonSerializer.Deserialize<TArgs>(json, s_jsonOptions)
               ?? Activator.CreateInstance<TArgs>();
    }

    private static string ToPascalCase(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;
        var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return name;
        return string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private static string BuildInvalidArgumentsJsonError(JsonException ex, string? argumentsJson)
    {
        var fieldHint = TryInferJsonField(argumentsJson, ex.LineNumber) is { Length: > 0 } field
            ? $" Near field '{field}'."
            : "";
        return "Tool arguments must be valid JSON object. String values must be wrapped in double quotes; " +
               "for example: {\"rollback_plan\": \"No rollback is required.\"}." +
               fieldHint +
               $" JSON parser error: {ex.Message}";
    }

    private static string? TryInferJsonField(string? argumentsJson, long? lineNumber)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson) || lineNumber is null)
            return null;

        var lines = argumentsJson.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var index = (int)Math.Clamp(lineNumber.Value, 0, lines.Length - 1);
        for (var i = index; i >= 0; i--)
        {
            var match = Regex.Match(lines[i], "\"(?<name>[^\"]+)\"\\s*:");
            if (match.Success)
                return match.Groups["name"].Value;
        }

        return null;
    }
}

/// <summary>Tool 描述符工厂。后续可以替换为 source generator，但运行时反射已足够支撑自动发现。</summary>
public static partial class ToolDescriptorFactory
{
    public static ToolDescriptor Create<TTool, TArgs>()
        => Create(typeof(TTool), typeof(TArgs));

    public static ToolDescriptor Create(Type toolType, Type argsType)
    {
        var attr = toolType.GetCustomAttribute<ToolAttribute>()
                   ?? throw new InvalidOperationException(
                       $"Tool type '{toolType.FullName}' must be annotated with [Tool].");

        if (!ToolIdRegex().IsMatch(attr.Id))
        {
            throw new InvalidOperationException(
                $"Tool type '{toolType.FullName}' has invalid tool id '{attr.Id}'. " +
                "Tool ids must use letters, numbers, and underscores only.");
        }

        return new ToolDescriptor
        {
            ToolId = attr.Id,
            Name = attr.Name,
            Description = attr.Description,
            Category = attr.Category,
            PermissionLevel = attr.Permission,
            Safety = attr.Safety,
            IsEnabledByDefault = attr.IsEnabledByDefault,
            SubAgentExposure = attr.SubAgentExposure,
            SortOrder = attr.SortOrder,
            Parameters = BuildParameterSchema(argsType),
        };
    }

    [GeneratedRegex(@"^[a-zA-Z0-9_]+$")]
    private static partial Regex ToolIdRegex();

    public static ToolParameterSchema BuildParameterSchema(Type argsType)
    {
        if (argsType == typeof(object))
            return new ToolParameterSchema([], []);

        var properties = new List<ToolParameter>();
        var required = new List<string>();

        foreach (var prop in argsType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetMethod is null)
                continue;

            var name = ToSnakeCase(prop.Name);
            var description = prop.GetCustomAttribute<ToolParamAttribute>()?.Description
                              ?? prop.Name;
            properties.Add(new ToolParameter(name, MapClrTypeToJsonType(prop.PropertyType), description));

            if (prop.GetCustomAttribute<RequiredMemberAttribute>() is not null)
                required.Add(name);
        }

        return new ToolParameterSchema(properties, required);
    }

    private static string MapClrTypeToJsonType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(string)) return "string";
        if (underlying == typeof(int)
            || underlying == typeof(long)
            || underlying == typeof(short)
            || underlying == typeof(byte)) return "integer";
        if (underlying == typeof(double)
            || underlying == typeof(float)
            || underlying == typeof(decimal)) return "number";
        if (underlying == typeof(bool)) return "boolean";
        if (underlying.IsArray
            || (underlying != typeof(string)
                && underlying.GetInterfaces().Any(i =>
                    i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))))
        {
            return "array";
        }

        return "object";
    }

    private static string ToSnakeCase(string name)
        => SnakeCaseRegex().Replace(name, "_$1").TrimStart('_').ToLowerInvariant();

    [GeneratedRegex("([A-Z])")]
    private static partial Regex SnakeCaseRegex();
}

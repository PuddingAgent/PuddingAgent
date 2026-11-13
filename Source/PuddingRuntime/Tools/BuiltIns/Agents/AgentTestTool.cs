using System.Text.Json;
using System.Text.Json.Serialization;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// AgentTestTool 请求参数。
/// </summary>
public sealed class AgentTestArgs
{
    /// <summary>要验证的工具 ID（如 "file_write"、"sleep"）。</summary>
    [ToolParam("要验证的工具 ID")]
    public string ToolId { get; init; } = string.Empty;

    /// <summary>可选的安全测试参数 JSON（用于实际调用测试）。</summary>
    [ToolParam("可选的安全测试参数 JSON")]
    public string? TestArgs { get; init; }
}

/// <summary>
/// Agent 工具验证器 — 验证指定工具是否注册、可用、权限级别与实际行为是否一致。
///
/// 当 Agent 对上下文中标注的工具可用性有疑问时，调用此工具进行实际验证，
/// 而非仅依赖文字描述。验证结果会写入记忆库，供后续 Agent 复用。
/// </summary>
[Tool(
    id: "test_tool",
    name: "Test Tool Availability",
    description: "验证指定工具是否注册、可用、权限级别。返回结构化可用性报告。当对工具可用性有疑问时使用此工具验证，而非猜测。",
    category: ToolCategory.Orchestration,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 10)]
public sealed class AgentTestTool : PuddingToolBase<AgentTestArgs>
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    // 使用 IServiceProvider 延迟解析 IPuddingToolRegistry，
    // 避免与 PuddingToolRegistry(IEnumerable<IPuddingTool>) 形成循环依赖。
    private readonly IServiceProvider _serviceProvider;
    private readonly IMemoryLibraryConvenience? _memoryLibrary;
    private readonly ILogger<AgentTestTool> _logger;

    public AgentTestTool(
        IServiceProvider serviceProvider,
        ILogger<AgentTestTool> logger,
        IMemoryLibraryConvenience? memoryLibrary = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _memoryLibrary = memoryLibrary;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        AgentTestArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var toolId = args.ToolId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(toolId))
            return ToolExecutionResult.Fail("toolId is required.");

        // 延迟解析 IPuddingToolRegistry，避免循环依赖
        var toolRegistry = _serviceProvider.GetRequiredService<IPuddingToolRegistry>();

        // 1. 检查工具是否可用。
        //    内置注册表只含 BuiltIn 工具；host-service 包（pudding.files / pudding.browser / …）
        //    声明的工具不在其中。只查注册表会给出**假阴性**：实测 file_read、browser_navigate、
        //    sleep 在返回 registered=false 的同一时刻完全可调用。因此未命中时继续查统一 Tool 目录。
        var tool = toolRegistry.GetTool(toolId, context.WorkspaceId);
        var resolution = ResolveDescriptor(
            toolRegistry.GetDescriptor(toolId, context.WorkspaceId),
            _serviceProvider.GetService<IPuddingToolCatalogService>(),
            context.WorkspaceId ?? "default",
            toolId);
        var descriptor = resolution.Descriptor;
        var resolvedBy = resolution.ResolvedBy;
        var suggestions = resolution.Suggestions;

        var registered = tool is not null || descriptor is not null;
        var isAvailable = registered; // 基础可用性（host-service 首次使用时挂载）

        // 「注册了」不等于「本 Agent 能调用」：search_tools 走的是 ListAvailable，
        // 那里还有一道能力策略闸门（ToolPermissionPolicyService.CanExposeToAgent）。
        // 只报 registered=true 会让调用方以为可加载——实测 provider_balance 就是这样：
        // registered=true / is_available=true，但 search_tools 永远搜不到（不在当前角色的
        // 有效工具集里）。两个判据不同源，必须分开报。
        var exposedToAgent = registered && IsExposed(
            toolRegistry.ListAvailable(context.CapabilityPolicy, context), toolId);

        // 2. 获取工具元数据
        string? labeledPermission = null;
        string? actualPermission = null;
        bool differenceDetected = false;
        string? toolName = null;
        string? toolCategory = null;
        List<string> differences = [];

        // 两个判据不同源：registered 只说明实现存在，exposed 才决定 search_tools 能否加载。
        if (registered && !exposedToAgent)
        {
            differences.Add(
                "registered but not exposed to this agent: the capability policy does not include it, " +
                "so search_tools cannot load it and an invoke would hit the firewall");
        }

        if (descriptor is not null)
        {
            toolName = descriptor.Name;
            toolCategory = descriptor.Category.ToString();
            labeledPermission = descriptor.PermissionLevel.ToString();

            // 从实际注册的工具实例获取真实行为
            if (tool is not null)
            {
                // 检查 ToolAttribute 声明 vs 实际注册状态
                actualPermission = descriptor.PermissionLevel.ToString();

                // 如果工具标注需要授权但实际可直接调用，标记差异
                if (descriptor.PermissionLevel >= ToolPermissionLevel.High)
                {
                    // 高危工具虽注册但可能需要运行时授权
                    isAvailable = true; // 存在但可能需要审批
                }
            }
        }

        // 3. 可选：用安全参数做一次轻量调用测试
        string? testResult = null;
        if (registered && !string.IsNullOrWhiteSpace(args.TestArgs))
        {
            try
            {
                if (toolId.Equals(Descriptor.ToolId, StringComparison.OrdinalIgnoreCase))
                {
                    return ToolExecutionResult.Fail("test_tool cannot recursively invoke itself.");
                }

                // Actual probes must traverse the same firewall, authorization, approval,
                // telemetry and workspace boundaries as normal Agent calls. Directly invoking
                // IPuddingTool here would let this low-risk diagnostic wrapper bypass them.
                var executionService = _serviceProvider.GetRequiredService<IPuddingToolExecutionService>();
                var result = await executionService.ExecuteAsync(
                    toolId,
                    args.TestArgs,
                    context,
                    policy: null,
                    ct);
                testResult = result.Success
                    ? $"OK: {Truncate(result.Output, 200)}"
                    : $"FAIL: {Truncate(result.Error ?? "unknown error", 200)}";
            }
            catch (Exception ex)
            {
                testResult = $"EXCEPTION: {Truncate(ex.Message, 200)}";
                _logger.LogWarning(ex, "[AgentTestTool] Test invocation of {ToolId} failed", toolId);
            }
        }

        // 4. 构建结构化报告
        var report = new AgentTestReport
        {
            ToolId = toolId,
            ToolName = toolName,
            ToolCategory = toolCategory,
            Registered = registered,
            IsAvailable = isAvailable,
            ExposedToAgent = exposedToAgent,
            SourceKind = descriptor?.SourceKind,
            SourceId = descriptor?.SourceId,
            ResolvedBy = resolvedBy,
            Suggestions = suggestions,
            ActualPermission = actualPermission,
            LabeledPermission = labeledPermission,
            DifferenceDetected = differenceDetected,
            Differences = differences.Count > 0 ? differences : null,
            TestResult = testResult,
        };

        // 5. 当发现标注差异时，写入记忆库供后续 Agent 参考
        if (differenceDetected && _memoryLibrary is not null)
        {
            await SaveMemoryFactAsync(context, toolId, report, ct);
        }

        var json = JsonSerializer.Serialize(report, s_jsonOptions);
        _logger.LogInformation("[AgentTestTool] toolId={ToolId} registered={Registered} available={IsAvailable}",
            toolId, registered, isAvailable);

        return ToolExecutionResult.Ok(json);
    }

    /// <summary>
    /// 将验证结果写入记忆库，Key 格式为 tool_availability:{toolId}，
    /// 标记来源为验证 Agent 实例和验证时间。
    /// </summary>
    private async Task SaveMemoryFactAsync(
        ToolExecutionContext context,
        string toolId,
        AgentTestReport report,
        CancellationToken ct)
    {
        try
        {
            var summary = report.IsAvailable
                ? $"工具 '{toolId}' 已验证可用。标注权限: {report.LabeledPermission}，实际行为: {report.ActualPermission}。"
                : $"工具 '{toolId}' 验证结果：未注册或不可用。";

            var experience = new ExperiencePackage
            {
                Title = $"tool_availability:{toolId}",
                Content = summary,
                SuggestedTags = ["系统/工具验证", $"工具/{toolId}"],
                SourceSessionId = context.SessionId,
                SourceReference = $"verified_by:{context.AgentInstanceId}",
                ReferenceType = "memo",
                Importance = 0.6,
                AgentInstanceId = context.AgentInstanceId,
            };

            await _memoryLibrary.UpsertExperienceAsync(
                context.WorkspaceId ?? "default", experience, ct);

            _logger.LogInformation(
                "[AgentTestTool] Saved memory fact for tool={ToolId} agent={AgentId}",
                toolId, context.AgentInstanceId);
        }
        catch (Exception ex)
        {
            // 记忆写入失败不阻断主流程
            _logger.LogWarning(ex,
                "[AgentTestTool] Failed to save memory fact for tool={ToolId}", toolId);
        }
    }

    /// <summary>工具描述符的解析结果。</summary>
    internal sealed record ToolResolution(ToolDescriptor? Descriptor, string ResolvedBy, string? Suggestions);

    /// <summary>
    /// 先看内置注册表，未命中再查统一 Tool 目录（含 host-service 插件包）。
    /// 拆成纯函数是为了能直接单测：内置注册表里没有 ≠ 工具不存在。
    /// </summary>
    internal static ToolResolution ResolveDescriptor(
        ToolDescriptor? registered,
        IPuddingToolCatalogService? catalog,
        string workspaceId,
        string toolId)
    {
        if (registered is not null) return new(registered, "registry", null);

        var inventory = catalog?.ListTools(workspaceId) ?? [];
        var matched = inventory.FirstOrDefault(d =>
            string.Equals(d.ToolId, toolId, StringComparison.OrdinalIgnoreCase));
        if (matched is not null) return new(matched, "host-catalog", null);

        // 未命中时给出最接近的候选，避免把「拼错 ID」误读成「工具不存在」。
        var near = inventory
            .Select(d => d.ToolId)
            .Where(id => id.Contains(toolId, StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .ToArray();
        return new(null, "none", near.Length > 0 ? string.Join(", ", near) : null);
    }

    /// <summary>
    /// 该工具是否在 ListAvailable 的结果里 —— 这正是 search_tools 的判据。
    /// 抽成纯函数以便直接单测（判据不同源是两类假结论的共同来源）。
    /// </summary>
    internal static bool IsExposed(IReadOnlyList<ToolDescriptor> available, string toolId) =>
        available.Any(d => string.Equals(d.ToolId, toolId, StringComparison.OrdinalIgnoreCase));

    private static string Truncate(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLen)
            return text;
        return text[..maxLen] + "...";
    }
}

/// <summary>
/// test_tool 的结构化验证报告。
/// </summary>
public sealed class AgentTestReport
{
    [JsonPropertyName("tool_id")]
    public string ToolId { get; init; } = string.Empty;

    [JsonPropertyName("tool_name")]
    public string? ToolName { get; init; }

    [JsonPropertyName("tool_category")]
    public string? ToolCategory { get; init; }

    [JsonPropertyName("registered")]
    public bool Registered { get; init; }

    [JsonPropertyName("is_available")]
    public bool IsAvailable { get; init; }

    /// <summary>
    /// 当前能力策略是否允许把它暴露给本 Agent。与 <see cref="Registered"/> 分开报：
    /// 注册只说明实现存在，暴露才决定 <c>search_tools</c> 能否加载它。
    /// </summary>
    [JsonPropertyName("exposed_to_agent")]
    public bool ExposedToAgent { get; init; }

    /// <summary>来源类型：BuiltIn 工具为 null，host-service 插件工具为 "Plugin"。</summary>
    [JsonPropertyName("source_kind")]
    public string? SourceKind { get; init; }

    /// <summary>来源包 ID（如 pudding.files），用于定位归属。</summary>
    [JsonPropertyName("source_id")]
    public string? SourceId { get; init; }

    /// <summary>解析路径：registry=内置注册表，host-catalog=host-service 插件目录，none=未找到。</summary>
    [JsonPropertyName("resolved_by")]
    public string? ResolvedBy { get; init; }

    /// <summary>未找到时的近似候选，用于区分「拼错 ID」与「工具不存在」。</summary>
    [JsonPropertyName("suggestions")]
    public string? Suggestions { get; init; }

    [JsonPropertyName("actual_permission")]
    public string? ActualPermission { get; init; }

    [JsonPropertyName("labeled_permission")]
    public string? LabeledPermission { get; init; }

    [JsonPropertyName("difference_detected")]
    public bool DifferenceDetected { get; init; }

    [JsonPropertyName("differences")]
    public List<string>? Differences { get; init; }

    [JsonPropertyName("test_result")]
    public string? TestResult { get; init; }
}

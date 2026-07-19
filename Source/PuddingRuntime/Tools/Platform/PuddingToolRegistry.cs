using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingRuntime.Services.Skills;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// Runtime Tool registry. Built-in DI tools are stable for the process lifetime, while
/// non-DI sources such as manifest-only plugins are re-read on every catalog operation so an
/// uploaded plugin package can become visible without restarting the backend.
/// </summary>
public sealed class PuddingToolRegistry : IPuddingToolRegistry
{
    private readonly IReadOnlyList<IPuddingTool> _nativeTools;
    private readonly IReadOnlyList<IPuddingToolSource> _toolSources;
    private readonly IToolPermissionPolicyService _permissionPolicy;
    private readonly IAgentFirewall? _firewall;

    public PuddingToolRegistry(
        IEnumerable<IPuddingTool> tools,
        IToolPermissionPolicyService? permissionPolicy = null,
        IAgentFirewall? firewall = null,
        IEnumerable<IPuddingToolSource>? toolSources = null)
    {
        _permissionPolicy = permissionPolicy ?? new ToolPermissionPolicyService();
        _firewall = firewall;
        _nativeTools = tools.ToList();
        _toolSources = (toolSources ?? []).ToList();

        _ = BuildToolSnapshot();
    }

    private Dictionary<string, IPuddingTool> BuildToolSnapshot()
    {
        var snapshot = new Dictionary<string, IPuddingTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in _nativeTools.Concat(_toolSources.SelectMany(s => s.ListTools())))
        {
            var id = tool.Descriptor.ToolId;
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException($"Tool '{tool.GetType().FullName}' has empty ToolId.");

            if (!Regex.IsMatch(id, "^[a-zA-Z0-9_]+$"))
                throw new InvalidOperationException(
                    $"Tool '{tool.GetType().FullName}' has invalid ToolId '{id}'. " +
                    "Tool ids must use letters, numbers, and underscores only.");

            if (snapshot.ContainsKey(id))
                throw new InvalidOperationException($"Duplicate tool id '{id}'.");

            snapshot[id] = tool;
        }

        return snapshot;
    }

    public IPuddingTool? GetTool(string toolId) =>
        BuildToolSnapshot().GetValueOrDefault(toolId);

    public ToolDescriptor? GetDescriptor(string toolId) =>
        BuildToolSnapshot().GetValueOrDefault(toolId)?.Descriptor;

    public IReadOnlyList<ToolDescriptor> ListDescriptors() =>
        BuildToolSnapshot().Values
            .Select(t => t.Descriptor)
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.ToolId, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IReadOnlyList<ToolDescriptor> ListAvailable(CapabilityPolicy? policy)
    {
        var descriptors = ListDescriptors();

        return descriptors
            .Where(d => _permissionPolicy.CanExposeToAgent(d, policy))
            .ToList();
    }
}

/// <summary>将 legacy IAgentSkill 适配进新的 Tool 平台。</summary>
public sealed class AgentSkillToolAdapter : IPuddingTool
{
    private static readonly ToolParameterSchema s_inputSchema = new(
        [new ToolParameter("input", "string", "Tool input payload")],
        ["input"]);

    private readonly IAgentSkill _skill;

    public AgentSkillToolAdapter(IAgentSkill skill)
    {
        _skill = skill;
        Descriptor = BuildDescriptor(skill);
    }

    public ToolDescriptor Descriptor { get; }

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request, CancellationToken ct = default)
    {
        var invokeRequest = new SkillInvokeRequest
        {
            AgentInstanceId = request.Context.AgentInstanceId,
            WorkspaceId = request.Context.WorkspaceId,
            SessionId = request.Context.SessionId,
            Input = ExtractInputFromJson(request.ArgumentsJson),
            Parameters = ExtractParametersFromJson(request.ArgumentsJson),
        };

        var result = await _skill.ExecuteAsync(invokeRequest, ct);
        return new ToolExecutionResult
        {
            Success = result.Success,
            Output = result.Output,
            Error = result.Error,
            ExitCode = result.ExitCode,
        };
    }

    private static ToolDescriptor BuildDescriptor(IAgentSkill skill)
    {
        var safety = InferSafety(skill);
        var category = InferCategory(skill);

        if (category == ToolCategory.FileSystem
            && skill.PermissionLevel == ToolPermissionLevel.High)
        {
            safety |= ToolSafetyFlags.RequiresFileWrite | ToolSafetyFlags.Destructive;
        }

        if (category == ToolCategory.Network)
            safety |= ToolSafetyFlags.RequiresNetwork;

        var parameters = skill is ITool legacyTool
            ? legacyTool.Parameters
            : BuildKnownParameterSchema(skill.SkillId);

        return new ToolDescriptor
        {
            ToolId = skill.SkillId,
            Name = skill.Name,
            Description = skill.Description,
            Category = category,
            PermissionLevel = skill.PermissionLevel,
            Safety = safety,
            Parameters = parameters,
            IsEnabledByDefault = true,
            SortOrder = 100,
        };
    }

    private static ToolSafetyFlags InferSafety(IAgentSkill skill)
    {
        var safety = ToolSafetyFlags.None;
        if (IsLegacyReadOnlySkill(skill.SkillId))
            safety |= ToolSafetyFlags.ReadOnly;
        if (skill.RequiresShellExecution)
            safety |= ToolSafetyFlags.RequiresShell;

        return safety;
    }

    private static bool IsLegacyReadOnlySkill(string skillId)
        => skillId.Equals("search_memory", StringComparison.OrdinalIgnoreCase)
           || skillId.Equals("grep_memory", StringComparison.OrdinalIgnoreCase)
           || skillId.Equals("query_sessions", StringComparison.OrdinalIgnoreCase)
           || skillId.Equals("query_session_logs", StringComparison.OrdinalIgnoreCase)
           || skillId.Equals("query_sub_agents", StringComparison.OrdinalIgnoreCase)
           || skillId.Equals("search_grep", StringComparison.OrdinalIgnoreCase);

    private static ToolCategory InferCategory(IAgentSkill skill)
    {
        var id = skill.SkillId;
        if (id.Contains("memory", StringComparison.OrdinalIgnoreCase))
            return ToolCategory.Memory;
        if (id.Contains("search", StringComparison.OrdinalIgnoreCase)
            || id.Contains("query", StringComparison.OrdinalIgnoreCase)
            || id.Contains("list", StringComparison.OrdinalIgnoreCase))
            return ToolCategory.Query;
        if (id.Contains("file", StringComparison.OrdinalIgnoreCase))
            return ToolCategory.FileSystem;
        if (id.Contains("http", StringComparison.OrdinalIgnoreCase)
            || id.Contains("fetch", StringComparison.OrdinalIgnoreCase))
            return ToolCategory.Network;
        if (id.Contains("shell", StringComparison.OrdinalIgnoreCase)
            || id.Contains("terminal", StringComparison.OrdinalIgnoreCase))
            return ToolCategory.Execute;
        if (id.Contains("message", StringComparison.OrdinalIgnoreCase))
            return ToolCategory.Messaging;
        if (id.Contains("task", StringComparison.OrdinalIgnoreCase)
            || id.Contains("event", StringComparison.OrdinalIgnoreCase))
            return ToolCategory.Orchestration;
        if (id.Contains("agent", StringComparison.OrdinalIgnoreCase))
            return ToolCategory.Orchestration;
        return ToolCategory.General;
    }

    private static ToolParameterSchema BuildKnownParameterSchema(string skillId)
    {
        if (skillId.Equals("shell", StringComparison.OrdinalIgnoreCase))
            return new ToolParameterSchema(
                [
                    new ToolParameter("command", "string", "Command to execute on the host. Relative paths are resolved against working_directory when it is provided; avoid repeating the same directory prefix in both fields."),
                    new ToolParameter("shell", "string", "Shell mode: auto, wsl, bash, cmd, or powershell. Default: auto"),
                    new ToolParameter("working_directory", "string", "Host working directory. Default: current runtime directory. If set to the workspace directory, command paths should be relative to that directory or absolute."),
                    new ToolParameter("timeout_seconds", "integer", "Timeout in seconds, 1-600. Default: 30"),
                ],
                ["command"]);

        if (skillId.Equals("http_fetch", StringComparison.OrdinalIgnoreCase))
            return new ToolParameterSchema(
                [
                    new ToolParameter("url", "string", "The full HTTP/HTTPS URL to request"),
                    new ToolParameter("method", "string", "HTTP method: GET, POST, PUT, PATCH, DELETE, HEAD, or OPTIONS (default: GET)"),
                    new ToolParameter("headers", "object", "HTTP request headers as a JSON object"),
                    new ToolParameter("body", "string", "Request body for methods that support a body"),
                    new ToolParameter("content_type", "string", "Content-Type header for request body (default: application/json)"),
                    new ToolParameter("timeout_seconds", "integer", "Request timeout in seconds"),
                    new ToolParameter("output_format", "string", "Output format: markdown, text, raw, or json (default: markdown)"),
                    new ToolParameter("max_response_chars", "integer", "Maximum response characters to return"),
                    new ToolParameter("include_headers", "boolean", "Whether to include response headers in output"),
                    new ToolParameter("cookie_scope", "string", "Cookie scope hint: none or session (default: none)"),
                ],
                ["url"]);

        if (skillId.Equals("search_memory", StringComparison.OrdinalIgnoreCase))
            return new ToolParameterSchema(
                [
                    new ToolParameter("query", "string", "Search keywords or question for memory retrieval."),
                    new ToolParameter("book", "string", "Optional book hint such as 用户档案 or 用户偏好."),
                    new ToolParameter("workspaceId", "string", "Optional workspace id for deeper memory exploration."),
                ],
                ["query"]);

        if (skillId.Equals("save_memory", StringComparison.OrdinalIgnoreCase))
            return new ToolParameterSchema(
                [
                    new ToolParameter("action", "string", "Operation: upsert or delete."),
                    new ToolParameter("type", "string", "Content type: fact / preference / summary / chapter."),
                    new ToolParameter("book", "string", "Optional target book name."),
                    new ToolParameter("content", "string", "Main content for fact, summary, or chapter."),
                    new ToolParameter("key", "string", "Preference key. Required for preference entries."),
                    new ToolParameter("value", "string", "Preference value. Required for preference entries."),
                    new ToolParameter("title", "string", "Chapter title."),
                    new ToolParameter("book_id", "string", "Exact book id for delete."),
                    new ToolParameter("chapter_id", "string", "Exact chapter id for delete."),
                    new ToolParameter("pointer_id", "string", "Exact pointer id for delete."),
                    new ToolParameter("source_ref", "string", "Optional source reference such as session id or URL."),
                    new ToolParameter("source_label", "string", "Optional source label."),
                    new ToolParameter("source_reference", "string", "Internal session path or external URL for source verification."),
                    new ToolParameter("reference_type", "string", "Reference type: internal / external / none."),
                    new ToolParameter("workspace_id", "string", "Workspace id. Runtime usually injects the active workspace."),
                ],
                ["action", "type"]);

        if (skillId.Equals("manage_memory", StringComparison.OrdinalIgnoreCase))
            return new ToolParameterSchema(
                [
                    new ToolParameter("action", "string", "Operation: list_books / create_book / list_chapters / add_chapter / update_chapter / delete_book / add_pointer / list_pointers."),
                    new ToolParameter("book_id", "string", "Target book id."),
                    new ToolParameter("library_id", "string", "Optional library id."),
                    new ToolParameter("title", "string", "Book or chapter title."),
                    new ToolParameter("content", "string", "Chapter content."),
                    new ToolParameter("summary", "string", "Book summary."),
                    new ToolParameter("chapter_id", "string", "Target chapter id."),
                    new ToolParameter("source_type", "string", "Pointer source type for list_pointers."),
                    new ToolParameter("source_id", "string", "Pointer source id for list_pointers."),
                    new ToolParameter("tags", "string", "Comma-separated tags."),
                    new ToolParameter("chapter_order", "number", "Chapter order."),
                    new ToolParameter("source_reference", "string", "Internal session path or external URL for source verification."),
                    new ToolParameter("reference_type", "string", "Reference type: internal / external / none."),
                    new ToolParameter("workspace_id", "string", "Workspace id. Runtime usually injects the active workspace."),
                ],
                ["action"]);

        if (skillId.Equals("grep_memory", StringComparison.OrdinalIgnoreCase))
            return new ToolParameterSchema(
                [
                    new ToolParameter("action", "string", "Operation: search / in_book / list_books / toc."),
                    new ToolParameter("query", "string", "Search query for search or in_book."),
                    new ToolParameter("mode", "string", "Search mode: fts5 or regex."),
                    new ToolParameter("book", "string", "Book name for in_book."),
                    new ToolParameter("top_k", "number", "Maximum result count."),
                    new ToolParameter("workspace_id", "string", "Workspace id. Runtime usually injects the active workspace."),
                ],
                ["action"]);

        if (skillId.Equals("query_sessions", StringComparison.OrdinalIgnoreCase))
            return new ToolParameterSchema(
                [
                    new ToolParameter("action", "string", "Operation: messages or recent."),
                    new ToolParameter("session_id", "string", "Session id. Required for messages."),
                    new ToolParameter("before", "number", "Cursor timestamp in milliseconds."),
                    new ToolParameter("limit", "number", "Page size. Default: 20, max: 50."),
                ],
                ["action"]);

        if (skillId.Equals("query_session_logs", StringComparison.OrdinalIgnoreCase))
            return new ToolParameterSchema(
                [
                    new ToolParameter("action", "string", "Operation: messages / list_days / list_sessions / grep / grep_raw_events / read_raw_events."),
                    new ToolParameter("workspace_id", "string", "Workspace id. Runtime usually injects the active workspace."),
                    new ToolParameter("agent_instance_id", "string", "Agent instance id. Runtime usually injects the active agent."),
                    new ToolParameter("day", "string", "Day in yyyy-MM-dd format."),
                    new ToolParameter("from_day", "string", "Start day in yyyy-MM-dd format."),
                    new ToolParameter("to_day", "string", "End day in yyyy-MM-dd format."),
                    new ToolParameter("session_id", "string", "Session id. Required for messages and read_raw_events."),
                    new ToolParameter("query", "string", "Text or regex query."),
                    new ToolParameter("regex", "string", "true to use .NET regular expressions."),
                    new ToolParameter("diagnostic", "string", "true to enable raw/debug actions."),
                    new ToolParameter("include_events", "string", "true to include raw event frames in grep; requires diagnostic=true."),
                    new ToolParameter("after_sequence", "number", "Raw event pagination cursor."),
                    new ToolParameter("page", "number", "Messages transcript page, starting from 1."),
                    new ToolParameter("window_size", "number", "Transcript window size, default 1024, max 4096."),
                    new ToolParameter("limit", "number", "Maximum rows to return."),
                ],
                ["action"]);

        if (skillId.Equals("search_grep", StringComparison.OrdinalIgnoreCase))
            return new ToolParameterSchema(
                [
                    new ToolParameter("query", "string", "Text or regex to search for in files"),
                    new ToolParameter("pattern", "string", "File glob pattern to filter files"),
                    new ToolParameter("case_sensitive", "string", "Case sensitive search: true/false"),
                    new ToolParameter("max_results", "string", "Maximum matching lines to return"),
                ],
                ["query"]);

        if (skillId.Equals("spawn_sub_agent", StringComparison.OrdinalIgnoreCase))
            return new ToolParameterSchema(
                [
                    new ToolParameter("task", "string", "Legacy free-form task description. Prefer question/scope/already_known/effort/stop_condition/output for structured delegation."),
                    new ToolParameter("tasks", "array", "Optional JSON array of structured sub-agent tasks. Each item requires task_id and either task or question. Do not combine with task."),
                    new ToolParameter("question", "string", "Structured delegation QUESTION: one clear question for the sub-agent to answer."),
                    new ToolParameter("scope", "string", "Structured delegation SCOPE: files, directories, PR, session, or other review boundary."),
                    new ToolParameter("already_known", "string", "Structured delegation ALREADY_KNOWN: facts already known; prevents repeated work."),
                    new ToolParameter("effort", "string", "Structured delegation EFFORT: quick, medium, or thorough."),
                    new ToolParameter("stop_condition", "string", "Structured delegation STOP_CONDITION: when the sub-agent should stop."),
                    new ToolParameter("output", "string", "Structured delegation OUTPUT fields. Default: SUMMARY, CHANGES, EVIDENCE, RISKS, BLOCKERS."),
                    new ToolParameter("agent_template", "string", "Optional agent template id."),
                    new ToolParameter("sync", "boolean", "true to wait for completion, false to run asynchronously."),
                    new ToolParameter("model", "string", "Optional model id or provider/model id."),
                    new ToolParameter("tools", "string", "Optional comma-separated allowed tool id subset for the child agent."),
                    new ToolParameter("permission_mode", "string", "Permission inheritance mode: inherit or low."),
                    new ToolParameter("timeout_seconds", "integer", "Optional sub-agent timeout. Must not exceed runtime.execution.json maxTimeoutSeconds."),
                    new ToolParameter("plan_id", "string", "Optional task planning plan id."),
                    new ToolParameter("task_node_id", "string", "Optional current task node id."),
                    new ToolParameter("parent_task_node_id", "string", "Optional parent task node id."),
                    new ToolParameter("depth", "integer", "Optional current delegation depth."),
                    new ToolParameter("max_depth", "integer", "Optional maximum delegation depth."),
                    new ToolParameter("role_in_plan", "string", "Optional task planning role."),
                ],
                []);

        if (skillId.Equals("manage_tasks", StringComparison.OrdinalIgnoreCase))
            return new ToolParameterSchema(
                [
                    new ToolParameter("operation", "string", "Operation: create / update_status / list / delete"),
                    new ToolParameter("task_id", "string", "Task ID. Required for update_status and delete."),
                    new ToolParameter("title", "string", "Task title. Required for create."),
                    new ToolParameter("status", "string", "Task status: pending / in-progress / completed."),
                ],
                ["operation"]);

        if (skillId.Equals("send_message", StringComparison.OrdinalIgnoreCase))
            return new ToolParameterSchema(
                [
                    new ToolParameter("to", "string", "Message target address list. Examples: user:owner, agent:assistant, room:default, @all."),
                    new ToolParameter("content", "string", "Message content to send."),
                    new ToolParameter("audience", "string", "Optional audience: direct / broadcast / room."),
                    new ToolParameter("visibility", "string", "Optional visibility: private / public / system."),
                    new ToolParameter("room_id", "string", "Optional room id for room transcript and @all broadcasts."),
                    new ToolParameter("priority", "string", "Optional numeric priority. 5 maps to important, 10 maps to urgent."),
                    new ToolParameter("reply_to_message_id", "string", "Optional message id this message replies to."),
                ],
                ["to", "content"]);

        if (skillId.Equals("receive_messages", StringComparison.OrdinalIgnoreCase))
            return new ToolParameterSchema(
                [
                    new ToolParameter("endpoint_id", "string", "Optional endpoint id. Defaults to the current agent instance id."),
                    new ToolParameter("endpoint_kind", "string", "Optional endpoint kind. Defaults to agent."),
                    new ToolParameter("room_id", "string", "Optional room id filter."),
                    new ToolParameter("limit", "string", "Maximum messages to return, from 1 to 100. Defaults to 20."),
                    new ToolParameter("include_delivered", "string", "true to include already delivered messages. Defaults to false."),
                    new ToolParameter("ack", "string", "true to acknowledge returned deliveries after reading. Defaults to false."),
                ],
                []);

        if (skillId.Equals("query_sub_agents", StringComparison.OrdinalIgnoreCase))
            return new ToolParameterSchema(
                [
                    new ToolParameter("action", "string", "Action: list / stats / status / grep / recent / running."),
                    new ToolParameter("sub_agent_id", "string", "Sub-agent id. Required for status."),
                    new ToolParameter("keyword", "string", "Search keyword. Required for grep."),
                    new ToolParameter("days", "integer", "Day count for recent, for example 1 or 7."),
                ],
                ["action"]);

        if (skillId.Equals("event_subscribe", StringComparison.OrdinalIgnoreCase))
            return new ToolParameterSchema(
                [
                    new ToolParameter("operation", "string", "Operation: subscribe / unsubscribe / list."),
                    new ToolParameter("event_type_patterns", "string", "Comma-separated event type patterns, supports wildcards such as mqtt.sensor.*."),
                    new ToolParameter("subscription_id", "string", "Subscription id. Required for unsubscribe."),
                    new ToolParameter("filter_expression", "string", "Optional filter expression, such as priority>=5."),
                ],
                ["operation"]);

        if (skillId.Equals("terminal_execute", StringComparison.OrdinalIgnoreCase))
            return new ToolParameterSchema(
                [
                    new ToolParameter("command", "string", "Command line to execute."),
                    new ToolParameter("cwd", "string", "Working directory. Default: /workspace."),
                ],
                ["command"]);

        return s_inputSchema;
    }

    private static string ExtractInputFromJson(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name is "input" or "command" or "url" or "code" or "query" or "text" or "content")
                        return prop.Value.GetString() ?? string.Empty;
                }
                return root.GetRawText();
            }

            return root.ValueKind == JsonValueKind.String
                ? root.GetString() ?? string.Empty
                : root.GetRawText();
        }
        catch
        {
            return argumentsJson;
        }
    }

    private static IReadOnlyDictionary<string, string> ExtractParametersFromJson(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return new Dictionary<string, string>();
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new Dictionary<string, string>();

            return doc.RootElement.EnumerateObject()
                .Select(p => (p.Name, Value: ConvertJsonValueToParameterString(p.Value)))
                .Where(p => p.Value is not null)
                .ToDictionary(
                    p => p.Name,
                    p => p.Value!,
                    StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    private static string? ConvertJsonValueToParameterString(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
            _ => null,
        };
}

/// <summary>默认 Tool Catalog 实现，从运行时注册表读取真实可执行 Tool。</summary>
public sealed class PuddingToolCatalogService : IPuddingToolCatalogService
{
    private readonly IPuddingToolRegistry _registry;

    public PuddingToolCatalogService(IPuddingToolRegistry registry)
    {
        _registry = registry;
    }

    public IReadOnlyList<ToolDescriptor> ListTools(bool enabledByDefaultOnly = false)
    {
        var tools = _registry.ListDescriptors();
        return enabledByDefaultOnly
            ? tools.Where(t => t.IsEnabledByDefault).ToList()
            : tools;
    }
}

/// <summary>从 Tool 注册表生成 LLM function-call schema。</summary>
public sealed class PuddingToolSchemaService
{
    private readonly IPuddingToolRegistry _registry;

    public PuddingToolSchemaService(IPuddingToolRegistry registry)
    {
        _registry = registry;
    }

    public IReadOnlyList<LlmToolDefinition> BuildLlmTools(CapabilityPolicy? policy)
    {
        return _registry.ListAvailable(policy)
            .Select(d => new LlmToolDefinition
            {
                Name = d.ToolId,
                Description = d.Description,
                Parameters = d.Parameters,
                SubAgentExposure = d.SubAgentExposure,
            })
            .ToList();
    }
}

/// <summary>统一执行 Tool，并在调用前套用注册表策略过滤与沙箱校验。</summary>
public sealed class PuddingToolExecutionService : IPuddingToolExecutionService
{
    private readonly IPuddingToolRegistry _registry;
    private readonly SandboxExecutor _sandbox;
    private readonly ILogger<PuddingToolExecutionService> _logger;
    private readonly IToolPermissionPolicyService _permissionPolicy;
    private readonly ITelemetryMetricSink? _telemetrySink;
    private readonly IToolAuthorizationService? _authorizationService;
    private readonly IToolApprovalService? _approvalService;
    private readonly IRuntimeControlService? _runtimeControl;
    private readonly IAgentFirewall _firewall;

    public PuddingToolExecutionService(
        IPuddingToolRegistry registry,
        SandboxExecutor sandbox,
        ILogger<PuddingToolExecutionService> logger,
        IToolPermissionPolicyService? permissionPolicy = null,
        ITelemetryMetricSink? telemetrySink = null,
        IToolAuthorizationService? authorizationService = null,
        IToolApprovalService? approvalService = null,
        IRuntimeControlService? runtimeControl = null,
        IAgentFirewall? firewall = null)
    {
        _registry = registry;
        _sandbox = sandbox;
        _logger = logger;
        _permissionPolicy = permissionPolicy ?? new ToolPermissionPolicyService();
        _telemetrySink = telemetrySink;
        _authorizationService = authorizationService;
        _approvalService = approvalService;
        _runtimeControl = runtimeControl;
        _firewall = firewall ?? new AgentFirewall(
            runtimeControl,
            _permissionPolicy,
            registry,
            authorizationService,
            approvalService,
            sandbox);
    }

    public async Task<ToolExecutionResult> ExecuteAsync(
        string toolId,
        string argumentsJson,
        ToolExecutionContext context,
        CapabilityPolicy? policy,
        CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var tool = _registry.GetTool(toolId);
        if (tool is null)
        {
            var result = ToolExecutionResult.Fail($"Tool '{toolId}' not found.");
            await RecordTelemetryAsync(toolId, argumentsJson, context, startedAt, result, "not_found", ct);
            return result;
        }

        var exposureDenied = GetSubAgentExposureDenial(tool.Descriptor, context);
        if (exposureDenied is not null)
        {
            var result = ToolExecutionResult.Fail(exposureDenied);
            await RecordTelemetryAsync(toolId, argumentsJson, context, startedAt, result, "subagent_exposure", ct);
            return result;
        }

        // Set YOLO mode on context (backward compat for tools that read context.IsYoloMode)
        if (_runtimeControl?.Mode == RuntimeExecutionMode.Yolo)
            context = context with { IsYoloMode = true };

        // ── Unified Agent Firewall (Phase 2) ──
        // Replaces scattered YOLO / capability / authorization / sandbox checks
        var firewallCtx = FirewallContext.FromExecutionContext(
            context,
            policy: policy,
            mode: _runtimeControl?.Mode ?? RuntimeExecutionMode.Normal,
            argumentsJson: argumentsJson,
            toolId: toolId);
        var fwDecision = await _firewall.EvaluateAsync(firewallCtx, ct);
        if (!fwDecision.Allowed)
        {
            var stage = fwDecision.DeniedAtGate switch
            {
                FirewallGate.Capability => "policy",
                FirewallGate.Authorization => "authorization",
                FirewallGate.Sandbox => "sandbox",
                _ => "policy"
            };
            var exitCode = fwDecision.DeniedAtGate == FirewallGate.Authorization ? 403 : 1;
            var deniedResult = ToolExecutionResult.Fail(fwDecision.DenyReason!, exitCode);
            await RecordTelemetryAsync(toolId, argumentsJson, context, startedAt, deniedResult, stage, ct);
            return deniedResult;
        }

        try
        {
            var executionResult = await tool.ExecuteAsync(new ToolExecutionRequest
            {
                ToolCallId = Guid.NewGuid().ToString("N"),
                ArgumentsJson = argumentsJson,
                Context = context,
            }, ct);
            await RecordTelemetryAsync(toolId, argumentsJson, context, startedAt, executionResult, "execute", ct);
            return executionResult;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RecordTelemetryAsync(
                toolId,
                argumentsJson,
                context,
                startedAt,
                ToolExecutionResult.Fail(ex.Message),
                "exception",
                ct);
            throw;
        }
    }

    internal static string? GetSubAgentExposureDenial(
        ToolDescriptor descriptor,
        ToolExecutionContext context)
    {
        if (context.ExecutionIdentity?.Kind != RuntimeExecutionKind.SubAgent)
            return null;

        if (descriptor.SubAgentExposure == SubAgentExposure.MainAgentOnly)
        {
            return $"Tool '{descriptor.ToolId}' is main-agent-only and cannot be invoked by a sub-agent.";
        }

        if (descriptor.SubAgentExposure != SubAgentExposure.DelegatedSubAgent)
            return null;

        var depth = Math.Max(0, context.DelegationDepth ?? 0);
        var maxDepth = context.MaxDelegationDepth ?? 1;
        return context.AllowSubDelegation == true && depth < maxDepth
            ? null
            : $"Tool '{descriptor.ToolId}' requires explicit sub-delegation permission " +
              $"below max depth (depth={depth}, maxDepth={maxDepth}).";
    }

    private async Task RecordTelemetryAsync(
        string toolId,
        string argumentsJson,
        ToolExecutionContext context,
        DateTimeOffset startedAt,
        ToolExecutionResult result,
        string stage,
        CancellationToken ct)
    {
        if (_telemetrySink is null)
            return;

        try
        {
            var durationMs = Math.Max(0, (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);
            var status = result.Success
                ? TelemetryMetricStatuses.Succeeded
                : TelemetryMetricStatuses.Failed;
            var trace = context.Trace ?? RuntimeTraceContext.CreateNew(
                sessionId: context.SessionId,
                workspaceId: context.WorkspaceId);
            var outputCharCount = result.Output?.Length ?? 0;
            var errorCharCount = result.Error?.Length ?? 0;
            var outputLineCount = CountLines(result.Output);
            var errorLineCount = CountLines(result.Error);
            var totalTextCharCount = outputCharCount + errorCharCount;
            var totalTextLineCount = outputLineCount + errorLineCount;

            await _telemetrySink.RecordAsync(new TelemetryMetric
            {
                Trace = trace,
                Source = "runtime",
                Category = TelemetryMetricCategories.Tool,
                Name = "tool.execution",
                Status = status,
                OccurredAtUtc = startedAt,
                DurationMs = durationMs,
                CountValue = 1,
                Unit = "call",
                Severity = result.Success ? "info" : "error",
                Summary = result.Success
                    ? $"Tool '{toolId}' executed successfully."
                    : $"Tool '{toolId}' failed at {stage}.",
                Dimensions = new Dictionary<string, string>
                {
                    ["tool_name"] = toolId,
                    ["agent_instance_id"] = context.AgentInstanceId,
                    ["agent_template_id"] = context.AgentTemplateId ?? "",
                    ["stage"] = RuntimePipelineStages.Tool,
                    ["tool_stage"] = stage,
                    ["exit_code"] = result.ExitCode.ToString(),
                    ["arguments_hash"] = ComputeSha256Hash(argumentsJson),
                    ["arguments_length"] = (argumentsJson?.Length ?? 0).ToString(),
                    ["output_length"] = (result.Output?.Length ?? 0).ToString(),
                    ["error_length"] = (result.Error?.Length ?? 0).ToString(),
                    ["output_char_count"] = outputCharCount.ToString(),
                    ["output_line_count"] = outputLineCount.ToString(),
                    ["error_char_count"] = errorCharCount.ToString(),
                    ["error_line_count"] = errorLineCount.ToString(),
                    ["total_text_char_count"] = totalTextCharCount.ToString(),
                    ["total_text_line_count"] = totalTextLineCount.ToString(),
                    ["output_size_level"] = ClassifyOutputSize(totalTextCharCount, totalTextLineCount),
                },
                ErrorCode = result.Success ? null : stage,
                ErrorMessage = result.Success ? null : Truncate(result.Error, 512),
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Telemetry is best-effort and must not alter cancellation behavior.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ToolExecution] Telemetry failed tool={Tool} stage={Stage}", toolId, stage);
        }
    }

    private static int CountLines(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return 0;

        var count = 1;
        foreach (var ch in value)
        {
            if (ch == '\n')
                count++;
        }

        return count;
    }

    private static string ClassifyOutputSize(int totalTextCharCount, int totalTextLineCount)
    {
        if (totalTextCharCount >= 32 * 1024 || totalTextLineCount >= 1000)
            return "critical";
        if (totalTextCharCount >= 8 * 1024 || totalTextLineCount >= 200)
            return "warning";
        return "normal";
    }

    private static string ComputeSha256Hash(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(hash);
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;
        return value[..maxLength];
    }
}

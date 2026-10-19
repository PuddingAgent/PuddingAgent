using System.Text.Json;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingAgent.HomeAssistant;
using PuddingHomeAssistant.Contracts;
using PuddingHomeAssistant.Diagnostics;
using PuddingHomeAssistant.Policy;
using PuddingHomeAssistant.Rest;

namespace PuddingAgent.Tools;

internal static class HaToolSupport
{
    public static string ResolveConfigAgent(ToolExecutionContext context) =>
        string.IsNullOrWhiteSpace(context.ConfigurationAgentInstanceId)
            ? context.AgentInstanceId
            : context.ConfigurationAgentInstanceId;

    public static CancellationTokenSource CreateDeadline(CancellationToken ct, TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return cts;
    }

    public static ToolExecutionResult Fail(HaClientException ex) =>
        ToolExecutionResult.Fail($"{ex.Code}: {ex.Message}", status: ex.Code);

    public static ToolExecutionResult FailNoInstance(string alias) =>
        ToolExecutionResult.Fail(
            $"ha.not_configured: 实例 '{alias}' 未配置。请先在 Admin 的 Home Assistant 设置中登记别名、地址与令牌。",
            status: HaErrorCodes.NotConfigured);

    public static string Serialize(object value) => JsonSerializer.Serialize(value, s_json);

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

// ───────────────────────────── ha_instances ─────────────────────────────

public sealed class HaInstancesArgs
{
    [ToolParam("Check each instance with GET /api/ and /api/config (default true).")]
    public bool CheckHealth { get; set; } = true;
}

[Tool(
    id: "ha_instances",
    name: "Home Assistant instances",
    description:
        "List the Home Assistant instances registered in Admin settings (alias, base URL, allowed opt-in domains) " +
        "and optionally health-check each one. Never returns the access token.",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.RequiresNetwork | ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 31)]
public sealed class HaInstancesTool(
    HaInstanceStore store,
    HaClientFactory factory,
    ILogger<HaInstancesTool> logger)
    : PuddingToolBase<HaInstancesArgs>
{
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        HaInstancesArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var agentId = HaToolSupport.ResolveConfigAgent(context);
        var instances = store.Load(agentId);
        if (instances.Count == 0)
        {
            return ToolExecutionResult.Ok(
                "[]",
                "no_instances_configured");
        }

        var client = factory.Create();
        var rows = new List<object>(instances.Count);

        foreach (var instance in instances)
        {
            string status = "unknown";
            string? version = null;

            if (args.CheckHealth)
            {
                try
                {
                    using var deadline = HaToolSupport.CreateDeadline(ct, TimeSpan.FromSeconds(10));
                    var api = await client.GetStatusAsync(instance, deadline.Token);
                    var config = await client.GetConfigAsync(instance, deadline.Token);
                    status = api.Message;
                    version = config.Version;
                }
                catch (HaClientException ex)
                {
                    status = ex.Code;
                    logger.LogWarning("Home Assistant 健康检查失败: alias={Alias} code={Code}", instance.Alias, ex.Code);
                }
            }

            rows.Add(new
            {
                alias = instance.Alias,
                baseUrl = instance.BaseUrl.ToString(),
                hasToken = !string.IsNullOrEmpty(instance.Token),
                optInDomains = instance.OptInDomains.OrderBy(d => d, StringComparer.Ordinal).ToArray(),
                status,
                version,
            });
        }

        return ToolExecutionResult.Ok(HaToolSupport.Serialize(rows));
    }
}

// ───────────────────────────── ha_states ─────────────────────────────

public sealed class HaStatesArgs
{
    [ToolParam("Instance alias registered in Admin. Omit when only one instance exists.")]
    public string? Instance { get; set; }

    [ToolParam("Domain filter, e.g. light, switch, sensor.")]
    public string? Domain { get; set; }

    [ToolParam("Exact entity id, e.g. light.study. Takes priority over domain.")]
    public string? EntityId { get; set; }

    [ToolParam("Maximum items to return (default 50, max 500).")]
    public int? Limit { get; set; }

    [ToolParam("Include full attributes. Default false because attributes can be large or sensitive.")]
    public bool IncludeAttributes { get; set; }
}

[Tool(
    id: "ha_states",
    name: "Home Assistant states",
    description:
        "Read entity states from Home Assistant. Filter by domain or exact entity id, project fields, " +
        "and get an explicit total/returned/truncated envelope. Every item carries the HA friendly name " +
        "(`name`) so opaque entity ids stay identifiable; attributes are omitted unless requested.",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.RequiresNetwork | ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 32)]
public sealed class HaStatesTool(
    HaInstanceStore store,
    HaClientFactory factory,
    ILogger<HaStatesTool> logger)
    : PuddingToolBase<HaStatesArgs>
{
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        HaStatesArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var agentId = HaToolSupport.ResolveConfigAgent(context);
        var instance = store.Find(agentId, args.Instance);
        if (instance is null)
        {
            return HaToolSupport.FailNoInstance(args.Instance ?? "(default)");
        }

        try
        {
            using var deadline = HaToolSupport.CreateDeadline(ct, TimeSpan.FromSeconds(20));
            var client = factory.Create();
            var all = await client.GetStatesAsync(instance, deadline.Token);

            var page = HaOutputBudget.Project(
                all,
                HaOutputBudget.NormalizeLimit(args.Limit),
                fields: args.IncludeAttributes ? ["attributes"] : null,
                domains: string.IsNullOrWhiteSpace(args.Domain) ? null : [args.Domain!],
                entityId: args.EntityId);

            var payload = new
            {
                instance = instance.Alias,
                total = page.Total,
                returned = page.Returned,
                truncated = page.Truncated,
                estimatedBytes = page.EstimatedBytes,
                items = page.Items.Select(i => new
                {
                    entityId = i.EntityId,
                    state = i.State,
                    lastChanged = i.LastChanged,
                    lastUpdated = i.LastUpdated,
                    attributes = i.Attributes,
                }),
            };

            return ToolExecutionResult.Ok(HaToolSupport.Serialize(payload));
        }
        catch (HaClientException ex)
        {
            logger.LogWarning("ha_states 失败: alias={Alias} code={Code}", instance.Alias, ex.Code);
            return HaToolSupport.Fail(ex);
        }
    }
}

// ───────────────────────────── ha_history ─────────────────────────────

public sealed class HaHistoryArgs
{
    [ToolParam("Instance alias registered in Admin.")]
    public string? Instance { get; set; }

    [ToolParam("Entity ids to query (1-10), e.g. [\"sensor.temperature\"].")]
    public List<string>? EntityIds { get; set; }

    [ToolParam("Hours to look back (default 24, max 168).")]
    public int? Hours { get; set; }
}

[Tool(
    id: "ha_history",
    name: "Home Assistant history",
    description:
        "Query state history for up to 10 entities over a bounded look-back window (default 24h, max 168h). " +
        "Response is capped and reports truncation.",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.RequiresNetwork | ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 33)]
public sealed class HaHistoryTool(
    HaInstanceStore store,
    HaClientFactory factory,
    ILogger<HaHistoryTool> logger)
    : PuddingToolBase<HaHistoryArgs>
{
    private const int MaxEntities = 10;
    private const int MaxHistoryBytes = 256 * 1024;

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        HaHistoryArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var entityIds = (args.EntityIds ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxEntities)
            .ToArray();

        if (entityIds.Length == 0)
        {
            return ToolExecutionResult.Fail("ha.invalid_request: entityIds is required (1-10).", status: HaErrorCodes.InvalidRequest);
        }

        var agentId = HaToolSupport.ResolveConfigAgent(context);
        var instance = store.Find(agentId, args.Instance);
        if (instance is null)
        {
            return HaToolSupport.FailNoInstance(args.Instance ?? "(default)");
        }

        var hours = Math.Clamp(args.Hours ?? 24, 1, 168);

        try
        {
            using var deadline = HaToolSupport.CreateDeadline(ct, TimeSpan.FromSeconds(30));
            var client = factory.Create();
            var history = await client.GetHistoryAsync(
                instance,
                entityIds,
                DateTimeOffset.UtcNow.AddHours(-hours),
                DateTimeOffset.UtcNow,
                deadline.Token);

            // 历史响应可达数十万字节。按 entity / state 粒度裁剪，而不是逐字符截断字符串：
            // 后者会把 JSON 切成半个对象，调用方拿到无法解析的文本；并且 `data` 直接放字符串
            // 会导致 JSON 被二次转义（`\u0022` 膨胀约 6 倍）。
            var trimmed = HaHistoryBudget.Trim(history, MaxHistoryBytes);

            var payload = new
            {
                instance = instance.Alias,
                hours,
                entityIds,
                truncated = trimmed.Truncated,
                entityCount = trimmed.EntityCount,
                keptEntities = trimmed.KeptEntities,
                droppedEntities = trimmed.DroppedEntities,
                estimatedBytes = trimmed.EstimatedBytes,
                data = trimmed.Data,
            };

            return ToolExecutionResult.Ok(
                HaToolSupport.Serialize(payload),
                trimmed.Truncated ? HaErrorCodes.OutputTruncated : null);
        }
        catch (HaClientException ex)
        {
            logger.LogWarning("ha_history 失败: alias={Alias} code={Code}", instance.Alias, ex.Code);
            return HaToolSupport.Fail(ex);
        }
    }
}

// ───────────────────────────── ha_call_service ─────────────────────────────

public sealed class HaCallServiceArgs
{
    [ToolParam("Instance alias registered in Admin.")]
    public string? Instance { get; set; }

    [ToolParam("Service domain, e.g. light, switch, media_player.")]
    public string? Domain { get; set; }

    [ToolParam("Service name, e.g. turn_on, turn_off, toggle.")]
    public string? Service { get; set; }

    [ToolParam("Target entity ids.")]
    public List<string>? EntityIds { get; set; }

    [ToolParam("Additional service data (JSON object), e.g. {\"brightness\":128}.")]
    public JsonElement? Data { get; set; }

    [ToolParam("Ask Home Assistant to return response data (?return_response). Only for actions that support it.")]
    public bool ReturnResponse { get; set; }
}

[Tool(
    id: "ha_call_service",
    name: "Home Assistant call service",
    description:
        "Call a Home Assistant action (service). Domain policy applies before any HTTP request: " +
        "shell_command/hassio/homeassistant/script/lock/alarm_control_panel/camera are permanently blocked; " +
        "media_player/fan/climate/cover/vacuum require the user to enable them in Admin settings.",
    category: ToolCategory.Execute,
    permission: ToolPermissionLevel.Medium,
    safety: ToolSafetyFlags.Destructive | ToolSafetyFlags.RequiresNetwork,
    SortOrder = 34)]
public sealed class HaCallServiceTool(
    HaInstanceStore store,
    HaClientFactory factory,
    ILogger<HaCallServiceTool> logger)
    : PuddingToolBase<HaCallServiceArgs>
{
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        HaCallServiceArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Domain) || string.IsNullOrWhiteSpace(args.Service))
        {
            return ToolExecutionResult.Fail(
                "ha.invalid_request: domain and service are required.",
                status: HaErrorCodes.InvalidRequest);
        }

        var agentId = HaToolSupport.ResolveConfigAgent(context);
        var instance = store.Find(agentId, args.Instance);
        if (instance is null)
        {
            return HaToolSupport.FailNoInstance(args.Instance ?? "(default)");
        }

        IReadOnlyDictionary<string, object?>? data = null;
        if (args.Data is { ValueKind: JsonValueKind.Object } element)
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                dict[property.Name] = JsonSerializer.Deserialize<object?>(property.Value.GetRawText());
            }

            data = dict;
        }

        try
        {
            using var deadline = HaToolSupport.CreateDeadline(ct, TimeSpan.FromSeconds(30));
            var client = factory.Create();
            var result = await client.CallServiceAsync(
                instance,
                new HaServiceCall(
                    args.Domain,
                    args.Service,
                    args.EntityIds?.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray(),
                    data,
                    args.ReturnResponse),
                deadline.Token);

            var payload = new
            {
                instance = instance.Alias,
                domain = args.Domain,
                service = args.Service,
                changedStates = result.ChangedStateCount,
                response = result.Response,
            };

            return ToolExecutionResult.Ok(HaToolSupport.Serialize(payload));
        }
        catch (HaClientException ex)
        {
            logger.LogWarning(
                "ha_call_service 被拒绝或失败: alias={Alias} domain={Domain} code={Code}",
                instance.Alias, args.Domain, ex.Code);
            return HaToolSupport.Fail(ex);
        }
    }
}

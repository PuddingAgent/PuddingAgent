using System.Text.Json;
using System.Text.Json.Serialization;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// LLM 路由表查询工具 — 列出资源池中 provider/model 的完整路由，
/// 供 Agent 在 spawn_sub_agent 等调用点填写正确的 'providerId/modelId'。
///
/// 安全合同：输出严禁包含 apiKey/baseUrl 等敏感字段。数据只来自
/// ILlmConfigService 的内存快照（启动时由 llm.providers.json 加载），
/// 契约记录本身不携带密钥；baseUrl 属于 LlmProviderInfo 但在此处不序列化。
/// </summary>
[Tool(
    id: "list_llm_providers",
    name: "list_llm_providers",
    description: "列出 LLM 资源池的 provider/model 路由表（来源 data/config/llm.providers.json，启动时加载）。" +
                 "调用 spawn_sub_agent 等需要 model 路由的工具前，先用本工具查到条目的 route 字段（providerId/modelId）再填写，" +
                 "裸 modelId 命中多 provider 注册时会报 'exists under multiple providers'。" +
                 "输出含 protocol/capabilityTags/价格/isEnabled/isDeprecated，" +
                 "ambiguous_model_ids 标出重复注册的 modelId。输出不含 apiKey/baseUrl 等敏感字段。",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly)]
public sealed class ListLlmProvidersTool : PuddingToolBase<ListLlmProvidersArgs>
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILlmConfigService? _llmConfigService;

    public ListLlmProvidersTool(ILlmConfigService? llmConfigService = null)
    {
        _llmConfigService = llmConfigService;
    }

    protected override Task<ToolExecutionResult> ExecuteCoreAsync(
        ListLlmProvidersArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_llmConfigService is null)
            return Task.FromResult(ToolExecutionResult.Fail(
                "LLM config service is not available in this environment."));

        var enabledProviders = _llmConfigService.GetEnabledProviders();
        var enabledProviderIds = enabledProviders
            .Select(p => p.ProviderId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allModels = _llmConfigService.GetAllModels();

        // 歧义判定与 FileLlmResolver 同语义：裸 modelId 只在
        // 「provider 启用 且 模型未弃用」的集合上匹配，命中多于一个才报错。
        var ambiguousModelIds = allModels
            .Where(m => !m.IsDeprecated && enabledProviderIds.Contains(m.ProviderId))
            .GroupBy(m => m.ModelId, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var providerNameById = new Dictionary<string, string?>(
            enabledProviders.Select(p => KeyValuePair.Create<string, string?>(p.ProviderId, p.Name)),
            StringComparer.OrdinalIgnoreCase);

        var (providerFilter, modelFilter) = SplitRouteFilter(args.ProviderId, args.ModelId);
        var requiredTags = args.Capability?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(tag => tag.Length > 0)
            .ToArray();

        var includeDisabled = args.IncludeDisabled == true;
        var includeDeprecated = args.IncludeDeprecated == true;
        var includeEmbeddings = args.IncludeEmbeddings == true;

        var models = allModels
            .Where(m => includeDeprecated || !m.IsDeprecated)
            .Where(m => includeEmbeddings || !m.IsEmbedding)
            .Where(m => includeDisabled || enabledProviderIds.Contains(m.ProviderId))
            .Where(m => providerFilter is null
                        || string.Equals(m.ProviderId, providerFilter, StringComparison.OrdinalIgnoreCase))
            .Where(m => modelFilter is null
                        || string.Equals(m.ModelId, modelFilter, StringComparison.OrdinalIgnoreCase))
            .Where(m => requiredTags is null || requiredTags.All(tag =>
                m.CapabilityTags.Contains(tag, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(m => m.ProviderId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.SortOrder)
            .ThenBy(m => m.ModelId, StringComparer.OrdinalIgnoreCase)
            .Select(m => new
            {
                providerId = m.ProviderId,
                modelId = m.ModelId,
                route = $"{m.ProviderId}/{m.ModelId}",
                name = m.Name,
                protocol = m.Protocol,
                capabilityTags = m.CapabilityTags,
                pricePer1MTokens = new
                {
                    input = m.InputPricePer1MTokens,
                    output = m.OutputPricePer1MTokens,
                    cacheHit = m.CacheHitPricePer1MTokens,
                },
                isEnabled = enabledProviderIds.Contains(m.ProviderId),
                isDefault = m.IsDefault,
                isDeprecated = m.IsDeprecated,
                isEmbedding = m.IsEmbedding,
                isAmbiguous = ambiguousModelIds.Contains(m.ModelId),
                maxContextTokens = m.MaxContextTokens,
                maxOutputTokens = m.MaxOutputTokens,
                sortOrder = m.SortOrder,
            })
            .ToList();

        // providers 只覆盖当前可见模型所属的 provider；禁用 provider 的
        // 名称不在 GetEnabledProviders 契约内，name 置 null 而不是猜测。
        var visibleProviderIds = models
            .Select(m => m.providerId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var providers = visibleProviderIds
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Select(id => new
            {
                providerId = id,
                name = providerNameById.GetValueOrDefault(id),
                isEnabled = enabledProviderIds.Contains(id),
            })
            .ToList();

        var output = JsonSerializer.Serialize(new
        {
            schema = "pudding-llm-routes",
            version = 1,
            usage = "spawn_sub_agent 等工具的 model 参数请直接使用条目的 route 字段（providerId/modelId 完整格式）；" +
                    "ambiguous_model_ids 中的 modelId 不可裸填，会报 exists under multiple providers。",
            ambiguousModelIds = ambiguousModelIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray(),
            providers,
            models,
        }, JsonOpts);

        var status = models.Count == 0 ? ToolResultStatuses.NoMatch : null;
        return Task.FromResult(ToolExecutionResult.Ok(output, status));
    }

    /// <summary>
    /// model_id 允许传完整路由（providerId/modelId）；此时把它当作
    /// provider + model 的联合过滤，而不是死板的字符串全等。
    /// </summary>
    private static (string? ProviderId, string? ModelId) SplitRouteFilter(
        string? providerId, string? modelId)
    {
        if (!string.IsNullOrWhiteSpace(modelId) && modelId.Contains('/'))
        {
            var parts = modelId.Split('/', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2
                && !string.IsNullOrWhiteSpace(parts[0])
                && !string.IsNullOrWhiteSpace(parts[1]))
            {
                return string.IsNullOrWhiteSpace(providerId)
                    ? (parts[0], parts[1])
                    : (providerId, parts[1]);
            }
        }

        return (
            string.IsNullOrWhiteSpace(providerId) ? null : providerId.Trim(),
            string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim());
    }
}

public sealed record ListLlmProvidersArgs
{
    [ToolParam("可选：只列出该 providerId 下的模型（不区分大小写）")]
    public string? ProviderId { get; init; }

    [ToolParam("可选：按 modelId 精确过滤（不区分大小写），也接受 providerId/modelId 完整路由")]
    public string? ModelId { get; init; }

    [ToolParam("可选：按 capabilityTags 过滤，逗号分隔多个时要求全部满足（不区分大小写）")]
    public string? Capability { get; init; }

    [ToolParam("是否包含已禁用 provider 的模型（默认 false）")]
    public bool? IncludeDisabled { get; init; }

    [ToolParam("是否包含已弃用模型（默认 false）")]
    public bool? IncludeDeprecated { get; init; }

    [ToolParam("是否包含 embedding 模型（默认 false；embedding 不能作为 spawn_sub_agent 聊天路由）")]
    public bool? IncludeEmbeddings { get; init; }
}

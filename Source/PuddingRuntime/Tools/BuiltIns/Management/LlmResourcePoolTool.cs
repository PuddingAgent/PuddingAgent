using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingPlatform.Services;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// LlmResourcePoolTool — 返回系统支持的 LLM 服务商和对应模型的信息。
/// 包含：服务商名称、协议、BaseUrl、模型名称、上下文窗口大小、能力标签、价格等。
/// 不包含：API 密钥等敏感信息。
///
/// 使用场景：
///   · Agent 需要了解可用的模型列表和能力
///   · Smart* 工具自动选择合适模型（配合能力标签系统）
///   · 管理后台展示 LLM 资源池
/// </summary>
[Tool(
    id: "llm_resource_pool",
    name: "LLM Resource Pool",
    description: "返回系统支持的 LLM 服务商和对应模型的信息。" +
                 "包含服务商名称、模型名称/类型/上下文窗口大小/能力标签/价格等。" +
                 "不包含 API 密钥等敏感信息。" +
                 "参数：provider_id（可选，筛选特定服务商）、" +
                 "model_id（可选，筛选特定模型）、" +
                 "include_deprecated（可选，是否包含已废弃模型，默认 false）。",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SubAgentExposure = SubAgentExposure.MainAgentOnly)]
public sealed class LlmResourcePoolTool : PuddingToolBase<LlmResourcePoolArgs>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<LlmResourcePoolTool> _logger;

    public LlmResourcePoolTool(
        IServiceProvider serviceProvider,
        ILogger<LlmResourcePoolTool> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        LlmResourcePoolArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        try
        {
            var fileService = _serviceProvider.GetRequiredService<LlmProviderFileService>();
            var config = await fileService.LoadAsync(ct);

            // 构建响应
            var response = new LlmResourcePoolResponse
            {
                Providers = config.Providers
                    .Where(p => p.IsEnabled)
                    .Select(p => MapProvider(p, args))
                    .Where(p => p.Models.Count > 0) // 过滤无匹配模型的 Provider
                    .ToList(),
                TotalProviders = config.Providers.Count(p => p.IsEnabled),
                TotalModels = config.Providers
                    .Where(p => p.IsEnabled)
                    .SelectMany(p => p.Models)
                    .Count(m => !m.IsDeprecated),
            };

            var json = JsonSerializer.Serialize(response, JsonOpts);
            return ToolExecutionResult.Ok(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LlmResourcePool] agent={Agent} error", context.AgentInstanceId);
            return ToolExecutionResult.Fail($"LLM resource pool query failed: {ex.Message}");
        }
    }

    private static LlmProviderSummary MapProvider(
        PuddingCode.Configuration.PuddingLlmProviderConfig provider,
        LlmResourcePoolArgs args)
    {
        var includeDeprecated = args.IncludeDeprecated ?? false;

        var models = provider.Models
            .Where(m => includeDeprecated || !m.IsDeprecated)
            .Where(m => string.IsNullOrWhiteSpace(args.ModelId)
                || string.Equals(m.ModelId, args.ModelId, StringComparison.OrdinalIgnoreCase))
            .Select(m => new LlmModelSummary
            {
                ModelId = m.ModelId,
                Name = m.Name,
                MaxContextTokens = m.MaxContextTokens ?? 0,
                MaxOutputTokens = m.MaxOutputTokens ?? 0,
                CapabilityTags = m.CapabilityTags ?? [],
                Pricing = m.PricePer1MInputTokens.HasValue || m.PricePer1MOutputTokens.HasValue
                    ? new LlmModelPricing
                    {
                        InputPer1MTokens = m.PricePer1MInputTokens ?? 0,
                        OutputPer1MTokens = m.PricePer1MOutputTokens ?? 0,
                        CacheHitPer1MTokens = m.PricePer1MCacheHitTokens,
                    }
                    : null,
                IsDefault = m.IsDefault,
                IsDeprecated = m.IsDeprecated,
                IsEmbedding = m.IsEmbedding,
                SortOrder = m.SortOrder,
                MaxConcurrentRequests = m.MaxConcurrentRequests,
            })
            .OrderBy(m => m.SortOrder)
            .ThenBy(m => m.ModelId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new LlmProviderSummary
        {
            ProviderId = provider.ProviderId,
            Name = provider.Name,
            Protocol = provider.Protocol,
            BaseUrl = provider.BaseUrl,
            IsEnabled = provider.IsEnabled,
            Models = models,
        };
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>LLM 资源池查询参数。</summary>
public sealed class LlmResourcePoolArgs
{
    /// <summary>筛选特定服务商（可选）。不指定则返回全部。</summary>
    [ToolParam("筛选特定服务商（可选），如 'deepseek'。不指定则返回全部已启用的服务商。")]
    public string? ProviderId { get; set; }

    /// <summary>筛选特定模型（可选）。支持模糊匹配模型 ID。</summary>
    [ToolParam("筛选特定模型（可选），如 'deepseek-v4-flash'。支持模糊匹配。")]
    public string? ModelId { get; set; }

    /// <summary>是否包含已废弃的模型（可选，默认 false）。</summary>
    [ToolParam("是否包含已废弃的模型（可选，默认 false）。设为 true 可查看历史模型。")]
    public bool? IncludeDeprecated { get; set; }
}

// ── 响应模型（不含敏感信息）──────────────────────────────────

/// <summary>LLM 资源池完整响应。</summary>
public sealed record LlmResourcePoolResponse
{
    public List<LlmProviderSummary> Providers { get; init; } = [];
    public int TotalProviders { get; init; }
    public int TotalModels { get; init; }
}

/// <summary>Provider 摘要（不含 ApiKey）。</summary>
public sealed record LlmProviderSummary
{
    public string ProviderId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Protocol { get; init; } = "openai";
    public string BaseUrl { get; init; } = "";
    public bool IsEnabled { get; init; }
    public List<LlmModelSummary> Models { get; init; } = [];
}

/// <summary>Model 摘要（含能力标签和价格，不含密钥）。</summary>
public sealed record LlmModelSummary
{
    public string ModelId { get; init; } = "";
    public string Name { get; init; } = "";
    public int MaxContextTokens { get; init; }
    public int MaxOutputTokens { get; init; }
    public List<string> CapabilityTags { get; init; } = [];

    /// <summary>Token 定价（美元/百万 token），可能为 null（未配置价格）。</summary>
    public LlmModelPricing? Pricing { get; init; }

    public bool IsDefault { get; init; }
    public bool IsDeprecated { get; init; }
    public bool IsEmbedding { get; init; }
    public int SortOrder { get; init; }
    public int? MaxConcurrentRequests { get; init; }
}

/// <summary>模型定价（美元/百万 token）。</summary>
public sealed record LlmModelPricing
{
    public decimal InputPer1MTokens { get; init; }
    public decimal OutputPer1MTokens { get; init; }
    public decimal? CacheHitPer1MTokens { get; init; }
}

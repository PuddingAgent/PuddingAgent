using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services;

/// <summary>
/// LLM 配置解析器实现：
/// - 从 GlobalAgentTemplates + WorkspaceAgentTemplates 读取 LLM 路由配置
/// - 不受 IsEnabled 限制——只过滤 TemplateId 匹配
/// - 回退链路：Workspace → Global → 环境变量 → 显意识 LLM
/// </summary>
public sealed class AgentLLMConfigResolver : ILLMConfigResolver
{
    private readonly IDbContextFactory<PlatformDbContext> _dbFactory;
    private readonly ILogger<AgentLLMConfigResolver> _logger;

    public AgentLLMConfigResolver(
        IDbContextFactory<PlatformDbContext> dbFactory,
        ILogger<AgentLLMConfigResolver> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<LlmRoutingConfig?> ResolveConsciousAsync(
        string templateId,
        string? workspaceId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(templateId))
            return null;

        var canonicalId = NormalizeTemplateId(templateId);
        if (string.IsNullOrWhiteSpace(canonicalId))
            return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var global = await db.GlobalAgentTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TemplateId == canonicalId, ct);

        var ws = !string.IsNullOrWhiteSpace(workspaceId)
            ? await db.WorkspaceAgentTemplates
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.TemplateId == canonicalId && t.WorkspaceId == workspaceId, ct)
            : null;

        return new LlmRoutingConfig
        {
            ProviderId = ws?.PreferredProviderId ?? global?.PreferredProviderId,
            ModelId = ws?.PreferredModelId ?? global?.PreferredModelId,
            Endpoint = Environment.GetEnvironmentVariable("LLM_ENDPOINT"),
            ApiKey = Environment.GetEnvironmentVariable("LLM_API_KEY"),
        };
    }

    public async Task<MemoryLlmRoutingConfig?> ResolveMemoryAsync(
        string templateId,
        string? workspaceId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(templateId))
            return null;

        var canonicalId = NormalizeTemplateId(templateId);
        if (string.IsNullOrWhiteSpace(canonicalId))
            return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // 全局模板
        var global = await db.GlobalAgentTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TemplateId == canonicalId, ct);

        // 工作区覆盖
        var ws = !string.IsNullOrWhiteSpace(workspaceId)
            ? await db.WorkspaceAgentTemplates
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.TemplateId == canonicalId && t.WorkspaceId == workspaceId, ct)
            : null;

        // 四级回退：Workspace → Global → Environment → null
        var endpoint = ws?.MemoryLlmEndpoint
                       ?? global?.MemoryLlmEndpoint
                       ?? Environment.GetEnvironmentVariable("MEMORY_LLM_ENDPOINT");

        var apiKey = ws?.MemoryLlmApiKey
                     ?? global?.MemoryLlmApiKey
                     ?? Environment.GetEnvironmentVariable("MEMORY_LLM_API_KEY");

        var modelId = ws?.MemoryLlmModelId
                      ?? global?.MemoryLlmModelId
                      ?? Environment.GetEnvironmentVariable("MEMORY_LLM_MODEL_ID");

        var searchMode = ws?.MemorySearchMode
                         ?? global?.MemorySearchMode
                         ?? "deep";

        _logger.LogDebug(
            "[LLMConfig] Resolved memory config template={Template} ws={Workspace} endpoint={HasEndpoint} model={Model} mode={Mode}",
            canonicalId, workspaceId, !string.IsNullOrWhiteSpace(endpoint), modelId, searchMode);

        return new MemoryLlmRoutingConfig
        {
            Endpoint = endpoint,
            ApiKey = apiKey,
            ModelId = modelId,
            SearchMode = searchMode,
        };
    }

    private static string NormalizeTemplateId(string templateId)
    {
        const string globalPrefix = "global:";
        return templateId.StartsWith(globalPrefix, StringComparison.OrdinalIgnoreCase)
            ? templateId[globalPrefix.Length..]
            : templateId;
    }
}

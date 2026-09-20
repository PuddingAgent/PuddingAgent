using System.Text.RegularExpressions;
using PuddingCode.Abstractions;

namespace PuddingRuntime.Services;

/// <summary>
/// <see cref="IJevDecisionOptionsProvider"/> 的默认实现：<b>资源池优先</b>，配置节/环境变量为回退。
/// <para>
/// 解析优先级：
/// <list type="number">
/// <item><b>资源池</b>（<c>data/config/llm.providers.json</c>）—— 端点为 provider <c>jev</c> 的 baseUrl，
///       模型为该 provider 下首个未废弃模型（可按 sortOrder 选），可通过 <c>Jev:ProviderId</c> 改 providerId；</item>
/// <item>回退：<c>Jev:BaseUrl</c>（或 <c>Jev:Endpoint</c>）→ <c>JEV_BASE_URL</c>；
///       <c>Jev:ModelId</c> → <c>JEV_MODEL_ID</c> → <c>jev-latest</c>。</item>
/// </list>
/// 密钥解析链：<b>资源池 provider 的 apiKeyRef（→ LlmConfig.KeyVaultId，经 <see cref="IKeyVaultService"/>）</b>
/// → <c>Jev:ApiKey</c> / <c>JEV_API_KEY</c>（支持 <c>${ENV_NAME}</c> 占位展开）
/// → <c>Jev:ApiKeyRef</c> / <c>JEV_API_KEY_REF</c> 经 KeyVault。
/// </para>
/// <para>
/// 端点或密钥缺失时 fail-closed 抛 <see cref="JevDecisionException"/>（<see cref="JevDecisionCodes.NotConfigured"/>），
/// 不返回空端点；密钥明文绝不写日志。刻意只读 <see cref="LlmConfig.KeyVaultId"/>，
/// 不引用已标 [Obsolete] 的明文 <see cref="LlmConfig.ApiKey"/>。
/// </para>
/// </summary>
public sealed class JevDecisionOptionsProvider(
    IConfiguration configuration,
    ILlmConfigService? llmConfigService = null,
    ILogger<JevDecisionOptionsProvider>? logger = null,
    IKeyVaultService? keyVaultService = null) : IJevDecisionOptionsProvider
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "Jev";

    /// <summary>资源池（llm.providers.json）中 Jev 的 providerId。</summary>
    public const string ProviderId = "jev";

    private const string ProviderIdKey = SectionName + ":ProviderId";
    private const string BaseUrlKey = SectionName + ":BaseUrl";
    private const string EndpointKey = SectionName + ":Endpoint";
    private const string ApiKeyKey = SectionName + ":ApiKey";
    private const string ApiKeyRefKey = SectionName + ":ApiKeyRef";
    private const string ModelIdKey = SectionName + ":ModelId";
    private const string MaxRetriesKey = SectionName + ":MaxRetries";
    private const string RetryDelayKey = SectionName + ":RetryDelayMilliseconds";

    private const string BaseUrlEnvironmentVariable = "JEV_BASE_URL";
    private const string ApiKeyEnvironmentVariable = "JEV_API_KEY";
    private const string ApiKeyRefEnvironmentVariable = "JEV_API_KEY_REF";
    private const string ModelIdEnvironmentVariable = "JEV_MODEL_ID";

    public async Task<JevDecisionOptions> GetOptionsAsync(CancellationToken ct = default)
    {
        // ── 资源池（llm.providers.json）—— 端点/模型的权威来源 ──
        var pool = ResolveFromResourcePool();

        var baseUrl = (pool.BaseUrl
            ?? configuration[BaseUrlKey]
            ?? configuration[EndpointKey]
            ?? Environment.GetEnvironmentVariable(BaseUrlEnvironmentVariable)
            ?? string.Empty).Trim();

        var modelId = pool.ModelId
            ?? configuration[ModelIdKey]
            ?? Environment.GetEnvironmentVariable(ModelIdEnvironmentVariable)
            ?? JevDecisionOptions.DefaultModelId;

        // ── 密钥：池 KeyVaultId → 显式配置/${ENV} → 配置 ApiKeyRef 经 KeyVault ──
        var apiKey = string.Empty;
        if (!string.IsNullOrWhiteSpace(pool.KeyVaultId))
            apiKey = await ResolveSecretAsync(pool.KeyVaultId!, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = ExpandEnvironmentPlaceholders(
                configuration[ApiKeyKey]
                ?? Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable)
                ?? string.Empty);
        }

        if (string.IsNullOrWhiteSpace(apiKey))
            apiKey = await ResolveApiKeyFromVaultAsync(ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            throw new JevDecisionException(
                JevDecisionCodes.NotConfigured,
                $"Jev 未配置：请在资源池 llm.providers.json 添加 providerId=\"{ProviderId}\" 的 provider"
                + $"（baseUrl + apiKey/apiKeyRef），或提供 {BaseUrlKey}（或 {BaseUrlEnvironmentVariable}）与 "
                + $"{ApiKeyKey}（或 {ApiKeyEnvironmentVariable}），密钥亦可经 "
                + $"{ApiKeyRefKey} 走 KeyVault。");
        }

        if (baseUrl.EndsWith("/api/v1/decide", StringComparison.OrdinalIgnoreCase))
            baseUrl = baseUrl[..^"/api/v1/decide".Length];

        return new JevDecisionOptions
        {
            BaseUrl = baseUrl.TrimEnd('/'),
            ApiKey = apiKey.Trim(),
            ModelId = string.IsNullOrWhiteSpace(modelId)
                ? JevDecisionOptions.DefaultModelId
                : modelId.Trim(),
            MaxRetries = configuration.GetValue<int?>(MaxRetriesKey) ?? 2,
            RetryDelayMilliseconds = configuration.GetValue<int?>(RetryDelayKey) ?? 500,
        };
    }

    /// <summary>
    /// 从资源池解析 Jev 的 baseUrl / 缺省模型 / KeyVaultId。
    /// 池中无（或未启用）jev provider 时三项均为 null，调用方回退到配置节与环境变量。
    /// 注意：刻意只读 <see cref="LlmConfig.KeyVaultId"/> 而不碰已标 [Obsolete] 的明文 ApiKey。
    /// </summary>
    private (string? BaseUrl, string? ModelId, string? KeyVaultId) ResolveFromResourcePool()
    {
        if (llmConfigService is null)
            return (null, null, null);

        var providerId = configuration[ProviderIdKey]?.Trim();
        if (string.IsNullOrWhiteSpace(providerId))
            providerId = ProviderId;

        var provider = llmConfigService.GetEnabledProviders()
            .FirstOrDefault(candidate => string.Equals(
                candidate.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
            return (null, null, null);

        var modelId = llmConfigService.GetAllModels()
            .Where(model => string.Equals(
                model.ProviderId, provider.ProviderId, StringComparison.OrdinalIgnoreCase))
            .Where(model => !model.IsDeprecated)
            .OrderBy(model => model.SortOrder)
            .ThenBy(model => model.ModelId, StringComparer.OrdinalIgnoreCase)
            .Select(model => model.ModelId)
            .FirstOrDefault();

        var keyVaultId = string.IsNullOrWhiteSpace(modelId)
            ? null
            : llmConfigService.Resolve(provider.ProviderId, modelId)?.KeyVaultId;

        return (
            string.IsNullOrWhiteSpace(provider.BaseUrl) ? null : provider.BaseUrl,
            string.IsNullOrWhiteSpace(modelId) ? null : modelId,
            string.IsNullOrWhiteSpace(keyVaultId) ? null : keyVaultId);
    }

    private async Task<string> ResolveSecretAsync(string keyVaultId, CancellationToken ct)
    {
        if (keyVaultService is null)
            return string.Empty;

        try
        {
            var secret = await keyVaultService
                .GetSecretAsync(keyVaultId, includePlainText: true, ct)
                .ConfigureAwait(false);
            return secret?.Value ?? string.Empty;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex, "[Jev] 资源池 KeyVaultId {KeyVaultId} 解析失败，回退到配置/环境变量。", keyVaultId);
            return string.Empty;
        }
    }

    private async Task<string> ResolveApiKeyFromVaultAsync(CancellationToken ct)
    {
        var reference = configuration[ApiKeyRefKey]
            ?? Environment.GetEnvironmentVariable(ApiKeyRefEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(reference) || keyVaultService is null)
            return string.Empty;

        try
        {
            var secret = await keyVaultService
                .GetSecretAsync(reference, includePlainText: true, ct)
                .ConfigureAwait(false);
            return secret?.Value ?? string.Empty;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex, "[Jev] KeyVault 引用 {Reference} 解析失败，回退为未配置。", reference);
            return string.Empty;
        }
    }

    /// <summary>展开 <c>${ENV_NAME}</c> 占位（与资源池配置同语义），未定义的环境变量展开为空串。</summary>
    private static string ExpandEnvironmentPlaceholders(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("${", StringComparison.Ordinal))
            return value;

        return Regex.Replace(
            value,
            @"\$\{([A-Za-z0-9_]+)\}",
            match => Environment.GetEnvironmentVariable(match.Groups[1].Value) ?? string.Empty);
    }
}

using System.Text.RegularExpressions;
using PuddingCode.Abstractions;

namespace PuddingRuntime.Services;

/// <summary>
/// <see cref="IJevDecisionOptionsProvider"/> 的默认实现：从 IConfiguration 的 <c>Jev</c> 节解析
/// 端点 / 密钥 / 模型，环境变量作为回退，密钥可经 KeyVault 引用（ApiKeyRef）解析。
/// <para>
/// 解析优先级（逐项独立）：
/// <list type="number">
/// <item><c>Jev:BaseUrl</c>（或 <c>Jev:Endpoint</c>）→ <c>JEV_BASE_URL</c>；</item>
/// <item><c>Jev:ApiKey</c> → <c>JEV_API_KEY</c>（支持 <c>${ENV_NAME}</c> 占位展开）
///       → 若为空则用 <c>Jev:ApiKeyRef</c> / <c>JEV_API_KEY_REF</c> 经 <see cref="IKeyVaultService"/> 取明文；</item>
/// <item><c>Jev:ModelId</c> → <c>JEV_MODEL_ID</c> → <see cref="JevDecisionOptions.DefaultModelId"/>。</item>
/// </list>
/// 端点或密钥缺失时 fail-closed 抛 <see cref="JevDecisionException"/>（<see cref="JevDecisionCodes.NotConfigured"/>），
/// 不返回空端点；密钥明文绝不写日志。
/// </para>
/// <para>
/// 若后续要把 Jev 纳入资源池（llm.providers.json），只需注册另一个本接口的实现，
/// 无需改动 <see cref="JevDecisionService"/>。
/// </para>
/// </summary>
public sealed class JevDecisionOptionsProvider(
    IConfiguration configuration,
    ILogger<JevDecisionOptionsProvider>? logger = null,
    IKeyVaultService? keyVaultService = null) : IJevDecisionOptionsProvider
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "Jev";

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
        var baseUrl = (
            configuration[BaseUrlKey]
            ?? configuration[EndpointKey]
            ?? Environment.GetEnvironmentVariable(BaseUrlEnvironmentVariable)
            ?? string.Empty).Trim();

        var modelId = configuration[ModelIdKey]
            ?? Environment.GetEnvironmentVariable(ModelIdEnvironmentVariable)
            ?? JevDecisionOptions.DefaultModelId;

        var apiKey = ExpandEnvironmentPlaceholders(
            configuration[ApiKeyKey]
            ?? Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable)
            ?? string.Empty);

        if (string.IsNullOrWhiteSpace(apiKey))
            apiKey = await ResolveApiKeyFromVaultAsync(ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            throw new JevDecisionException(
                JevDecisionCodes.NotConfigured,
                $"Jev 未配置：需要 {BaseUrlKey}（或 {BaseUrlEnvironmentVariable}）与 "
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

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Platform;

namespace PuddingRuntime.Services;

/// <summary>
/// OpenAI 兼容的嵌入向量生成服务。
/// 复用 DirectLlmClient 的 HttpClientFactory 和 KeyVault，调用 /embeddings 端点。
/// dim 默认 1536（text-embedding-3-small）。
/// </summary>
public sealed class OpenAiEmbeddingService : IEmbeddingService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILlmConfigService _llmConfigService;
    private readonly ILogger<OpenAiEmbeddingService> _logger;
    private readonly IKeyVaultService? _keyVaultService;
    private readonly ProviderRateLimiter? _rateLimiter;

    public OpenAiEmbeddingService(
        IHttpClientFactory httpClientFactory,
        ILlmConfigService llmConfigService,
        ILogger<OpenAiEmbeddingService> logger,
        IKeyVaultService? keyVaultService = null,
        ProviderRateLimiter? rateLimiter = null)
    {
        _httpClientFactory = httpClientFactory;
        _llmConfigService = llmConfigService;
        _logger = logger;
        _keyVaultService = keyVaultService;
        _rateLimiter = rateLimiter;
    }

    /// <summary>生成单个文本的嵌入向量。</summary>
    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        var results = await GenerateEmbeddingsAsync(new[] { text }, ct);
        return results[0];
    }

    /// <summary>批量生成嵌入向量（单次 API 调用）。</summary>
    public async Task<float[][]> GenerateEmbeddingsAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (texts.Count == 0)
            return Array.Empty<float[]>();

        // ── 从 ILlmConfigService 获取配置 ──
        var llmConfig = _llmConfigService.GetEmbeddingConfig();
        if (llmConfig is null)
            throw new InvalidOperationException("Embedding config not found in llm.providers.json. Add an Embedding section or mark a model with isEmbedding=true.");

        var apiKey = await ResolveApiKeyAsync(llmConfig, ct);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Embedding API Key not configured.");

        var endpoint = llmConfig.Endpoint;
        if (!endpoint.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
            endpoint = endpoint.TrimEnd('/') + "/embeddings";

        var model = llmConfig.ModelId;

        var requestBody = new
        {
            model,
            input = texts.ToArray(),
            encoding_format = "float"
        };

        using var httpClient = _httpClientFactory.CreateClient("DirectLlm");
        var content = new StringContent(
            JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        _logger.LogInformation(
            "[OpenAiEmbedding] REQUEST model={Model} count={Count}", model, texts.Count);

        // ── 并发限流 ──
        var providerId = llmConfig.KeyVaultId ?? "embedding";
        async Task<HttpResponseMessage> SendRequest() => await httpClient.SendAsync(request, ct);
        var response = _rateLimiter is not null
            ? await _rateLimiter.ExecuteAsync(providerId, SendRequest, ct)
            : await SendRequest();
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("[OpenAiEmbedding] ERROR {Status}: {Body}",
                (int)response.StatusCode, body);
            throw new HttpRequestException(
                $"Embedding API returned {(int)response.StatusCode}: {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var dataArray = doc.RootElement.GetProperty("data");

        var results = new float[dataArray.GetArrayLength()][];
        int idx = 0;
        foreach (var item in dataArray.EnumerateArray())
        {
            var embeddingArray = item.GetProperty("embedding");
            var floats = new float[embeddingArray.GetArrayLength()];
            int j = 0;
            foreach (var val in embeddingArray.EnumerateArray())
            {
                floats[j++] = val.GetSingle();
            }
            results[idx++] = floats;
        }

        _logger.LogInformation(
            "[OpenAiEmbedding] OK count={Count} dim={Dim}",
            results.Length, results.Length > 0 ? results[0].Length : 0);

        return results;
    }

    /// <summary>从 KeyVault 或环境变量解析 API Key。</summary>
    private async Task<string> ResolveApiKeyAsync(LlmConfig config, CancellationToken ct)
    {
        if (_keyVaultService is not null)
        {
            try
            {
                // 优先使用显式配置的 ApiKeyRef
                var keyId = config.KeyVaultId ?? "EMBEDDING_API_KEY";
                var secret = await _keyVaultService.GetSecretAsync(keyId, ct: ct);
                if (!string.IsNullOrWhiteSpace(secret?.Value))
                    return secret.Value;
            }
            catch
            {
                // KeyVault 不可用时回退到环境变量
            }
        }
        // 直接明文 key 或环境变量
        return config.ApiKey
            ?? Environment.GetEnvironmentVariable("EMBEDDING_API_KEY")
            ?? "";
    }
}

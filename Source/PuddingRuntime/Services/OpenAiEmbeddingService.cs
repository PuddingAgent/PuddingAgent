using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;

namespace PuddingRuntime.Services;

/// <summary>
/// OpenAI 兼容的嵌入向量生成服务。
/// 复用 DirectLlmClient 的 HttpClientFactory 和 KeyVault，调用 /embeddings 端点。
/// dim 默认 1536（text-embedding-3-small）。
/// </summary>
public sealed class OpenAiEmbeddingService : IEmbeddingService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenAiEmbeddingService> _logger;
    private readonly IKeyVaultService? _keyVaultService;

    public OpenAiEmbeddingService(
        IHttpClientFactory httpClientFactory,
        ILogger<OpenAiEmbeddingService> logger,
        IKeyVaultService? keyVaultService = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _keyVaultService = keyVaultService;
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

        var apiKey = await ResolveApiKeyAsync(ct);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("EMBEDDING_API_KEY not configured.");

        var endpoint = "https://api.openai.com/v1/embeddings";
        var model = "text-embedding-3-small";

        var requestBody = new
        {
            model,
            input = texts.ToArray(),
            encoding_format = "float"
        };

        using var httpClient = _httpClientFactory.CreateClient("DirectLlm");
        var content = new StringContent(
            JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
        content.Headers.Add("Authorization", $"Bearer {apiKey}");

        _logger.LogInformation(
            "[OpenAiEmbedding] REQUEST model={Model} count={Count}", model, texts.Count);

        var response = await httpClient.PostAsync(endpoint, content, ct);
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
    private async Task<string> ResolveApiKeyAsync(CancellationToken ct)
    {
        if (_keyVaultService is not null)
        {
            try
            {
                var secret = await _keyVaultService.GetSecretAsync("EMBEDDING_API_KEY", ct: ct);
                if (!string.IsNullOrWhiteSpace(secret?.Value))
                    return secret.Value;
            }
            catch
            {
                // KeyVault 不可用时回退到环境变量
            }
        }
        return Environment.GetEnvironmentVariable("EMBEDDING_API_KEY") ?? "";
    }
}

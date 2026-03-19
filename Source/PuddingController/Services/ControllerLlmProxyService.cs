using PuddingCode.Core;
using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingController.Services;

/// <summary>
/// Controller 统一 LLM 代理服务：
/// - 优先使用请求中由 Platform 下发的 LlmConfig
/// - 缺省回退到 .env 静态配置（开发/单机模式兼容）
/// - 预留后续配额（Quota）治理入口
/// </summary>
public sealed class ControllerLlmProxyService(IConfiguration configuration, ILogger<ControllerLlmProxyService> logger)
{
    public async Task<LlmResponse> ChatAsync(
        string workspaceId,
        string sessionId,
        string agentTemplateId,
        IReadOnlyList<ChatMessage> messages,
        LlmConfig? llmConfig = null,
        CancellationToken ct = default)
    {
        var endpoint = NormalizeV1Endpoint(
            llmConfig?.Endpoint ?? configuration["Pudding:LlmEndpoint"] ?? "https://api.openai.com/v1");
        var apiKey = llmConfig?.ApiKey ?? configuration["Pudding:LlmApiKey"] ?? string.Empty;
        var model = llmConfig?.ModelId ?? configuration["Pudding:LlmModel"] ?? "gpt-4o-mini";

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "LLM API key is not configured. Set it via Agent's preferred provider or Pudding:LlmApiKey.");
        }

        // 未来在这里加入 Workspace 级配额检查/扣减。
        logger.LogInformation(
            "[ControllerLLM] ws={WorkspaceId} session={SessionId} template={Template} model={Model} endpoint={Endpoint}",
            workspaceId, sessionId, agentTemplateId, model, endpoint);

        var gateway = new OpenAiLlmGateway(new HttpClient(), new LlmOptions(endpoint, apiKey, model));
        return await gateway.ChatAsync(messages, [], ct);
    }

    /// <summary>确保端点以 /v1 结尾（兼容已含 /v1 的 URL）。</summary>
    private static string NormalizeV1Endpoint(string url)
    {
        var u = url.TrimEnd('/');
        return u.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? u : u + "/v1";
    }
}

using PuddingCode.Core;
using PuddingCode.Models;

namespace PuddingController.Services;

/// <summary>
/// Controller 统一 LLM 代理服务：
/// - 持有 Provider/Key/Model 配置
/// - 代表 Runtime 调用外部 LLM
/// - 预留后续配额（Quota）治理入口
/// </summary>
public sealed class ControllerLlmProxyService(IConfiguration configuration, ILogger<ControllerLlmProxyService> logger)
{
    public async Task<LlmResponse> ChatAsync(
        string workspaceId,
        string sessionId,
        string agentTemplateId,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken ct = default)
    {
        var endpoint = configuration["Pudding:LlmEndpoint"] ?? "https://api.openai.com/v1";
        var apiKey = configuration["Pudding:LlmApiKey"] ?? string.Empty;
        var model = configuration["Pudding:LlmModel"] ?? "gpt-4o-mini";

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("LLM API key is not configured in Controller (Pudding:LlmApiKey).");
        }

        // 未来在这里加入 Workspace 级配额检查/扣减。
        logger.LogInformation("[ControllerLLM] ws={WorkspaceId} session={SessionId} template={Template} model={Model}",
            workspaceId, sessionId, agentTemplateId, model);

        var gateway = new OpenAiLlmGateway(new HttpClient(), new LlmOptions(endpoint, apiKey, model));
        return await gateway.ChatAsync(messages, [], ct);
    }
}

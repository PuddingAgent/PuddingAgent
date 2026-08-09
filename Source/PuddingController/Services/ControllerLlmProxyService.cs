using PuddingCode.Core;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using System.Runtime.CompilerServices;

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
        IReadOnlyList<LlmToolDefinition>? tools = null,
        LlmConfig? llmConfig = null,
        CancellationToken ct = default)
    {
        var configSource = llmConfig is not null ? "request(Platform)" : ".env(fallback)";
        var protocol = ResolveProtocol(llmConfig);
        var endpoint = NormalizeEndpoint(
            llmConfig?.Endpoint ?? configuration["Pudding:LlmEndpoint"] ?? "https://api.openai.com/v1",
            protocol);
        var apiKey = llmConfig?.ApiKey ?? configuration["Pudding:LlmApiKey"] ?? string.Empty;
        var model = llmConfig?.ModelId ?? configuration["Pudding:LlmModel"] ?? "gpt-4o-mini";

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "LLM API key is not configured. Set it via Agent's preferred provider or Pudding:LlmApiKey.");
        }

        // 未来在这里加入 Workspace 级配额检查/扣减。
        logger.LogInformation(
            "[ControllerLLM] CALL ws={WorkspaceId} session={SessionId} template={Template} model={Model} protocol={Protocol} endpoint={Endpoint} configSource={ConfigSource} toolCount={ToolCount}",
            workspaceId, sessionId, agentTemplateId, model, protocol, endpoint, configSource, tools?.Count ?? 0);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var gateway = CreateGateway(endpoint, apiKey, model, protocol, llmConfig);
            var toolSpecs = (tools ?? []).Select(t => (ITool)new ProxyTool(t)).ToList();
            var result = await gateway.ChatAsync(messages, toolSpecs, ct);
            sw.Stop();
            logger.LogInformation(
                "[ControllerLLM] OK ws={WorkspaceId} session={SessionId} elapsed={Elapsed}ms contentLen={Len} usage={Usage}",
                workspaceId, sessionId, sw.ElapsedMilliseconds, result.Content?.Length ?? 0,
                result.Usage?.TotalTokens);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogError(
                "[ControllerLLM] ERROR ws={WorkspaceId} session={SessionId} elapsed={Elapsed}ms model={Model} endpoint={Endpoint} msg={Msg}",
                workspaceId, sessionId, sw.ElapsedMilliseconds, model, endpoint, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// 流式调用外部 LLM。Controller 仍是唯一持有密钥的边界，Runtime 只消费 delta。
    /// </summary>
    public async IAsyncEnumerable<StreamDelta> ChatStreamAsync(
        string workspaceId,
        string sessionId,
        string agentTemplateId,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools = null,
        LlmConfig? llmConfig = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var configSource = llmConfig is not null ? "request(Platform)" : ".env(fallback)";
        var protocol = ResolveProtocol(llmConfig);
        var endpoint = NormalizeEndpoint(
            llmConfig?.Endpoint ?? configuration["Pudding:LlmEndpoint"] ?? "https://api.openai.com/v1",
            protocol);
        var apiKey = llmConfig?.ApiKey ?? configuration["Pudding:LlmApiKey"] ?? string.Empty;
        var model = llmConfig?.ModelId ?? configuration["Pudding:LlmModel"] ?? "gpt-4o-mini";

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "LLM API key is not configured. Set it via Agent's preferred provider or Pudding:LlmApiKey.");
        }

        logger.LogInformation(
            "[ControllerLLM] STREAM ws={WorkspaceId} session={SessionId} template={Template} model={Model} protocol={Protocol} endpoint={Endpoint} configSource={ConfigSource} toolCount={ToolCount}",
            workspaceId, sessionId, agentTemplateId, model, protocol, endpoint, configSource, tools?.Count ?? 0);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var gateway = CreateGateway(endpoint, apiKey, model, protocol, llmConfig);
        var toolSpecs = (tools ?? []).Select(t => (ITool)new ProxyTool(t)).ToList();

        await foreach (var delta in gateway.ChatStreamAsync(messages, toolSpecs, ct))
        {
            yield return delta;
        }

        sw.Stop();
        logger.LogInformation(
            "[ControllerLLM] STREAM OK ws={WorkspaceId} session={SessionId} elapsed={Elapsed}ms",
            workspaceId, sessionId, sw.ElapsedMilliseconds);
    }

    private static ILlmGateway CreateGateway(
        string endpoint,
        string apiKey,
        string model,
        string protocol,
        LlmConfig? config)
    {
        var options = new LlmOptions(
            endpoint,
            apiKey,
            model,
            MaxTokens: config?.MaxOutputTokens,
            ReasoningEffort: config?.ReasoningEffort);
        if (string.Equals(protocol, "responses", StringComparison.OrdinalIgnoreCase))
            return new ResponsesLlmGateway(new HttpClient(), options);
        if (string.Equals(protocol, "anthropic", StringComparison.OrdinalIgnoreCase))
            return new AnthropicMessagesLlmGateway(new HttpClient(), options);
        if (string.Equals(protocol, "openai", StringComparison.OrdinalIgnoreCase))
            return new OpenAiLlmGateway(new HttpClient(), options);

        throw new InvalidOperationException($"Unsupported model protocol '{protocol}'.");
    }

    private static string ResolveProtocol(LlmConfig? config)
    {
        var protocol = config?.Protocol;
        if (!string.Equals(protocol, "openai", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(protocol, "responses", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(protocol, "anthropic", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "LLM model protocol is not configured. Set model.protocol to 'openai', 'responses', or 'anthropic'.");
        }

        return protocol!.ToLowerInvariant();
    }

    private static string NormalizeEndpoint(string url, string protocol)
    {
        var u = url.TrimEnd('/');
        if (string.Equals(protocol, "responses", StringComparison.OrdinalIgnoreCase))
            return u;
        return u.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? u : u + "/v1";
    }

    private sealed class ProxyTool(LlmToolDefinition dto) : ITool
    {
        public string Name => dto.Name;
        public string Description => dto.Description;
        public ToolParameterSchema Parameters => dto.Parameters;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
            => throw new NotSupportedException("Proxy tool definitions are only for function schema transport.");
    }
}

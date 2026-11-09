using System.Security.Cryptography;
using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Core;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Platform.Options;
using PuddingCode.Tools;
using PuddingExecutionResources.Contracts;
using PuddingRuntime.Services;

namespace PuddingRuntime.Operators.PublicApi;

public interface IExecutionModelBinder
{
    Task<ILeasedModel> BindAsync(ToolExecutionContext context, CancellationToken ct);
}

/// <summary>Freezes the caller's explicitly selected route/config/prices. No default model, retries, tools or credentials on the public boundary.</summary>
public sealed class ConfiguredExecutionModelBinder(ILlmConfigService configs, IHttpClientFactory http,
    ProviderRateLimiter rateLimiter, IKeyVaultService vault, ILlmGatewayUsageRecorder recorder) : IExecutionModelBinder
{
    public async Task<ILeasedModel> BindAsync(ToolExecutionContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var route = context.CallerLlmSnapshot ?? throw Error("model.not_configured", "The parent has no frozen model route.");
        var provider = configs.GetEnabledProviders().SingleOrDefault(p => p.ProviderId == route.ProviderId && p.IsEnabled);
        var info = configs.GetAllModels().SingleOrDefault(m => m.ProviderId == route.ProviderId && m.ModelId == route.ModelId && !m.IsEmbedding && !m.IsDeprecated);
        var config = configs.Resolve(route.ProviderId, route.ModelId);
        if (provider is null || info is null || config is null || config.ModelId != route.ModelId || info.Protocol != route.Protocol
            || info.Protocol is not ("openai" or "responses" or "anthropic") || info.MaxInputTokens is not > 0 || info.MaxOutputTokens < 1
            || string.IsNullOrWhiteSpace(config.Endpoint)) throw Error("model.not_configured", "The selected model requires explicit protocol, input/output capacity and pricing configuration.");
        if (info.PriceWindows.Count != 0) throw Error("model.pricing_unsupported", "Time-window pricing needs a frozen price quote before model evaluation can use it.");
        if (info.InputPricePer1MTokens == 0 && info.OutputPricePer1MTokens == 0)
            throw Error("model.pricing_unavailable", "An all-zero configuration cannot distinguish missing pricing from an explicitly free model.");
#pragma warning disable CS0618
        var key = config.ApiKey;
#pragma warning restore CS0618
        if (!string.IsNullOrWhiteSpace(config.KeyVaultId))
        {
            var placeholder = "{{vault:" + config.KeyVaultId + "}}";
            key = await vault.InjectAsync(placeholder, ct);
            if (key == placeholder) throw Error("model.credentials_unavailable", "The selected model credential could not be resolved.");
        }
        if (string.IsNullOrWhiteSpace(key) || key.Contains("{{vault:", StringComparison.Ordinal)) throw Error("model.credentials_unavailable", "The selected model credential is unavailable.");
        var input = Math.Min(8192, info.MaxInputTokens.Value);
        if (context.UsageBudget?.MaxInputTokens is > 0) input = (int)Math.Min(input, context.UsageBudget.MaxInputTokens);
        var strategy = configs.GetModelStrategy(info.ProviderId, info.ModelId) ?? configs.GetProviderStrategy(info.ProviderId) ?? LlmProviderStrategy.Default;
        var price = new ModelPrice(info.InputPricePer1MTokens, info.OutputPricePer1MTokens,
            info.CacheHitPricePer1MTokens > 0 ? info.CacheHitPricePer1MTokens : info.InputPricePer1MTokens);
        var output = Math.Min(2048, info.MaxOutputTokens);
        var concurrency = Math.Clamp(strategy.EffectiveMaxConcurrentRequests, 1, 64);
        var revision = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { info.ProviderId, info.ModelId, info.Protocol, config.Endpoint, input, output, concurrency, price, strategy.Compat })));
        var descriptor = new ModelResourceDescriptor(info.ProviderId + "/" + info.ModelId, revision, info.ModelId, input, output, concurrency, price);
        return new Binding(descriptor, info.ProviderId, info.Protocol, config.Endpoint, key, strategy.Compat, context, http, rateLimiter, recorder);
    }
    private static ExecutionResourceException Error(string code, string message) => new(code, message);
    private sealed class Binding(ModelResourceDescriptor descriptor, string providerId, string protocol, string endpoint, string key,
        ProviderCompatConfig? compat, ToolExecutionContext context, IHttpClientFactory http, ProviderRateLimiter limiter,
        ILlmGatewayUsageRecorder recorder) : ILeasedModel
    {
        public ModelResourceDescriptor Descriptor => descriptor;
        public async Task<ModelCompletion> InvokeAsync(ModelResourceRequest request, Action dispatch, CancellationToken ct)
        {
            // Reuse the same model gate as primary Agent calls. Cancellation before acquisition is not a dispatch.
            using var sharedSlot = await limiter.AcquireAsync(providerId, descriptor.ModelId, ct);
            using var client = http.CreateClient("DirectLlm");
            var options = new PuddingCode.Platform.Options.LlmOptions(endpoint, key, descriptor.ModelId, Temperature: 0, MaxTokens: request.MaxOutputTokens);
            ILlmGateway gateway = protocol switch
            {
                "responses" => new ResponsesLlmGateway(client, options),
                "anthropic" => new AnthropicMessagesLlmGateway(client, options),
                _ => new OpenAiLlmGateway(client, options) { Compat = compat },
            };
            var started = DateTimeOffset.UtcNow; var usageId = Guid.NewGuid().ToString("N");
            ct.ThrowIfCancellationRequested(); dispatch();
            // Gateways perform one transport attempt. No DirectLlmClient retry loop is involved.
            var response = await gateway.ChatAsync([new ChatMessage(ChatRole.System, request.Instruction), new ChatMessage(ChatRole.User, request.Input)], [], ct);
            if (response.Usage is { } usage)
                await recorder.RecordRequiredAsync(usage, "model-evaluation:" + usageId, "model-evaluation", context.WorkspaceId, context.SessionId,
                    context.AgentTemplateId ?? context.AgentInstanceId, providerId, descriptor.ModelId, started, CancellationToken.None);
            var observed = response.Usage is { PromptTokens: { } prompt, CompletionTokens: { } completion } u
                ? new ModelUsage(prompt, completion, u.PromptCacheHitTokens ?? 0) : null;
            return new(response.Content ?? string.Empty, observed, response.FinishReason);
        }
    }
}

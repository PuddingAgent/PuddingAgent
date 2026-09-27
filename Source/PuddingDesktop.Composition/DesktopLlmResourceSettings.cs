using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Configuration;
using PuddingDesktop.Foundation;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingDesktop.Composition;

/// <summary>
/// Binds the LLM settings pages to the existing Core application service. Every call goes through the
/// DS-00 kernel boundary, so an unready or stopping host refuses instead of returning a fake success.
/// Core remains the authority for validation, atomic writes and the write lock.
/// </summary>
internal sealed class DesktopLlmResourceSettings(IDesktopKernel kernel) : ILlmResourceSettings
{
    private Task<T> RunAsync<T>(string operationId,
        Func<LlmProviderFileService, CancellationToken, Task<T>> body, CancellationToken cancellationToken)
        => kernel.RunSettingsAsync(operationId,
            (scope, token) => body(scope.Services.GetRequiredService<LlmProviderFileService>(), token), cancellationToken);

    private Task<T> RunQuotaAsync<T>(string operationId,
        Func<LlmProviderQuotaService, CancellationToken, Task<T>> body, CancellationToken cancellationToken)
        => kernel.RunSettingsAsync(operationId,
            (scope, token) => body(scope.Services.GetRequiredService<LlmProviderQuotaService>(), token), cancellationToken);

    public Task<IReadOnlyList<LlmProviderSummary>> ListProvidersAsync(CancellationToken cancellationToken = default)
        => RunAsync("llm.providers.list", async (service, token) =>
        {
            var config = await service.LoadAsync(token);
            return (IReadOnlyList<LlmProviderSummary>)config.Providers
                .Select(provider => new LlmProviderSummary(
                    provider.ProviderId, provider.Name, provider.BaseUrl, provider.Description ?? "",
                    provider.IsEnabled, HasKey(provider),
                    new LlmProviderLimits(provider.MaxConcurrentRequests, provider.TokensPerMinute, provider.RequestsPerMinute),
                    provider.Models.Count))
                .ToArray();
        }, cancellationToken);

    public Task<IReadOnlyList<LlmModelSummary>> ListModelsAsync(string providerId, CancellationToken cancellationToken = default)
        => RunAsync("llm.models.list", async (service, token) =>
        {
            var provider = (await service.LoadAsync(token)).Providers.FirstOrDefault(candidate =>
                string.Equals(candidate.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
            if (provider is null) return (IReadOnlyList<LlmModelSummary>)[];
            return provider.Models
                .OrderBy(model => model.SortOrder)
                .ThenBy(model => model.ModelId, StringComparer.OrdinalIgnoreCase)
                .Select(Map)
                .ToArray();
        }, cancellationToken);

    public Task SaveProviderAsync(LlmProviderEdit edit, CancellationToken cancellationToken = default)
        => RunAsync("llm.providers.save", async (service, token) =>
        {
            // "Keep" must submit a null key so Core leaves the stored secret and vault reference untouched.
            var request = new UpsertLlmProviderRequest(
                edit.ProviderId, edit.Name, edit.BaseUrl,
                edit.KeyChange == ApiKeyChange.Replace ? edit.NewKey : null,
                edit.Description, edit.IsEnabled,
                edit.Limits.MaxConcurrentRequests, edit.Limits.TokensPerMinute, edit.Limits.RequestsPerMinute,
                ClearApiKey: edit.KeyChange == ApiKeyChange.Clear);
            if (await service.GetProviderAsync(edit.ProviderId, token) is null)
                await service.CreateProviderAsync(request, token);
            else
                await service.UpdateProviderAsync(edit.ProviderId, request, token);
            return true;
        }, cancellationToken);

    public Task DeleteProviderAsync(string providerId, CancellationToken cancellationToken = default)
        => RunAsync("llm.providers.delete", async (service, token) =>
        {
            await service.DeleteProviderAsync(providerId, token);
            return true;
        }, cancellationToken);

    public Task SaveModelAsync(LlmModelEdit edit, CancellationToken cancellationToken = default)
        => RunAsync("llm.models.save", async (service, token) =>
        {
            var provider = (await service.LoadAsync(token)).Providers.FirstOrDefault(candidate =>
                string.Equals(candidate.ProviderId, edit.ProviderId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"服务商 {edit.ProviderId} 不存在。");
            // Null price windows / profile fields keep the versioned price data Core already stores.
            var request = new UpsertLlmModelRequest(
                edit.ModelId, edit.Name, edit.Protocol, null,
                edit.MaxContextTokens ?? 0, edit.MaxOutputTokens ?? 0,
                edit.InputPricePer1MTokens, edit.OutputPricePer1MTokens, edit.CacheHitPricePer1MTokens,
                [.. edit.CapabilityTags], edit.IsDeprecated, edit.IsDefault, edit.IsEmbedding, edit.SortOrder,
                edit.MaxConcurrentRequests, edit.MaxInputTokens);
            if (provider.Models.Any(model => string.Equals(model.ModelId, edit.ModelId, StringComparison.OrdinalIgnoreCase)))
                await service.UpdateModelAsync(edit.ProviderId, edit.ModelId, request, token);
            else
                await service.CreateModelAsync(edit.ProviderId, request, token);
            return true;
        }, cancellationToken);

    public Task DeleteModelAsync(string providerId, string modelId, CancellationToken cancellationToken = default)
        => RunAsync("llm.models.delete", async (service, token) =>
        {
            await service.DeleteModelAsync(providerId, modelId, token);
            return true;
        }, cancellationToken);

    public Task<LlmQuotaStatus?> GetQuotaAsync(string providerId, CancellationToken cancellationToken = default)
        => RunQuotaAsync("llm.quota.read",
            async (quota, token) => await quota.TryGetAsync(providerId, token) is { } status ? Map(status) : null,
            cancellationToken);

    public Task<LlmQuotaStatus> SaveQuotaAsync(string providerId, LlmQuotaLimits limits, CancellationToken cancellationToken = default)
        => RunQuotaAsync("llm.quota.save", async (quota, token) => Map(await quota.UpsertAsync(providerId,
            new UpdateQuotaRequest(limits.DailyTokenLimit, limits.MonthlyTokenLimit), token)), cancellationToken);

    public Task<LlmQuotaStatus> ResetDailyQuotaAsync(string providerId, CancellationToken cancellationToken = default)
        => RunQuotaAsync("llm.quota.reset-daily",
            async (quota, token) => Map(await quota.ResetDailyAsync(providerId, token)), cancellationToken);

    private static LlmQuotaStatus Map(LlmProviderQuotaDto quota) => new(
        quota.DailyTokenLimit, quota.MonthlyTokenLimit, quota.DailyTokensUsed, quota.MonthlyTokensUsed,
        quota.IsSuspended, quota.DailyResetAt, quota.MonthlyResetAt, quota.UpdatedAt);

    private static bool HasKey(PuddingLlmProviderConfig provider) =>
        !string.IsNullOrWhiteSpace(provider.ApiKey) || !string.IsNullOrWhiteSpace(provider.ApiKeyRef);

    private static LlmModelSummary Map(PuddingLlmModelConfig model) => new(
        model.ModelId, model.Name, model.Protocol, model.CapabilityTags ?? [],
        model.MaxContextTokens, model.MaxInputTokens, model.MaxOutputTokens, model.MaxConcurrentRequests,
        model.PricePer1MInputTokens ?? 0m, model.PricePer1MOutputTokens ?? 0m, model.PricePer1MCacheHitTokens ?? 0m,
        model.IsDefault, model.IsDeprecated, model.IsEmbedding, model.SortOrder);
}

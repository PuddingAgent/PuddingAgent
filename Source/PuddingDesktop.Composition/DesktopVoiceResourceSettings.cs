using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Configuration;
using PuddingDesktop.Foundation;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingDesktop.Composition;

/// <summary>
/// Binds the TTS/ASR voice settings pages to the existing Core application service. Every call goes
/// through the DS-00 kernel boundary, so an unready or stopping host refuses instead of reporting a
/// fake save. Core keeps validation, the write lock, atomic replacement and default-pointer sync.
/// </summary>
internal sealed class DesktopVoiceResourceSettings(IDesktopKernel kernel) : IVoiceResourceSettings
{
    private Task<T> RunAsync<T>(string operationId,
        Func<VoiceProviderFileService, CancellationToken, Task<T>> body, CancellationToken cancellationToken)
        => kernel.RunSettingsAsync(operationId,
            (scope, token) => body(scope.Services.GetRequiredService<VoiceProviderFileService>(), token), cancellationToken);

    public Task<IReadOnlyList<VoiceProviderSummary>> ListProvidersAsync(CancellationToken cancellationToken = default)
        => RunAsync("voice.providers.list", async (service, token) =>
        {
            var config = await service.LoadAsync(token);
            return (IReadOnlyList<VoiceProviderSummary>)config.Providers
                .Select(provider => new VoiceProviderSummary(
                    provider.ProviderId, provider.Name, provider.Endpoint, provider.Description ?? "",
                    provider.IsEnabled, !string.IsNullOrWhiteSpace(provider.ApiKey),
                    provider.TtsModels.Count, provider.AsrModels.Count))
                .ToArray();
        }, cancellationToken);

    public Task<IReadOnlyList<VoiceTtsModel>> ListTtsModelsAsync(string providerId, CancellationToken cancellationToken = default)
        => RunAsync("voice.tts.list", async (service, token) =>
        {
            var provider = Find(await service.LoadAsync(token), providerId);
            return (IReadOnlyList<VoiceTtsModel>)(provider?.TtsModels ?? [])
                .OrderBy(model => model.SortOrder)
                .ThenBy(model => model.ModelId, StringComparer.OrdinalIgnoreCase)
                .Select(model => new VoiceTtsModel(provider!.ProviderId, model.ModelId, model.Name, model.Path ?? "",
                    model.Voices ?? [], model.AudioFormats ?? [], model.SampleRates ?? [],
                    model.SupportsStreaming, model.SupportsInstructions, model.SupportsVoiceCloning, model.SupportsVoiceDesign,
                    model.IsDeprecated, model.IsDefault, model.SortOrder))
                .ToArray();
        }, cancellationToken);

    public Task<IReadOnlyList<VoiceAsrModel>> ListAsrModelsAsync(string providerId, CancellationToken cancellationToken = default)
        => RunAsync("voice.asr.list", async (service, token) =>
        {
            var provider = Find(await service.LoadAsync(token), providerId);
            return (IReadOnlyList<VoiceAsrModel>)(provider?.AsrModels ?? [])
                .OrderBy(model => model.SortOrder)
                .ThenBy(model => model.ModelId, StringComparer.OrdinalIgnoreCase)
                .Select(model => new VoiceAsrModel(provider!.ProviderId, model.ModelId, model.Name, model.Path ?? "",
                    model.Languages ?? [], model.SampleRates ?? [],
                    model.SupportsEmotion, model.SupportsTimestamps, model.SupportsHotWords,
                    model.IsDeprecated, model.IsDefault, model.SortOrder))
                .ToArray();
        }, cancellationToken);

    public Task<VoiceDefaults> GetDefaultsAsync(CancellationToken cancellationToken = default)
        => RunAsync("voice.defaults.read", async (service, token) =>
        {
            var defaults = await service.GetDefaultsAsync(token);
            return new VoiceDefaults(defaults.DefaultTtsProviderId, defaults.DefaultTtsModelId,
                defaults.DefaultAsrProviderId, defaults.DefaultAsrModelId);
        }, cancellationToken);

    public Task SaveProviderAsync(VoiceProviderEdit edit, CancellationToken cancellationToken = default)
        => RunAsync("voice.providers.save", async (service, token) =>
        {
            // "Keep" submits a null key so Core leaves the stored secret untouched.
            var request = new UpsertVoiceProviderRequest(edit.ProviderId, edit.Name, edit.Endpoint,
                edit.KeyChange == ApiKeyChange.Replace ? edit.NewKey : null, edit.Description, edit.IsEnabled,
                ClearApiKey: edit.KeyChange == ApiKeyChange.Clear);
            if (await service.GetProviderAsync(edit.ProviderId, token) is null)
                await service.CreateProviderAsync(request, token);
            else
                await service.UpdateProviderAsync(edit.ProviderId, request, token);
            return true;
        }, cancellationToken);

    public Task DeleteProviderAsync(string providerId, CancellationToken cancellationToken = default)
        => RunAsync("voice.providers.delete", async (service, token) =>
        {
            await service.DeleteProviderAsync(providerId, token);
            return true;
        }, cancellationToken);

    public Task SaveTtsModelAsync(VoiceTtsModel model, CancellationToken cancellationToken = default)
        => RunAsync("voice.tts.save", async (service, token) =>
        {
            // null means "keep the stored arrays"; the shell always submits what it displayed.
            var request = new UpsertTtsModelRequest(model.ModelId, model.Name, Nullable(model.Path),
                [.. model.Voices], [.. model.AudioFormats], [.. model.SampleRates],
                model.SupportsStreaming, model.SupportsInstructions, model.SupportsVoiceCloning, model.SupportsVoiceDesign,
                model.IsDeprecated, model.IsDefault, model.SortOrder);
            if ((await service.GetProviderAsync(model.ProviderId, token))?.TtsModels.Any(existing =>
                    string.Equals(existing.ModelId, model.ModelId, StringComparison.OrdinalIgnoreCase)) == true)
                await service.UpdateTtsModelAsync(model.ProviderId, model.ModelId, request, token);
            else
                await service.CreateTtsModelAsync(model.ProviderId, request, token);
            return true;
        }, cancellationToken);

    public Task DeleteTtsModelAsync(string providerId, string modelId, CancellationToken cancellationToken = default)
        => RunAsync("voice.tts.delete", async (service, token) =>
        {
            await service.DeleteTtsModelAsync(providerId, modelId, token);
            return true;
        }, cancellationToken);

    public Task SaveAsrModelAsync(VoiceAsrModel model, CancellationToken cancellationToken = default)
        => RunAsync("voice.asr.save", async (service, token) =>
        {
            var request = new UpsertAsrModelRequest(model.ModelId, model.Name, Nullable(model.Path),
                [.. model.Languages], [.. model.SampleRates],
                model.SupportsEmotion, model.SupportsTimestamps, model.SupportsHotWords,
                model.IsDeprecated, model.IsDefault, model.SortOrder);
            if ((await service.GetProviderAsync(model.ProviderId, token))?.AsrModels.Any(existing =>
                    string.Equals(existing.ModelId, model.ModelId, StringComparison.OrdinalIgnoreCase)) == true)
                await service.UpdateAsrModelAsync(model.ProviderId, model.ModelId, request, token);
            else
                await service.CreateAsrModelAsync(model.ProviderId, request, token);
            return true;
        }, cancellationToken);

    public Task DeleteAsrModelAsync(string providerId, string modelId, CancellationToken cancellationToken = default)
        => RunAsync("voice.asr.delete", async (service, token) =>
        {
            await service.DeleteAsrModelAsync(providerId, modelId, token);
            return true;
        }, cancellationToken);

    private static PuddingVoiceProviderConfig? Find(PuddingVoiceProvidersConfig config, string providerId) =>
        config.Providers.FirstOrDefault(provider =>
            string.Equals(provider.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));

    private static string? Nullable(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

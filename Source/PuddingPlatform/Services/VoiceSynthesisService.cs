using System.Runtime.CompilerServices;
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingPlatform.Services;

/// <summary>
/// Application-level TTS abstraction shared by web and external channels. It
/// resolves the configured provider/model, delegates synthesis to ITtsProvider,
/// and materializes provider output as bounded audio bytes.
/// </summary>
public sealed class VoiceSynthesisService(
    VoiceProviderFileService voiceProviders,
    IVoiceProviderFactory providerFactory,
    IHttpClientFactory httpClientFactory,
    ILogger<VoiceSynthesisService> logger) : IVoiceSynthesisService
{
    private const int MaxAudioBytes = 30 * 1024 * 1024;

    public async Task<VoiceSynthesisResult> SynthesizeAsync(
        VoiceSynthesisRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);

        var (provider, normalized) = await ResolveAsync(request, ct);
        var result = await provider.SynthesizeAsync(normalized, ct);
        if (result.AudioBytes is { Length: > 0 })
            return result;
        if (string.IsNullOrWhiteSpace(result.AudioUrl))
        {
            throw new InvalidOperationException(
                $"TTS provider '{provider.Provider}' returned no audio payload.");
        }

        var bytes = await DownloadBoundedAsync(result.AudioUrl, ct);
        logger.LogInformation(
            "[VoiceTts] Audio materialized provider={Provider} model={Model} message={MessageId} bytes={Bytes}",
            result.Provider,
            result.Model,
            result.MessageId,
            bytes.Length);
        return result with { AudioBytes = bytes };
    }

    public async IAsyncEnumerable<VoiceSynthesisStreamEvent> StreamAsync(
        VoiceSynthesisRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (provider, normalized) = await ResolveAsync(request, ct);
        await foreach (var evt in provider.StreamAsync(normalized, ct))
            yield return evt;
    }

    private async Task<(ITtsProvider Provider, VoiceSynthesisRequest Request)>
        ResolveAsync(
            VoiceSynthesisRequest request,
            CancellationToken ct)
    {
        var config = await voiceProviders.LoadAsync(ct);
        if (config.Providers.Count == 0)
            throw new InvalidOperationException("Voice providers are not configured.");

        var explicitlyRequestedProvider = !string.Equals(
                request.Provider,
                VoiceSynthesisProviders.Unknown,
                StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(request.Provider);
        var providerId = explicitlyRequestedProvider
            ? request.Provider
            : config.DefaultTtsProviderId
              ?? throw new InvalidOperationException(
                  "No default TTS provider configured.");
        var providerConfig = config.Providers.FirstOrDefault(item =>
                item.IsEnabled
                && string.Equals(
                    item.ProviderId,
                    providerId,
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"TTS provider '{providerId}' not found or disabled.");

        var modelId = string.IsNullOrWhiteSpace(request.Model)
            ? null
            : request.Model;
        if (modelId is null
            && !explicitlyRequestedProvider
            && !string.IsNullOrWhiteSpace(config.DefaultTtsModelId)
            && providerConfig.TtsModels.Any(item =>
                string.Equals(
                    item.ModelId,
                    config.DefaultTtsModelId,
                    StringComparison.OrdinalIgnoreCase)))
        {
            modelId = config.DefaultTtsModelId;
        }
        modelId ??= providerConfig.TtsModels
            .FirstOrDefault(item => item.IsDefault)
            ?.ModelId
            ?? throw new InvalidOperationException(
                $"No default TTS model configured for provider '{providerId}'.");

        var provider = providerFactory.CreateTtsProvider(
            config,
            providerConfig.ProviderId,
            modelId);

        return (provider, request with
        {
            Provider = provider.Provider,
            Model = modelId,
            Voice = string.IsNullOrWhiteSpace(request.Voice)
                ? "Cherry"
                : request.Voice,
            AudioFormat = string.IsNullOrWhiteSpace(request.AudioFormat)
                ? VoiceAudioFormats.Wav
                : request.AudioFormat,
            SampleRate = request.SampleRate > 0 ? request.SampleRate : 24_000,
        });
    }

    private async Task<byte[]> DownloadBoundedAsync(
        string audioUrl,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, audioUrl);
        using var response = await httpClientFactory
            .CreateClient("VoiceAudioDownload")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxAudioBytes)
        {
            throw new InvalidDataException(
                $"TTS audio exceeds {MaxAudioBytes} bytes.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var destination = new MemoryStream();
        var buffer = new byte[81_920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0)
                break;
            if (destination.Length + read > MaxAudioBytes)
            {
                throw new InvalidDataException(
                    $"TTS audio exceeds {MaxAudioBytes} bytes.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        if (destination.Length == 0)
            throw new InvalidDataException("TTS provider returned an empty audio file.");
        return destination.ToArray();
    }
}

using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingPlatform.Services;

public sealed class AudioTranscriptionService(
    VoiceProviderFileService voiceProviders,
    IVoiceProviderFactory providerFactory,
    ILogger<AudioTranscriptionService> logger) : IAudioTranscriptionService
{
    private const int MaxAudioBytes = 30 * 1024 * 1024;

    public async Task<AudioTranscriptionResult> TranscribeAsync(
        AudioTranscriptionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Content.Length == 0)
            throw new InvalidDataException("ASR audio is empty.");
        if (request.Content.Length > MaxAudioBytes)
            throw new InvalidDataException($"ASR audio exceeds {MaxAudioBytes} bytes.");
        if (!string.Equals(request.Format, VoiceAudioFormats.Wav, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(request.Format, VoiceAudioFormats.Mp3, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"ASR input format '{request.Format}' is not provider-safe. Use WAV or MP3.");
        }

        var config = await voiceProviders.LoadAsync(ct);
        var providerId = string.IsNullOrWhiteSpace(request.Provider)
            ? config.DefaultAsrProviderId
            : request.Provider;
        if (string.IsNullOrWhiteSpace(providerId))
            throw new InvalidOperationException("No default ASR provider configured.");
        var providerConfig = config.Providers.FirstOrDefault(item =>
                item.IsEnabled
                && string.Equals(item.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"ASR provider '{providerId}' not found or disabled.");

        var modelId = string.IsNullOrWhiteSpace(request.Model)
            ? null
            : request.Model;
        if (modelId is null
            && string.Equals(providerId, config.DefaultAsrProviderId, StringComparison.OrdinalIgnoreCase)
            && providerConfig.AsrModels.Any(item =>
                string.Equals(item.ModelId, config.DefaultAsrModelId, StringComparison.OrdinalIgnoreCase)))
        {
            modelId = config.DefaultAsrModelId;
        }
        modelId ??= providerConfig.AsrModels.FirstOrDefault(item => item.IsDefault)?.ModelId
            ?? throw new InvalidOperationException(
                $"No default ASR model configured for provider '{providerId}'.");

        var recognizer = providerFactory.CreateAsrProvider(
            config,
            providerConfig.ProviderId,
            modelId);
        var providerResult = await recognizer.RecognizeAsync(
            request.Content,
            request.Format,
            request.Language,
            ct);
        if (string.IsNullOrWhiteSpace(providerResult.Text))
            throw new InvalidOperationException("ASR provider returned an empty transcript.");

        logger.LogInformation(
            "[VoiceAsr] Audio transcribed provider={ProviderId} model={ModelId} bytes={Bytes} transcriptLength={TranscriptLength}",
            providerConfig.ProviderId,
            modelId,
            request.Content.Length,
            providerResult.Text.Length);
        return new AudioTranscriptionResult
        {
            Text = providerResult.Text.Trim(),
            Emotion = providerResult.Emotion,
            Provider = providerConfig.ProviderId,
            Model = modelId,
        };
    }
}

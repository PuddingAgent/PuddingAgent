using PuddingAgent.Connectors;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingAgent.Services;

/// <summary>
/// Converts a typed Feishu TTS delivery into provider-neutral synthesized audio
/// and then into the Ogg/Opus format accepted by Feishu.
/// </summary>
public sealed class FeishuTtsDeliveryService(
    IVoiceSynthesisService voiceSynthesis,
    IAudioTranscoder audioTranscoder,
    ILogger<FeishuTtsDeliveryService> logger)
{
    public const int MaxTextCharacters = 1_000;

    public async Task<AudioTranscodingResult> CreateAudioAsync(
        FeishuConnectorBinding binding,
        ConnectorMessage message,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.Content);
        if (message.Content.Length > MaxTextCharacters)
        {
            throw new InvalidOperationException(
                $"Feishu TTS text exceeds {MaxTextCharacters} characters.");
        }

        var stableId = Get(message.Metadata, "uuid")
            ?? throw new InvalidOperationException(
                "Feishu TTS delivery is missing a stable uuid.");
        var voice = Get(message.Metadata, MessageGatewayMetadata.TtsVoice)
            ?? binding.TtsVoice;
        var synthesized = await voiceSynthesis.SynthesizeAsync(
            new VoiceSynthesisRequest
            {
                WorkspaceId = binding.WorkspaceId,
                MessageId = stableId,
                DeliveryId = stableId,
                Text = message.Content,
                Provider = VoiceSynthesisProviders.Unknown,
                Model = "",
                Voice = string.IsNullOrWhiteSpace(voice) ? "Cherry" : voice,
                AudioFormat = VoiceAudioFormats.Wav,
                SampleRate = 24_000,
                OutputMode = VoiceSynthesisOutputModes.NonRealtimeFile,
            },
            ct);
        if (synthesized.AudioBytes is not { Length: > 0 } wav)
            throw new InvalidOperationException("TTS returned no materialized audio bytes.");

        var transcoded = await audioTranscoder.TranscodeAsync(
            new AudioTranscodingRequest
            {
                Content = wav,
                SourceFormat = synthesized.Format,
                TargetFormat = VoiceAudioFormats.Opus,
                TargetSampleRate = 16_000,
                TargetChannels = 1,
                TargetBitrate = 24_000,
            },
            ct);
        logger.LogInformation(
            "[FeishuTts] Audio prepared connector={ConnectorId} message={MessageId} voice={Voice} bytes={Bytes} durationMs={DurationMs}",
            FeishuConnectorIdentity.ForChannel(binding.ChannelId ?? binding.AgentId),
            stableId,
            voice,
            transcoded.Content.Length,
            transcoded.DurationMs);
        return transcoded;
    }

    private static string? Get(
        IReadOnlyDictionary<string, string> metadata,
        string key)
        => metadata.TryGetValue(key, out var value)
           && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
}

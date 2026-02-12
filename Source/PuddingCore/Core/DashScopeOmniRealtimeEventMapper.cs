using System.Text.Json.Nodes;
using PuddingCode.Models;

namespace PuddingCode.Core;

/// <summary>
/// Maps DashScope Qwen-Omni-Realtime server events into Pudding's normalized realtime
/// multimodal event stream. Raw provider JSON is intentionally not copied to the output.
/// </summary>
public static class DashScopeOmniRealtimeEventMapper
{
    public static OmniRealtimeStreamEvent? TryMap(string json, string sessionId, int sequence)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch
        {
            return null;
        }

        if (root is not JsonObject obj)
            return null;

        var providerType = obj["type"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(providerType))
            return null;

        return providerType switch
        {
            "session.created" => Create(
                OmniRealtimeStreamEventTypes.SessionCreated,
                sessionId,
                sequence,
                providerSessionId: obj["session"]?["id"]?.GetValue<string>()),

            "session.updated" => Create(
                OmniRealtimeStreamEventTypes.SessionUpdated,
                sessionId,
                sequence,
                providerSessionId: obj["session"]?["id"]?.GetValue<string>()),

            "input_audio_buffer.speech_started" => Create(
                OmniRealtimeStreamEventTypes.SpeechStarted,
                sessionId,
                sequence),

            "input_audio_buffer.speech_stopped" => Create(
                OmniRealtimeStreamEventTypes.SpeechStopped,
                sessionId,
                sequence),

            "input_audio_buffer.committed" => Create(
                OmniRealtimeStreamEventTypes.InputCommitted,
                sessionId,
                sequence),

            "response.created" => Create(
                OmniRealtimeStreamEventTypes.ResponseCreated,
                sessionId,
                sequence,
                providerResponseId: obj["response"]?["id"]?.GetValue<string>()),

            "conversation.item.input_audio_transcription.delta" => Create(
                OmniRealtimeStreamEventTypes.InputTranscriptDelta,
                sessionId,
                sequence,
                textDelta: (obj["text"]?.GetValue<string>() ?? "") + (obj["stash"]?.GetValue<string>() ?? "")),

            "conversation.item.input_audio_transcription.completed" => Create(
                OmniRealtimeStreamEventTypes.InputTranscriptCompleted,
                sessionId,
                sequence,
                transcript: obj["transcript"]?.GetValue<string>()),

            "response.text.delta" => Create(
                OmniRealtimeStreamEventTypes.ResponseTextDelta,
                sessionId,
                sequence,
                textDelta: obj["delta"]?.GetValue<string>()),

            "response.text.done" => Create(
                OmniRealtimeStreamEventTypes.ResponseTextDone,
                sessionId,
                sequence,
                transcript: obj["text"]?.GetValue<string>()),

            "response.audio_transcript.delta" => Create(
                OmniRealtimeStreamEventTypes.ResponseAudioTranscriptDelta,
                sessionId,
                sequence,
                textDelta: obj["delta"]?.GetValue<string>()),

            "response.audio_transcript.done" => Create(
                OmniRealtimeStreamEventTypes.ResponseAudioTranscriptDone,
                sessionId,
                sequence,
                transcript: obj["transcript"]?.GetValue<string>()),

            "response.audio.delta" => Create(
                OmniRealtimeStreamEventTypes.ResponseAudioDelta,
                sessionId,
                sequence,
                audioBytes: TryDecodeBase64(obj["delta"]?.GetValue<string>())),

            "response.audio.done" => Create(
                OmniRealtimeStreamEventTypes.ResponseAudioDone,
                sessionId,
                sequence),

            "response.done" => Create(
                OmniRealtimeStreamEventTypes.ResponseDone,
                sessionId,
                sequence,
                providerResponseId: obj["response"]?["id"]?.GetValue<string>(),
                usage: ParseUsage(obj["response"]?["usage"])),

            "error" => Create(
                OmniRealtimeStreamEventTypes.Failed,
                sessionId,
                sequence,
                errorCode: obj["error"]?["code"]?.GetValue<string>(),
                errorMessage: obj["error"]?["message"]?.GetValue<string>() ?? obj["message"]?.GetValue<string>()),

            _ => null,
        };
    }

    private static OmniRealtimeStreamEvent Create(
        string type,
        string sessionId,
        int sequence,
        string? textDelta = null,
        string? transcript = null,
        byte[]? audioBytes = null,
        string? providerSessionId = null,
        string? providerResponseId = null,
        OmniRealtimeUsage? usage = null,
        string? errorCode = null,
        string? errorMessage = null) => new()
        {
            Type = type,
            SessionId = sessionId,
            Sequence = sequence,
            TextDelta = textDelta,
            Transcript = transcript,
            AudioBytes = audioBytes,
            ProviderSessionId = providerSessionId,
            ProviderResponseId = providerResponseId,
            Usage = usage,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
        };

    private static byte[]? TryDecodeBase64(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            return Convert.FromBase64String(value);
        }
        catch
        {
            return null;
        }
    }

    private static OmniRealtimeUsage? ParseUsage(JsonNode? usage)
    {
        if (usage is not JsonObject obj)
            return null;

        var inputDetails = obj["input_tokens_details"];
        var outputDetails = obj["output_tokens_details"];
        var search = obj["plugins"]?["search"];

        return new OmniRealtimeUsage
        {
            InputTokens = TryGetInt(obj["input_tokens"]),
            OutputTokens = TryGetInt(obj["output_tokens"]),
            TotalTokens = TryGetInt(obj["total_tokens"]),
            AudioInputTokens = TryGetInt(inputDetails?["audio_tokens"]),
            AudioOutputTokens = TryGetInt(outputDetails?["audio_tokens"]),
            ImageInputTokens = TryGetInt(inputDetails?["image_tokens"]),
            SearchCount = TryGetInt(search?["count"]),
            SearchStrategy = search?["strategy"]?.GetValue<string>(),
        };
    }

    private static int? TryGetInt(JsonNode? value)
    {
        if (value is null)
            return null;

        try
        {
            return value.GetValue<int>();
        }
        catch
        {
            return null;
        }
    }
}

namespace PuddingCode.Models;

/// <summary>Known voice recognition providers. Provider implementations may add their own identifiers.</summary>
public static class VoiceRecognitionProviders
{
    public const string DashScope = "dashscope";
    public const string Local = "local";
    public const string Unknown = "unknown";
}

/// <summary>Transport used by an ASR provider for realtime recognition.</summary>
public static class VoiceRecognitionTransports
{
    public const string WebSocket = "websocket";
}

/// <summary>Turn detection mode for realtime speech recognition.</summary>
public static class VoiceRecognitionTurnModes
{
    public const string ServerVad = "server_vad";
    public const string Manual = "manual";
}

/// <summary>Normalized ASR stream event types consumed by voice session projections.</summary>
public static class VoiceRecognitionStreamEventTypes
{
    public const string SessionStarted = "session_started";
    public const string SessionUpdated = "session_updated";
    public const string SpeechStarted = "speech_started";
    public const string SpeechStopped = "speech_stopped";
    public const string Transcript = "transcript";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

/// <summary>Normalized emotion labels surfaced by ASR providers when available.</summary>
public static class VoiceRecognitionEmotions
{
    public const string Surprised = "surprised";
    public const string Neutral = "neutral";
    public const string Happy = "happy";
    public const string Sad = "sad";
    public const string Disgusted = "disgusted";
    public const string Angry = "angry";
    public const string Fearful = "fearful";
}

/// <summary>
/// Business-level ASR request. Secrets are intentionally excluded; providers resolve credentials
/// from server-side KeyVault or environment configuration.
/// </summary>
public sealed record VoiceRecognitionRequest
{
    public required string WorkspaceId { get; init; }
    public required string RoomId { get; init; }
    public required string ParticipantId { get; init; }
    public required string SessionId { get; init; }
    public string Provider { get; init; } = VoiceRecognitionProviders.Unknown;
    public required string Model { get; init; }
    public string Transport { get; init; } = VoiceRecognitionTransports.WebSocket;
    public string TurnMode { get; init; } = VoiceRecognitionTurnModes.ServerVad;
    public string AudioFormat { get; init; } = VoiceAudioFormats.Pcm;
    public int SampleRate { get; init; } = 16_000;
    public string Language { get; init; } = "Auto";
    public bool EnablePunctuation { get; init; } = true;
    public bool EnableEmotion { get; init; }
    public bool EnableTimestamps { get; init; }
    public bool EnableHotWords { get; init; }
    public int? SilenceDurationMs { get; init; }
    public double? VadThreshold { get; init; }
    public string? TraceId { get; init; }
    public IReadOnlyDictionary<string, string> HotWords { get; init; } = new Dictionary<string, string>();
}

/// <summary>One audio frame captured by a client and normalized before provider upload.</summary>
public sealed record VoiceAudioFrame
{
    public required string SessionId { get; init; }
    public int Sequence { get; init; }
    public required byte[] AudioBytes { get; init; }
    public string Format { get; init; } = VoiceAudioFormats.Pcm;
    public int SampleRate { get; init; } = 16_000;
    public int? DurationMs { get; init; }
    public long CapturedAt { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>Word-level ASR timing information when the provider supports it.</summary>
public sealed record VoiceTranscriptWord
{
    public required string Text { get; init; }
    public int? BeginTimeMs { get; init; }
    public int? EndTimeMs { get; init; }
    public string? Punctuation { get; init; }
}

/// <summary>Normalized realtime ASR event emitted by providers.</summary>
public sealed record VoiceRecognitionStreamEvent
{
    public required string Type { get; init; }
    public required string SessionId { get; init; }
    public string? Text { get; init; }
    public bool IsFinal { get; init; }
    public int Sequence { get; init; }
    public string? Language { get; init; }
    public string? Emotion { get; init; }
    public IReadOnlyList<VoiceTranscriptWord> Words { get; init; } = [];
    public string? ProviderSessionId { get; init; }
    public string? ProviderRequestId { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Reserved for trusted diagnostics only. UI-facing projections should keep this null to avoid
    /// leaking provider payloads, audio data, or credential-adjacent fields.
    /// </summary>
    public string? ProviderRawPayload { get; init; }

    public static VoiceRecognitionStreamEvent Transcript(
        string sessionId,
        string text,
        bool isFinal,
        int sequence,
        string? emotion = null,
        IReadOnlyList<VoiceTranscriptWord>? words = null) => new()
        {
            Type = VoiceRecognitionStreamEventTypes.Transcript,
            SessionId = sessionId,
            Text = text,
            IsFinal = isFinal,
            Sequence = sequence,
            Emotion = emotion,
            Words = words ?? [],
        };
}

/// <summary>Final ASR result ready to become a voice-originated message draft.</summary>
public sealed record VoiceRecognitionResult
{
    public required string SessionId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string RoomId { get; init; }
    public required string ParticipantId { get; init; }
    public required string Text { get; init; }
    public string? Language { get; init; }
    public string? Emotion { get; init; }
    public long CompletedAt { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public IReadOnlyList<VoiceTranscriptWord> Words { get; init; } = [];
    public IDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

namespace PuddingCode.Models;

/// <summary>Known realtime multimodal providers.</summary>
public static class OmniRealtimeProviders
{
    public const string DashScope = "dashscope";
    public const string Local = "local";
    public const string Unknown = "unknown";
}

/// <summary>Transport used by realtime multimodal sessions.</summary>
public static class OmniRealtimeTransports
{
    public const string WebSocket = "websocket";
    public const string WebRtc = "webrtc";
}

/// <summary>Output modalities requested from an omni realtime model.</summary>
public static class OmniRealtimeModalities
{
    public const string Text = "text";
    public const string Audio = "audio";
}

/// <summary>Input frame kinds accepted by an omni realtime session.</summary>
public static class OmniRealtimeInputKinds
{
    public const string Audio = "audio";
    public const string Image = "image";
    public const string VideoFrame = "video_frame";
}

/// <summary>Turn detection mode for realtime multimodal conversation.</summary>
public static class OmniRealtimeTurnModes
{
    public const string ServerVad = "server_vad";
    public const string SemanticVad = "semantic_vad";
    public const string Manual = "manual";
}

/// <summary>Normalized stream event types emitted by realtime multimodal providers.</summary>
public static class OmniRealtimeStreamEventTypes
{
    public const string SessionCreated = "session_created";
    public const string SessionUpdated = "session_updated";
    public const string SpeechStarted = "speech_started";
    public const string SpeechStopped = "speech_stopped";
    public const string InputCommitted = "input_committed";
    public const string InputTranscriptDelta = "input_transcript_delta";
    public const string InputTranscriptCompleted = "input_transcript_completed";
    public const string ResponseCreated = "response_created";
    public const string ResponseTextDelta = "response_text_delta";
    public const string ResponseTextDone = "response_text_done";
    public const string ResponseAudioTranscriptDelta = "response_audio_transcript_delta";
    public const string ResponseAudioTranscriptDone = "response_audio_transcript_done";
    public const string ResponseAudioDelta = "response_audio_delta";
    public const string ResponseAudioDone = "response_audio_done";
    public const string ResponseDone = "response_done";
    public const string Failed = "failed";
}

/// <summary>
/// Business-level realtime multimodal session request. Credentials are intentionally excluded;
/// providers resolve API keys from server-side configuration or KeyVault.
/// </summary>
public sealed record OmniRealtimeSessionRequest
{
    public required string WorkspaceId { get; init; }
    public required string RoomId { get; init; }
    public required string ParticipantId { get; init; }
    public required string SessionId { get; init; }
    public string Provider { get; init; } = OmniRealtimeProviders.Unknown;
    public required string Model { get; init; }
    public string Transport { get; init; } = OmniRealtimeTransports.WebSocket;
    public IReadOnlyList<string> OutputModalities { get; init; } = [OmniRealtimeModalities.Text];
    public string? Voice { get; init; }
    public string InputAudioFormat { get; init; } = VoiceAudioFormats.Pcm;
    public string OutputAudioFormat { get; init; } = VoiceAudioFormats.Pcm;
    public int InputSampleRate { get; init; } = 16_000;
    public int OutputSampleRate { get; init; } = 24_000;
    public string TurnMode { get; init; } = OmniRealtimeTurnModes.SemanticVad;
    public double? VadThreshold { get; init; }
    public int? SilenceDurationMs { get; init; }
    public string? Instructions { get; init; }
    public bool EnableInputAudioTranscription { get; init; }
    public string? InputAudioTranscriptionModel { get; init; }
    public bool EnableSearch { get; init; }
    public bool EnableSearchSource { get; init; }
    public string? TraceId { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Browser or server captured realtime media frame. Raw bytes are transient transport input and
/// must not be persisted as chat metadata or long-lived UI state.
/// </summary>
public sealed record OmniRealtimeInputFrame
{
    public required string SessionId { get; init; }
    public required string Kind { get; init; }
    public required byte[] Bytes { get; init; }
    public int Sequence { get; init; }
    public string Format { get; init; } = VoiceAudioFormats.Pcm;
    public string? MimeType { get; init; }
    public int? SampleRate { get; init; }
    public int? DurationMs { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public long CapturedAt { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static OmniRealtimeInputFrame Audio(
        string sessionId,
        byte[] audioBytes,
        int sequence,
        int? durationMs = null,
        int sampleRate = 16_000,
        string format = VoiceAudioFormats.Pcm) => new()
        {
            SessionId = sessionId,
            Kind = OmniRealtimeInputKinds.Audio,
            Bytes = audioBytes,
            Sequence = sequence,
            DurationMs = durationMs,
            SampleRate = sampleRate,
            Format = format,
        };

    public static OmniRealtimeInputFrame Image(
        string sessionId,
        byte[] imageBytes,
        string mimeType,
        int sequence,
        int? width = null,
        int? height = null) => new()
        {
            SessionId = sessionId,
            Kind = OmniRealtimeInputKinds.Image,
            Bytes = imageBytes,
            Sequence = sequence,
            Format = mimeType,
            MimeType = mimeType,
            Width = width,
            Height = height,
        };
}

/// <summary>Normalized usage for realtime multimodal responses.</summary>
public sealed record OmniRealtimeUsage
{
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public int? TotalTokens { get; init; }
    public int? AudioInputTokens { get; init; }
    public int? AudioOutputTokens { get; init; }
    public int? ImageInputTokens { get; init; }
    public int? SearchCount { get; init; }
    public string? SearchStrategy { get; init; }
}

/// <summary>Normalized realtime multimodal provider event consumed by projections.</summary>
public sealed record OmniRealtimeStreamEvent
{
    public required string Type { get; init; }
    public required string SessionId { get; init; }
    public int Sequence { get; init; }
    public string? TextDelta { get; init; }
    public string? Transcript { get; init; }
    public byte[]? AudioBytes { get; init; }
    public string? ProviderSessionId { get; init; }
    public string? ProviderResponseId { get; init; }
    public string? ProviderItemId { get; init; }
    public OmniRealtimeUsage? Usage { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Trusted diagnostics only. UI-facing projections should keep this null to avoid leaking
    /// provider payloads, media data, or credential-adjacent fields.
    /// </summary>
    public string? ProviderRawPayload { get; init; }
}

/// <summary>Provider capability description for realtime multimodal model selection.</summary>
public sealed record OmniRealtimeProviderCapabilities
{
    public required string Provider { get; init; }
    public IReadOnlyList<string> SupportedTransports { get; init; } = [];
    public IReadOnlyList<string> SupportedModels { get; init; } = [];
    public bool RequiresAudioInput { get; init; } = true;
    public bool SupportsImageInput { get; init; }
    public bool SupportsVideoInput { get; init; }
    public bool SupportsAudioOutput { get; init; }
    public bool SupportsTextOutput { get; init; } = true;
    public bool SupportsServerVad { get; init; }
    public bool SupportsSemanticVad { get; init; }
    public bool SupportsManualTurn { get; init; }
    public bool SupportsSearch { get; init; }
    public int? MaxSessionMinutes { get; init; }
    public int? SuggestedVideoFrameIntervalMs { get; init; }
    public bool RequiresServerSideCredential { get; init; } = true;
}

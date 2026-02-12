namespace PuddingCode.Models;

/// <summary>Known voice synthesis providers. Provider implementations may add their own identifiers.</summary>
public static class VoiceSynthesisProviders
{
    public const string DashScope = "dashscope";
    public const string Local = "local";
    public const string Unknown = "unknown";
}

/// <summary>Transport used by a TTS provider for a synthesis request.</summary>
public static class VoiceSynthesisTransports
{
    public const string Http = "http";
    public const string Sse = "sse";
    public const string WebSocket = "websocket";
}

/// <summary>High-level output mode expected by the caller.</summary>
public static class VoiceSynthesisOutputModes
{
    public const string NonRealtimeFile = "non_realtime_file";
    public const string StreamingChunks = "streaming_chunks";
    public const string RealtimeDuplex = "realtime_duplex";
}

/// <summary>Provider session mode for text buffering and commit semantics.</summary>
public static class VoiceSynthesisSessionModes
{
    public const string SingleTurn = "single_turn";
    public const string ServerCommit = "server_commit";
    public const string Commit = "commit";
}

/// <summary>Audio formats exposed above provider-specific protocol details.</summary>
public static class VoiceAudioFormats
{
    public const string Pcm = "pcm";
    public const string Wav = "wav";
    public const string Mp3 = "mp3";
    public const string Opus = "opus";
}

/// <summary>Stream event types emitted by realtime or streaming TTS providers.</summary>
public static class VoiceSynthesisStreamEventTypes
{
    public const string SessionStarted = "session_started";
    public const string SessionUpdated = "session_updated";
    public const string AudioDelta = "audio_delta";
    public const string AudioDone = "audio_done";
    public const string ResponseDone = "response_done";
    public const string SessionFinished = "session_finished";
    public const string Failed = "failed";
}

/// <summary>
/// Business-level TTS request. Secrets are intentionally excluded; providers resolve credentials
/// from server-side KeyVault or environment configuration.
/// </summary>
public sealed record VoiceSynthesisRequest
{
    public required string WorkspaceId { get; init; }
    public required string MessageId { get; init; }
    public string? DeliveryId { get; init; }
    public required string Text { get; init; }
    public IReadOnlyList<string> TextChunks { get; init; } = [];
    public string Provider { get; init; } = VoiceSynthesisProviders.Unknown;
    public required string Model { get; init; }
    public required string Voice { get; init; }
    public string LanguageType { get; init; } = "Auto";
    public string? Instructions { get; init; }
    public bool OptimizeInstructions { get; init; }
    public string Transport { get; init; } = VoiceSynthesisTransports.Http;
    public string OutputMode { get; init; } = VoiceSynthesisOutputModes.NonRealtimeFile;
    public string SessionMode { get; init; } = VoiceSynthesisSessionModes.SingleTurn;
    public string AudioFormat { get; init; } = VoiceAudioFormats.Mp3;
    public int SampleRate { get; init; } = 24_000;
    public int? Bitrate { get; init; }
    public double? Speed { get; init; }
    public double? Pitch { get; init; }
    public double? Volume { get; init; }
    public string? TraceId { get; init; }
}

/// <summary>Provider capability description used by policy, diagnostics, and admin UI.</summary>
public sealed record VoiceSynthesisProviderCapabilities
{
    public required string Provider { get; init; }
    public IReadOnlyList<string> SupportedTransports { get; init; } = [];
    public IReadOnlyList<string> SupportedSessionModes { get; init; } = [];
    public IReadOnlyList<string> SupportedAudioFormats { get; init; } = [];
    public IReadOnlyList<int> SupportedSampleRates { get; init; } = [];
    public bool SupportsConnectionReuse { get; init; }
    public bool SupportsVoiceCloning { get; init; }
    public bool SupportsVoiceDesign { get; init; }
    public bool SupportsInstructions { get; init; }
    public bool RequiresServerSideCredential { get; init; } = true;
    public int? MaxConnectionIdleSeconds { get; init; }
}

/// <summary>Final result for non-streaming or completed streaming synthesis.</summary>
public sealed record VoiceSynthesisResult
{
    public required string MessageId { get; init; }
    public string? DeliveryId { get; init; }
    public string? AudioArtifactId { get; init; }
    public string? AudioUrl { get; init; }
    public long? ExpiresAt { get; init; }
    public required string Format { get; init; }
    public int SampleRate { get; init; }
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public string? RequestId { get; init; }
    public long? FirstAudioDelayMs { get; init; }
    public long? DurationMs { get; init; }
}

/// <summary>Normalized streaming TTS event consumed by projections and clients.</summary>
public sealed record VoiceSynthesisStreamEvent
{
    public required string Type { get; init; }
    public required string MessageId { get; init; }
    public string? DeliveryId { get; init; }
    public byte[]? AudioBytes { get; init; }
    public string? Format { get; init; }
    public int? SampleRate { get; init; }
    public int Sequence { get; init; }
    public string? ProviderSessionId { get; init; }
    public string? ProviderResponseId { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Reserved for diagnostics in trusted server logs only. It should stay null for UI-facing
    /// projections so provider payloads and secrets do not leak.
    /// </summary>
    public string? ProviderRawPayload { get; init; }

    public static VoiceSynthesisStreamEvent AudioDelta(
        string messageId,
        string? deliveryId,
        byte[] audioBytes,
        string format,
        int sampleRate,
        int sequence) => new()
        {
            Type = VoiceSynthesisStreamEventTypes.AudioDelta,
            MessageId = messageId,
            DeliveryId = deliveryId,
            AudioBytes = audioBytes,
            Format = format,
            SampleRate = sampleRate,
            Sequence = sequence,
        };
}

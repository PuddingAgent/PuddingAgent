namespace PuddingCode.Models;

/// <summary>Known visual reasoning providers. Provider implementations may add their own identifiers.</summary>
public static class VisualReasoningProviders
{
    public const string DashScope = "dashscope";
    public const string Local = "local";
    public const string Unknown = "unknown";
}

/// <summary>Transport used by a visual reasoning provider.</summary>
public static class VisualReasoningTransports
{
    public const string Http = "http";
    public const string Sse = "sse";
    public const string OpenAiCompatibleSse = "openai_compatible_sse";
}

/// <summary>High-level response mode expected by the caller.</summary>
public static class VisualReasoningOutputModes
{
    public const string NonStreaming = "non_streaming";
    public const string Streaming = "streaming";
}

/// <summary>Thinking behavior for visual reasoning models.</summary>
public static class VisualReasoningThinkingModes
{
    public const string AlwaysOn = "always_on";
    public const string Toggleable = "toggleable";
    public const string Disabled = "disabled";
}

/// <summary>Input artifact kinds accepted by visual reasoning requests.</summary>
public static class VisualInputKinds
{
    public const string CameraFrame = "camera_frame";
    public const string ImageArtifact = "image_artifact";
    public const string ImageUrl = "image_url";
    public const string VideoArtifact = "video_artifact";
    public const string VideoUrl = "video_url";
}

/// <summary>Common visual MIME types used by browser capture and uploaded artifacts.</summary>
public static class VisualMimeTypes
{
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string Webp = "image/webp";
    public const string Mp4 = "video/mp4";
}

/// <summary>Normalized visual reasoning stream event types consumed by projections and clients.</summary>
public static class VisualReasoningStreamEventTypes
{
    public const string SessionStarted = "session_started";
    public const string ReasoningDelta = "reasoning_delta";
    public const string AnswerDelta = "answer_delta";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

/// <summary>
/// Visual input reference. Raw image/video bytes are intentionally excluded from the request model;
/// callers should pass an artifact id or URL that can be resolved by trusted server-side code.
/// </summary>
public sealed record VisualInputArtifact
{
    public required string ArtifactId { get; init; }
    public required string Kind { get; init; }
    public required string MimeType { get; init; }
    public string? Uri { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public long? DurationMs { get; init; }
    public long CapturedAt { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>
/// Business-level visual reasoning request. Secrets are intentionally excluded; providers resolve
/// credentials from server-side KeyVault or environment configuration.
/// </summary>
public sealed record VisualReasoningRequest
{
    public required string WorkspaceId { get; init; }
    public required string RoomId { get; init; }
    public required string ParticipantId { get; init; }
    public required string SessionId { get; init; }
    public string Provider { get; init; } = VisualReasoningProviders.Unknown;
    public required string Model { get; init; }
    public string Transport { get; init; } = VisualReasoningTransports.Sse;
    public string OutputMode { get; init; } = VisualReasoningOutputModes.Streaming;
    public string ThinkingMode { get; init; } = VisualReasoningThinkingModes.Toggleable;
    public bool EnableThinking { get; init; }
    public int? ThinkingBudgetTokens { get; init; }
    public required string Prompt { get; init; }
    public IReadOnlyList<VisualInputArtifact> Inputs { get; init; } = [];
    public string? SystemPrompt { get; init; }
    public string? TraceId { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>Normalized streaming visual reasoning event emitted by providers.</summary>
public sealed record VisualReasoningStreamEvent
{
    public required string Type { get; init; }
    public required string SessionId { get; init; }
    public string? ReasoningDelta { get; init; }
    public string? AnswerDelta { get; init; }
    public int Sequence { get; init; }
    public string? ProviderRequestId { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Reserved for trusted diagnostics only. UI-facing projections should keep this null to avoid
    /// leaking provider payloads, image data, or credential-adjacent fields.
    /// </summary>
    public string? ProviderRawPayload { get; init; }

    public static VisualReasoningStreamEvent CreateReasoningDelta(
        string sessionId,
        string delta,
        int sequence) => new()
        {
            Type = VisualReasoningStreamEventTypes.ReasoningDelta,
            SessionId = sessionId,
            ReasoningDelta = delta,
            Sequence = sequence,
        };

    public static VisualReasoningStreamEvent CreateAnswerDelta(
        string sessionId,
        string delta,
        int sequence) => new()
        {
            Type = VisualReasoningStreamEventTypes.AnswerDelta,
            SessionId = sessionId,
            AnswerDelta = delta,
            Sequence = sequence,
        };
}

/// <summary>Final visual reasoning result ready for projections, messages, or tool outputs.</summary>
public sealed record VisualReasoningResult
{
    public required string SessionId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string RoomId { get; init; }
    public required string ParticipantId { get; init; }
    public required string Answer { get; init; }
    public string? ReasoningSummary { get; init; }
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public string? RequestId { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public int? ImageTokens { get; init; }
    public int? VideoTokens { get; init; }
    public int? TotalTokens => InputTokens is null && OutputTokens is null
        ? null
        : (InputTokens ?? 0) + (OutputTokens ?? 0);
    public long CompletedAt { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public IDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>Per-model capability description used by policy, diagnostics, and admin UI.</summary>
public sealed record VisualReasoningModelCapability
{
    public required string Model { get; init; }
    public string ThinkingMode { get; init; } = VisualReasoningThinkingModes.Toggleable;
    public bool RequiresStreaming { get; init; }
    public bool SupportsEnableThinking { get; init; }
    public bool SupportsThinkingBudget { get; init; }
    public IReadOnlyList<string> SupportedInputKinds { get; init; } = [];
}

/// <summary>Provider capability description for visual reasoning model selection.</summary>
public sealed record VisualReasoningProviderCapabilities
{
    public required string Provider { get; init; }
    public IReadOnlyList<string> SupportedTransports { get; init; } = [];
    public IReadOnlyList<VisualReasoningModelCapability> SupportedModels { get; init; } = [];
    public bool RequiresServerSideCredential { get; init; } = true;
}

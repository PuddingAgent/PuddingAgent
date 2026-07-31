namespace PuddingCode.Models;

public sealed record AudioTranscriptionRequest
{
    public required byte[] Content { get; init; }
    public string Format { get; init; } = VoiceAudioFormats.Wav;
    public string? Language { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
}

public sealed record AudioTranscriptionResult
{
    public required string Text { get; init; }
    public string? Emotion { get; init; }
    public required string Provider { get; init; }
    public required string Model { get; init; }
}

namespace PuddingCode.Models;

/// <summary>Provider-neutral in-memory audio transcoding request.</summary>
public sealed record AudioTranscodingRequest
{
    public required byte[] Content { get; init; }
    public required string SourceFormat { get; init; }
    public required string TargetFormat { get; init; }
    public int TargetSampleRate { get; init; } = 16_000;
    public int TargetChannels { get; init; } = 1;
    public int? TargetBitrate { get; init; }
}

/// <summary>Materialized audio after an in-process transcode.</summary>
public sealed record AudioTranscodingResult
{
    public required byte[] Content { get; init; }
    public required string Format { get; init; }
    public required string MediaType { get; init; }
    public int SampleRate { get; init; }
    public int Channels { get; init; }
    public long DurationMs { get; init; }
}

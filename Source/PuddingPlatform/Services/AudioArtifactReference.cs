namespace PuddingPlatform.Services;

public sealed record AudioArtifactReference(
    string ArtifactId,
    string Uri,
    string MimeType,
    string Format,
    long? DurationMs,
    long? CapturedAt);

public sealed record AudioArtifactLocalFile(
    string ArtifactId,
    string Path,
    string MimeType,
    string Format,
    long? DurationMs,
    long? CapturedAt);

public interface IAudioArtifactReferenceResolver
{
    Task<AudioArtifactReference?> ResolveAsync(
        string workspaceId,
        string artifactId,
        CancellationToken ct = default);
}

public interface IAudioArtifactLocalFileResolver
{
    Task<AudioArtifactLocalFile?> ResolveLocalFileAsync(
        string workspaceId,
        string artifactId,
        CancellationToken ct = default);
}

using PuddingCode.Models;

namespace PuddingPlatform.Services;

/// <summary>Resolved server-side visual artifact reference safe to pass to Core visual reasoning.</summary>
public sealed record VisualArtifactReference(
    string ArtifactId,
    string Uri,
    string MimeType,
    int? Width,
    int? Height,
    long? CapturedAt);

/// <summary>Server-authorized local file backing a visual artifact.</summary>
public sealed record VisualArtifactLocalFile(
    string ArtifactId,
    string Path,
    string MimeType,
    int? Width,
    int? Height,
    long? CapturedAt);

/// <summary>Resolves a user-facing artifact id into a server-authorized URI.</summary>
public interface IVisualArtifactReferenceResolver
{
    Task<VisualArtifactReference?> ResolveAsync(
        string workspaceId,
        string artifactId,
        CancellationToken ct = default);
}

/// <summary>
/// Resolves a browser artifact id to its controlled workspace-local file.
/// This is used to tell a text-only main Agent that an image is available to
/// an explicit image-reading tool without exposing arbitrary client paths.
/// </summary>
public interface IVisualArtifactLocalFileResolver
{
    Task<VisualArtifactLocalFile?> ResolveLocalFileAsync(
        string workspaceId,
        string artifactId,
        CancellationToken ct = default);
}

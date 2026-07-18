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

/// <summary>Resolves a user-facing artifact id into a server-authorized URI.</summary>
public interface IVisualArtifactReferenceResolver
{
    Task<VisualArtifactReference?> ResolveAsync(
        string workspaceId,
        string artifactId,
        CancellationToken ct = default);
}

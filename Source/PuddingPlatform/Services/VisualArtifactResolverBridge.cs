using PuddingCode.Abstractions;

namespace PuddingPlatform.Services;

/// <summary>
/// Prepares every Core model input through workspace storage. The original Artifact remains immutable.
/// </summary>
public sealed class VisualArtifactResolverBridge(VisionArtifactStorageService storage) : IVisualArtifactResolver
{
    public async Task<VisualArtifactResolveResult?> ResolveAsync(
        string workspaceId,
        string artifactId,
        CancellationToken ct = default, string detail = PuddingCode.Models.VisionContentPartDetails.Original)
    {
        var reference = await storage.ResolveForModelAsync(workspaceId, artifactId, detail, ct);
        if (reference is null)
            return null;

        return new VisualArtifactResolveResult(
            reference.ArtifactId,
            reference.Uri,
            reference.MimeType);
    }
}

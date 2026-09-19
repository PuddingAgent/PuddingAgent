using PuddingCode.Abstractions;

namespace PuddingPlatform.Services;

/// <summary>
/// Prepares every Core model input through workspace storage. The original Artifact remains immutable.
/// </summary>
public sealed class VisualArtifactResolverBridge(VisionArtifactStorageService storage) : IVisualArtifactPreprocessor
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
            reference.MimeType, reference.Width, reference.Height);
    }

    public async Task<VisualArtifactResolveResult?> PrepareForRequestAsync(string workspaceId, string artifactId,
        VisualArtifactPreparationOptions options, CancellationToken ct = default, string detail = "original")
    {
        var reference = await storage.ResolveForRequestAsync(workspaceId, artifactId, detail, options, ct);
        return reference is null ? null : new VisualArtifactResolveResult(artifactId, reference.Uri,
            reference.MimeType, reference.Width, reference.Height);
    }
}

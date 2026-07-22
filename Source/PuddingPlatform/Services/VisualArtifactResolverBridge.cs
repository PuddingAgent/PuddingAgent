using PuddingCode.Abstractions;

namespace PuddingPlatform.Services;

/// <summary>
/// Bridges the Core-level <see cref="IVisualArtifactResolver"/> to the Platform-level
/// <see cref="IVisualArtifactReferenceResolver"/>. Both resolvers are stateless singletons so the
/// singleton runtime LLM client can resolve server-authorized artifacts without capturing a scope.
/// </summary>
public sealed class VisualArtifactResolverBridge : IVisualArtifactResolver
{
    private readonly IVisualArtifactReferenceResolver _platformResolver;

    public VisualArtifactResolverBridge(IVisualArtifactReferenceResolver platformResolver)
    {
        _platformResolver = platformResolver;
    }

    public async Task<VisualArtifactResolveResult?> ResolveAsync(
        string workspaceId,
        string artifactId,
        CancellationToken ct = default)
    {
        var reference = await _platformResolver.ResolveAsync(workspaceId, artifactId, ct);
        if (reference is null)
            return null;

        return new VisualArtifactResolveResult(
            reference.ArtifactId,
            reference.Uri,
            reference.MimeType);
    }
}

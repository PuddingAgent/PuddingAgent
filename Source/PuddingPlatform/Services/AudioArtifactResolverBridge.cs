using PuddingCode.Abstractions;

namespace PuddingPlatform.Services;

public sealed class AudioArtifactResolverBridge(
    IAudioArtifactReferenceResolver platformResolver) : IAudioArtifactResolver
{
    public async Task<AudioArtifactResolveResult?> ResolveAsync(
        string workspaceId,
        string artifactId,
        CancellationToken ct = default)
    {
        var reference = await platformResolver.ResolveAsync(
            workspaceId,
            artifactId,
            ct);
        return reference is null
            ? null
            : new AudioArtifactResolveResult(
                reference.ArtifactId,
                reference.Uri,
                reference.MimeType,
                reference.Format);
    }
}

namespace PuddingCode.Abstractions;

/// <summary>
/// Resolves a server-authorized audio artifact into a provider-safe data URI.
/// Text-only models never receive this resolver.
/// </summary>
public interface IAudioArtifactResolver
{
    Task<AudioArtifactResolveResult?> ResolveAsync(
        string workspaceId,
        string artifactId,
        CancellationToken ct = default);
}

public sealed record AudioArtifactResolveResult(
    string ArtifactId,
    string DataUri,
    string MimeType,
    string Format);

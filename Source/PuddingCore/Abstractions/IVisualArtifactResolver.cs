namespace PuddingCode.Abstractions;

/// <summary>Core-level vision artifact resolver — resolves artifact IDs to data URIs for LLM consumption.</summary>
public interface IVisualArtifactResolver
{
    /// <summary>
    /// Resolve a server-authorized artifact id into a data URI and MIME type.
    /// Returns null when the artifact does not exist.
    /// </summary>
    Task<VisualArtifactResolveResult?> ResolveAsync(
        string workspaceId,
        string artifactId,
        CancellationToken ct = default);
}

/// <summary>Resolved vision artifact ready for provider consumption.</summary>
public sealed record VisualArtifactResolveResult(
    string ArtifactId,
    string DataUri,
    string MimeType);

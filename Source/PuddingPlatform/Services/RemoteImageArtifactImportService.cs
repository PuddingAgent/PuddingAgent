using System.Security.Cryptography;
using System.Text;
using PuddingPlatform.Services.Mcp;

namespace PuddingPlatform.Services;

public sealed record RemoteImageArtifactImportResult(
    string ArtifactId,
    string LocalPath,
    string MimeType,
    long ByteCount,
    bool Reused);

/// <summary>
/// Downloads a public HTTPS image into the current workspace's durable Vision
/// Artifact store. Public-only connection routing prevents DNS-rebinding SSRF.
/// </summary>
public sealed class RemoteImageArtifactImportService(
    IHttpClientFactory httpClientFactory,
    VisionArtifactStorageService storage,
    ILogger<RemoteImageArtifactImportService> logger)
{
    public const int MaxImageBytes = 50 * 1024 * 1024;
    public const string HttpClientName = "RemoteImageImport";

    public static HttpMessageHandler CreatePublicNetworkHandler()
        => McpNetworkPolicy.CreateHandler(allowPrivateNetwork: false);

    public async Task<RemoteImageArtifactImportResult> ImportAsync(
        string workspaceId,
        string sourceUrl,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            throw new InvalidOperationException("Image import requires a workspace id.");
        if (!Uri.TryCreate(sourceUrl?.Trim(), UriKind.Absolute, out var uri)
            || !string.Equals(
                uri.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            throw new InvalidOperationException(
                "Image import accepts only public HTTPS URLs without embedded credentials.");
        }

        var artifactId = StableArtifactId(workspaceId, uri.AbsoluteUri);
        var existing = await storage.ResolveLocalFileAsync(
            workspaceId,
            artifactId,
            ct);
        if (existing is not null)
        {
            return new RemoteImageArtifactImportResult(
                existing.ArtifactId,
                existing.Path,
                existing.MimeType,
                new FileInfo(existing.Path).Length,
                Reused: true);
        }

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not { } finalUri
            || !string.Equals(
                finalUri.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Image import redirect target must remain HTTPS.");
        }
        if (response.Content.Headers.ContentLength is > MaxImageBytes)
        {
            throw new InvalidOperationException(
                $"Remote image exceeds the {MaxImageBytes}-byte limit.");
        }

        var bytes = await ReadBoundedAsync(
            await response.Content.ReadAsStreamAsync(ct),
            ct);
        var mimeType = DetectMimeType(bytes)
            ?? throw new UnsupportedVisionArtifactMediaTypeException(
                response.Content.Headers.ContentType?.MediaType);
        await using var stream = new MemoryStream(bytes, writable: false);
        var saved = await storage.SaveIdempotentAsync(
            workspaceId,
            artifactId,
            stream,
            mimeType,
            ct: ct);
        var local = await storage.ResolveLocalFileAsync(
                        workspaceId,
                        saved.ArtifactId,
                        ct)
                    ?? throw new InvalidOperationException(
                        "Imported image was stored but could not be resolved.");
        logger.LogInformation(
            "[RemoteImageImport] Stored workspace={WorkspaceId} artifact={ArtifactId} bytes={Bytes} host={Host}",
            workspaceId,
            local.ArtifactId,
            bytes.Length,
            uri.Host);
        return new RemoteImageArtifactImportResult(
            local.ArtifactId,
            local.Path,
            local.MimeType,
            bytes.Length,
            Reused: false);
    }

    private static string StableArtifactId(string workspaceId, string url)
    {
        var digest = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{workspaceId}\nremote-image\n{url}"));
        return $"vision-{Convert.ToHexString(digest).ToLowerInvariant()[..32]}";
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream source,
        CancellationToken ct)
    {
        using var destination = new MemoryStream();
        var buffer = new byte[81_920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0)
                break;
            if (destination.Length + read > MaxImageBytes)
            {
                throw new InvalidOperationException(
                    $"Remote image exceeds the {MaxImageBytes}-byte limit.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        if (destination.Length == 0)
            throw new InvalidOperationException("Remote image is empty.");
        return destination.ToArray();
    }

    private static string? DetectMimeType(ReadOnlySpan<byte> content)
    {
        if (content.Length >= 8
            && content[..8].SequenceEqual(
                new byte[]
                {
                    0x89, 0x50, 0x4E, 0x47,
                    0x0D, 0x0A, 0x1A, 0x0A,
                }))
        {
            return "image/png";
        }
        if (content.Length >= 3
            && content[0] == 0xFF
            && content[1] == 0xD8
            && content[2] == 0xFF)
        {
            return "image/jpeg";
        }
        if (content.Length >= 12
            && content[..4].SequenceEqual("RIFF"u8)
            && content.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }
        return null;
    }
}

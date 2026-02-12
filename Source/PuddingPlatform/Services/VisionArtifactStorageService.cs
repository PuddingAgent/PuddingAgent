using System.Text.Json;
using System.Text.RegularExpressions;
using PuddingCode.Configuration;

namespace PuddingPlatform.Services;

public sealed record VisionArtifactUploadResult(
    string ArtifactId,
    string MimeType,
    int? Width,
    int? Height,
    long CapturedAt);

/// <summary>
/// Stores browser-captured vision frames under the server data root and resolves them
/// into provider-safe references. Client supplied image URLs are intentionally ignored.
/// </summary>
public sealed partial class VisionArtifactStorageService(
    PuddingDataPaths dataPaths,
    ILogger<VisionArtifactStorageService> logger) : IVisualArtifactReferenceResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<VisionArtifactUploadResult> SaveAsync(
        string workspaceId,
        Stream content,
        string mimeType,
        int? width = null,
        int? height = null,
        long? capturedAt = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            throw new InvalidOperationException("Vision artifact upload requires a workspace id.");
        if (content is null || !content.CanRead)
            throw new InvalidOperationException("Vision artifact upload requires readable content.");

        var normalizedMime = NormalizeMimeType(mimeType);
        var artifactId = $"vision-{Guid.NewGuid():N}";
        var storedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var effectiveCapturedAt = capturedAt ?? storedAt;
        var ext = ExtensionForMime(normalizedMime);
        var root = WorkspaceVisionRoot(workspaceId);
        Directory.CreateDirectory(root);

        var bytesPath = Path.Combine(root, $"{artifactId}{ext}");
        var metadataPath = Path.Combine(root, $"{artifactId}.json");

        await using (var file = File.Create(bytesPath))
        {
            await content.CopyToAsync(file, ct);
        }

        var metadata = new VisionArtifactMetadata(
            artifactId,
            normalizedMime,
            Path.GetFileName(bytesPath),
            width,
            height,
            effectiveCapturedAt,
            storedAt);
        await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata, JsonOptions), ct);

        logger.LogInformation(
            "[VisionArtifact] Stored workspace={WorkspaceId} artifact={ArtifactId} mime={MimeType}",
            workspaceId,
            artifactId,
            normalizedMime);

        return new VisionArtifactUploadResult(
            artifactId,
            normalizedMime,
            width,
            height,
            effectiveCapturedAt);
    }

    public async Task<VisualArtifactReference?> ResolveAsync(
        string workspaceId,
        string artifactId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(artifactId))
            return null;
        if (!ArtifactIdRegex().IsMatch(artifactId))
            return null;

        var metadataPath = Path.Combine(WorkspaceVisionRoot(workspaceId), $"{artifactId}.json");
        if (!File.Exists(metadataPath))
            return null;

        VisionArtifactMetadata? metadata;
        try
        {
            var json = await File.ReadAllTextAsync(metadataPath, ct);
            metadata = JsonSerializer.Deserialize<VisionArtifactMetadata>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[VisionArtifact] Failed to read metadata for artifact={ArtifactId}", artifactId);
            return null;
        }

        if (metadata is null || !string.Equals(metadata.ArtifactId, artifactId, StringComparison.Ordinal))
            return null;

        var bytesPath = Path.Combine(WorkspaceVisionRoot(workspaceId), metadata.FileName);
        var fullRoot = Path.GetFullPath(WorkspaceVisionRoot(workspaceId));
        var fullBytesPath = Path.GetFullPath(bytesPath);
        if (!fullBytesPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullBytesPath))
            return null;

        var bytes = await File.ReadAllBytesAsync(fullBytesPath, ct);
        var dataUri = $"data:{metadata.MimeType};base64,{Convert.ToBase64String(bytes)}";
        return new VisualArtifactReference(
            metadata.ArtifactId,
            dataUri,
            metadata.MimeType,
            metadata.Width,
            metadata.Height,
            metadata.CapturedAt);
    }

    private string WorkspaceVisionRoot(string workspaceId) =>
        Path.Combine(dataPaths.WorkspaceRoot(SanitizePathSegment(workspaceId)), "vision-artifacts");

    private static string NormalizeMimeType(string? mimeType)
    {
        var normalized = string.IsNullOrWhiteSpace(mimeType)
            ? "image/jpeg"
            : mimeType.Trim().ToLowerInvariant();

        return normalized switch
        {
            "image/jpg" => "image/jpeg",
            "image/jpeg" or "image/png" or "image/webp" => normalized,
            _ => throw new InvalidOperationException($"Unsupported vision artifact MIME type '{mimeType}'."),
        };
    }

    private static string ExtensionForMime(string mimeType) => mimeType switch
    {
        "image/png" => ".png",
        "image/webp" => ".webp",
        _ => ".jpg",
    };

    private static string SanitizePathSegment(string value)
    {
        var sanitized = PathSegmentRegex().Replace(value.Trim(), "_");
        return string.IsNullOrWhiteSpace(sanitized) ? "default" : sanitized;
    }

    private sealed record VisionArtifactMetadata(
        string ArtifactId,
        string MimeType,
        string FileName,
        int? Width,
        int? Height,
        long CapturedAt,
        long StoredAt);

    [GeneratedRegex("^vision-[a-f0-9]{32}$", RegexOptions.Compiled)]
    private static partial Regex ArtifactIdRegex();

    [GeneratedRegex("[^a-zA-Z0-9._-]+", RegexOptions.Compiled)]
    private static partial Regex PathSegmentRegex();
}

using System.Collections.Concurrent;
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

public sealed class UnsupportedVisionArtifactMediaTypeException(string? mimeType)
    : InvalidOperationException(
        $"Unsupported vision artifact MIME type '{mimeType}'. " +
        "Supported types are image/jpeg, image/png, and image/webp.")
{
    public string? MimeType { get; } = mimeType;
}

/// <summary>
/// Stores browser-captured vision frames under the server data root and resolves them
/// into provider-safe references. Client supplied image URLs are intentionally ignored.
/// </summary>
public sealed partial class VisionArtifactStorageService(
    PuddingDataPaths dataPaths,
    ILogger<VisionArtifactStorageService> logger) :
    IVisualArtifactReferenceResolver,
    IVisualArtifactLocalFileResolver
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _workspaceSaveLocks =
        new(StringComparer.OrdinalIgnoreCase);

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

        var artifactId = $"vision-{Guid.NewGuid():N}";
        return await SaveIdempotentAsync(
            workspaceId,
            artifactId,
            content,
            mimeType,
            width,
            height,
            capturedAt,
            ct);
    }

    /// <summary>
    /// Stores an artifact using a caller-provided stable id. Repeated connector
    /// delivery of the same external resource resolves the first durable copy
    /// instead of creating duplicate browser/Agent attachments.
    /// </summary>
    public async Task<VisionArtifactUploadResult> SaveIdempotentAsync(
        string workspaceId,
        string artifactId,
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
        if (string.IsNullOrWhiteSpace(artifactId) || !ArtifactIdRegex().IsMatch(artifactId))
            throw new InvalidOperationException("Vision artifact id is invalid.");

        var normalizedMime = NormalizeMimeType(mimeType);
        var workspaceKey = SanitizePathSegment(workspaceId);
        var saveLock = _workspaceSaveLocks.GetOrAdd(
            workspaceKey,
            static _ => new SemaphoreSlim(1, 1));
        await saveLock.WaitAsync(ct);
        try
        {
            var existing = await ResolveLocalFileAsync(workspaceId, artifactId, ct);
            if (existing is not null)
            {
                return new VisionArtifactUploadResult(
                    existing.ArtifactId,
                    existing.MimeType,
                    existing.Width,
                    existing.Height,
                    existing.CapturedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }

            return await SaveCoreAsync(
                workspaceId,
                artifactId,
                content,
                normalizedMime,
                width,
                height,
                capturedAt,
                ct);
        }
        finally
        {
            saveLock.Release();
        }
    }

    private async Task<VisionArtifactUploadResult> SaveCoreAsync(
        string workspaceId,
        string artifactId,
        Stream content,
        string normalizedMime,
        int? width,
        int? height,
        long? capturedAt,
        CancellationToken ct)
    {
        var storedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var effectiveCapturedAt = capturedAt ?? storedAt;
        var ext = ExtensionForMime(normalizedMime);
        var root = WorkspaceVisionRoot(workspaceId);
        Directory.CreateDirectory(root);

        var bytesPath = Path.Combine(root, $"{artifactId}{ext}");
        var metadataPath = Path.Combine(root, $"{artifactId}.json");
        var tempSuffix = $".tmp-{Guid.NewGuid():N}";
        var tempBytesPath = bytesPath + tempSuffix;
        var tempMetadataPath = metadataPath + tempSuffix;

        try
        {
            await using (var file = new FileStream(
                tempBytesPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81_920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await content.CopyToAsync(file, ct);
                await file.FlushAsync(ct);
            }

            var metadata = new VisionArtifactMetadata(
                artifactId,
                normalizedMime,
                Path.GetFileName(bytesPath),
                width,
                height,
                effectiveCapturedAt,
                storedAt);
            await File.WriteAllTextAsync(
                tempMetadataPath,
                JsonSerializer.Serialize(metadata, JsonOptions),
                ct);

            File.Move(tempBytesPath, bytesPath, overwrite: true);
            File.Move(tempMetadataPath, metadataPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempBytesPath))
                File.Delete(tempBytesPath);
            if (File.Exists(tempMetadataPath))
                File.Delete(tempMetadataPath);
        }

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
        var localFile = await ResolveLocalFileAsync(workspaceId, artifactId, ct);
        if (localFile is null)
            return null;

        var bytes = await File.ReadAllBytesAsync(localFile.Path, ct);
        var dataUri = $"data:{localFile.MimeType};base64,{Convert.ToBase64String(bytes)}";
        return new VisualArtifactReference(
            localFile.ArtifactId,
            dataUri,
            localFile.MimeType,
            localFile.Width,
            localFile.Height,
            localFile.CapturedAt);
    }

    public async Task<VisualArtifactLocalFile?> ResolveLocalFileAsync(
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

        var fullRoot = Path.GetFullPath(WorkspaceVisionRoot(workspaceId));
        var fullBytesPath = Path.GetFullPath(Path.Combine(fullRoot, metadata.FileName));
        if (!fullBytesPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(fullBytesPath))
        {
            return null;
        }

        return new VisualArtifactLocalFile(
            metadata.ArtifactId,
            fullBytesPath,
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
            _ => throw new UnsupportedVisionArtifactMediaTypeException(mimeType),
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

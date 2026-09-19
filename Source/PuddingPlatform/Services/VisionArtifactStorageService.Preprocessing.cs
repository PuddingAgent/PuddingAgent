using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PuddingCode.Core;
using PuddingCode.Models;

namespace PuddingPlatform.Services;

public sealed partial class VisionArtifactStorageService
{
    private static readonly SemaphoreSlim ProcessingSlot = new(1, 1);
    public async Task<VisionArtifactUploadResult> PrepareAsync(string workspaceId, string artifactId,
        ImageProcessingOptions options, CancellationToken ct = default)
    {
        var source = await ResolveLocalFileAsync(workspaceId, artifactId, ct)
            ?? throw new VisionPipelineException(VisionErrorCodes.ArtifactMissing, "Source artifact is unavailable in this workspace.");
        // Source artifacts are immutable; a versioned transformation key avoids re-decoding on subsequent rounds.
        var key = Encoding.UTF8.GetBytes("image-processing-v1:" + artifactId + ":" + JsonSerializer.Serialize(options));
        var derivedId = "vision-" + Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant()[..32];
        var cached = await ResolveLocalFileAsync(workspaceId, derivedId, ct);
        if (cached is not null)
            return new VisionArtifactUploadResult(cached.ArtifactId, cached.MimeType, cached.Width, cached.Height, cached.CapturedAt ?? 0);
        await ProcessingSlot.WaitAsync(ct);
        try
        {
            cached = await ResolveLocalFileAsync(workspaceId, derivedId, ct);
            if (cached is not null)
                return new VisionArtifactUploadResult(cached.ArtifactId, cached.MimeType, cached.Width, cached.Height, cached.CapturedAt ?? 0);
            var bytes = ImagePreprocessing.Process(source.Path, options, ct);
            await using var content = new MemoryStream(bytes, writable: false);
            return await SaveIdempotentAsync(workspaceId, derivedId, content, "application/octet-stream", ct: ct);
        }
        finally { ProcessingSlot.Release(); }
    }

    /// <summary>All user/history/tool images pass this preparation boundary before wire encoding. Originals remain on disk.</summary>
    public async Task<VisualArtifactReference?> ResolveForModelAsync(string workspaceId, string artifactId,
        string detail, CancellationToken ct = default)
    {
        if (!VisionContentPartDetails.IsValid(detail))
            throw new VisionPipelineException(VisionErrorCodes.SourceInvalid, "detail must be low, high, original or auto.");
        var source = await ResolveLocalFileAsync(workspaceId, artifactId, ct);
        if (source is null) return null;
        var info = ImagePreprocessing.Inspect(source.Path);
        var low = detail == VisionContentPartDetails.Low;
        var oversized = info.Width > ImagePreprocessing.MaxOutputEdge || info.Height > ImagePreprocessing.MaxOutputEdge;
        if ((low && (info.Width > 512 || info.Height > 512)) || oversized || info.MimeType is "image/bmp" or "image/gif" || info.Orientation != "TopLeft")
        {
            var prepared = await PrepareAsync(workspaceId, artifactId,
                new ImageProcessingOptions(MaxEdge: low ? 512 : oversized ? 1536 : 8192, Format: "png"), ct);
            var reference = await ResolveAsync(workspaceId, prepared.ArtifactId, ct);
            return reference is null ? null : reference with { ArtifactId = artifactId };
        }
        return await ResolveAsync(workspaceId, artifactId, ct);
    }

    /// <summary>Fit an immutable source to a per-request pixel and encoded-byte allowance.</summary>
    public async Task<VisualArtifactReference?> ResolveForRequestAsync(string workspaceId, string artifactId,
        string detail, PuddingCode.Abstractions.VisualArtifactPreparationOptions limits, CancellationToken ct = default)
    {
        if (!VisionContentPartDetails.IsValid(detail) || limits.MaxEdge <= 0 || limits.MaxBytes <= 0)
            throw new VisionPipelineException(VisionErrorCodes.SourceInvalid, "Invalid image preparation options.");
        var source = await ResolveLocalFileAsync(workspaceId, artifactId, ct);
        if (source is null) return null;
        var info = ImagePreprocessing.Inspect(source.Path);
        var maxEdge = Math.Min(ImagePreprocessing.MaxOutputEdge, detail == VisionContentPartDetails.Low
            ? Math.Min(512, limits.MaxEdge) : limits.MaxEdge);
        if (info.Width <= maxEdge && info.Height <= maxEdge && info.Bytes <= limits.MaxBytes
            && info.MimeType is not ("image/bmp" or "image/gif") && info.Orientation == "TopLeft")
            return await ResolveAsync(workspaceId, artifactId, ct);

        var key = Encoding.UTF8.GetBytes($"image-request-v1:{artifactId}:{detail}:{maxEdge}:{limits.MaxBytes}");
        var derivedId = "vision-" + Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant()[..32];
        var cached = await ResolveAsync(workspaceId, derivedId, ct);
        if (cached is not null) return cached with { ArtifactId = artifactId };
        await ProcessingSlot.WaitAsync(ct);
        try
        {
            cached = await ResolveAsync(workspaceId, derivedId, ct);
            if (cached is not null) return cached with { ArtifactId = artifactId };
            var edge = Math.Min(maxEdge, Math.Max(info.Width, info.Height));
            var pixelRatio = Math.Sqrt((double)ImagePreprocessing.MaxOutputPixels / ((long)info.Width * info.Height));
            edge = Math.Min(edge, (int)(Math.Max(info.Width, info.Height) * Math.Min(1, pixelRatio)));
            // Only the final candidate is persisted. All attempts start from the original pixels.
            for (var attempt = 0; attempt < 12; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var format = attempt == 0 && info.Bytes <= limits.MaxBytes ? "png" : "jpeg";
                var quality = attempt <= 1 ? 85 : attempt == 2 ? 70 : 55;
                if (attempt >= 3) edge = Math.Max(16, (int)(edge * 0.7));
                var bytes = ImagePreprocessing.Process(source.Path,
                    new ImageProcessingOptions(MaxEdge: edge, Format: format, Quality: quality), ct);
                if (bytes.LongLength > limits.MaxBytes) continue;
                await using var stream = new MemoryStream(bytes, writable: false);
                await SaveIdempotentAsync(workspaceId, derivedId, stream, "application/octet-stream", ct: ct);
                logger.LogInformation("[VisionPreprocess] source={Source} derived={Derived} sourceBytes={SourceBytes} preparedBytes={Bytes} maxEdge={Edge} byteBudget={Budget}",
                    artifactId, derivedId, info.Bytes, bytes.Length, edge, limits.MaxBytes);
                var result = await ResolveAsync(workspaceId, derivedId, ct);
                return result is null ? null : result with { ArtifactId = artifactId };
            }
        }
        finally { ProcessingSlot.Release(); }
        throw new VisionPipelineException(VisionErrorCodes.RequestLimitExceeded,
            $"Image {artifactId} could not fit {limits.MaxBytes} bytes and {maxEdge}px after preprocessing.",
            userMessage: $"图片经过自动缩放和压缩，仍无法满足本次请求分配的 {limits.MaxBytes:N0} 字节、最长边 {maxEdge} 像素限制。请减少图片或分批处理；原图已保留，仍可继续发送文字消息。");
    }
}

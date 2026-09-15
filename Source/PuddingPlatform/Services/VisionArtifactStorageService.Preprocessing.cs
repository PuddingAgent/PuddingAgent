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
}

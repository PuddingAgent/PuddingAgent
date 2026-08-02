using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using PuddingPlatform.Services;

namespace PuddingAgent.Services;

public sealed record FeishuImageUploadPayload(
    byte[] Content,
    string FileName,
    string MimeType,
    bool Transcoded);

/// <summary>
/// Keeps the original high-resolution Vision Artifact untouched while creating
/// a bounded JPEG delivery copy when Feishu's image-upload limit requires it.
/// </summary>
public sealed class FeishuImageUploadPreparationService(
    ILogger<FeishuImageUploadPreparationService> logger)
{
    public const int MaxUploadBytes = 10 * 1024 * 1024;
    private const long MaxDecodedPixels = 150_000_000;

    public async Task<FeishuImageUploadPayload> PrepareAsync(
        VisualArtifactLocalFile image,
        CancellationToken ct = default)
    {
        var original = await File.ReadAllBytesAsync(image.Path, ct);
        if (original.Length <= MaxUploadBytes)
        {
            return new FeishuImageUploadPayload(
                original,
                Path.GetFileName(image.Path),
                image.MimeType,
                Transcoded: false);
        }

        var converted = TranscodeToBoundedJpeg(original, ct);
        logger.LogInformation(
            "[FeishuImage] Prepared delivery copy artifact={ArtifactId} originalBytes={OriginalBytes} uploadBytes={UploadBytes}",
            image.ArtifactId,
            original.Length,
            converted.Length);
        return new FeishuImageUploadPayload(
            converted,
            $"{image.ArtifactId}-feishu.jpg",
            "image/jpeg",
            Transcoded: true);
    }

    private static byte[] TranscodeToBoundedJpeg(
        byte[] sourceBytes,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
        {
            throw new PlatformNotSupportedException(
                "The C# image delivery transcoder currently requires Windows 7 or later.");
        }

        using var sourceStream = new MemoryStream(sourceBytes, writable: false);
        using var source = Image.FromStream(
            sourceStream,
            useEmbeddedColorManagement: true,
            validateImageData: true);
        if ((long)source.Width * source.Height > MaxDecodedPixels)
        {
            throw new InvalidOperationException(
                $"Image dimensions {source.Width}x{source.Height} exceed the safe decode limit.");
        }

        var jpeg = ImageCodecInfo.GetImageEncoders()
            .Single(codec => string.Equals(
                codec.MimeType,
                "image/jpeg",
                StringComparison.OrdinalIgnoreCase));
        var scale = 1d;
        for (var pass = 0; pass < 6; pass++)
        {
            ct.ThrowIfCancellationRequested();
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));
            using var bitmap = new Bitmap(
                width,
                height,
                PixelFormat.Format24bppRgb);
            bitmap.SetResolution(
                Math.Clamp(source.HorizontalResolution, 72, 300),
                Math.Clamp(source.VerticalResolution, 72, 300));
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.White);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality =
                    CompositingQuality.HighQuality;
                graphics.InterpolationMode =
                    InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(
                    source,
                    new Rectangle(0, 0, width, height));
            }

            byte[]? smallest = null;
            foreach (var quality in new long[] { 92, 85, 78, 70, 62 })
            {
                ct.ThrowIfCancellationRequested();
                using var output = new MemoryStream();
                using var parameters = new EncoderParameters(1);
                parameters.Param[0] = new EncoderParameter(
                    Encoder.Quality,
                    quality);
                bitmap.Save(output, jpeg, parameters);
                var candidate = output.ToArray();
                smallest = candidate;
                if (candidate.Length <= MaxUploadBytes)
                    return candidate;
            }

            var ratio = smallest is { Length: > 0 }
                ? Math.Sqrt(
                    MaxUploadBytes * 0.90d / smallest.Length)
                : 0.75d;
            scale *= Math.Clamp(ratio, 0.55d, 0.82d);
        }

        throw new InvalidOperationException(
            $"Image could not be reduced below the {MaxUploadBytes}-byte Feishu upload limit.");
    }
}

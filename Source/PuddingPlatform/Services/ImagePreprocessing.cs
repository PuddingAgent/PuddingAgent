using PuddingCode.Core;
using SkiaSharp;

namespace PuddingPlatform.Services;

public sealed record ImageProcessingOptions(
    int? CropX = null, int? CropY = null, int? CropWidth = null, int? CropHeight = null,
    int Rotation = 0, double Scale = 1, int? MaxEdge = null,
    bool Grayscale = false, bool Denoise = false, string? Format = null, int Quality = 85);

public sealed record ImageFileInfo(
    string MimeType, int Width, int Height, int EncodedWidth, int EncodedHeight,
    int FrameCount, string Orientation, long Bytes);

/// <summary>Deterministic local image processing. No model, Agent, route or network dependency.</summary>
public static class ImagePreprocessing
{
    public const int MaxSourceEdge = 32768;
    public const long MaxSourcePixels = 67_108_864;
    public const int MaxOutputEdge = 8192;
    public const long MaxOutputPixels = 33_554_432;

    public static ImageFileInfo Inspect(string path)
    {
        using var stream = File.OpenRead(path);
        var byteLength = stream.Length;
        using var codec = SKCodec.Create(stream)
            ?? throw Error(VisionErrorCodes.MediaInvalid, "Image header could not be decoded.");
        var info = codec.Info;
        var mime = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Jpeg => "image/jpeg", SKEncodedImageFormat.Png => "image/png",
            SKEncodedImageFormat.Webp => "image/webp", SKEncodedImageFormat.Gif => "image/gif",
            SKEncodedImageFormat.Bmp => "image/bmp",
            _ => throw Error(VisionErrorCodes.MediaInvalid, "Supported sources: JPEG, PNG, GIF, WebP and BMP (converted before model input)."),
        };
        if (info.Width < 1 || info.Height < 1 || info.Width > MaxSourceEdge || info.Height > MaxSourceEdge
            || (long)info.Width * info.Height > MaxSourcePixels)
            throw Error(VisionErrorCodes.RequestLimitExceeded, $"Source exceeds {MaxSourceEdge}px or {MaxSourcePixels} pixels; supply a smaller source or tiles.");
        var swap = (int)codec.EncodedOrigin >= 5;
        return new ImageFileInfo(mime, swap ? info.Height : info.Width, swap ? info.Width : info.Height,
            info.Width, info.Height, Math.Max(codec.FrameCount, 1), codec.EncodedOrigin.ToString(), byteLength);
    }

    public static byte[] Process(string path, ImageProcessingOptions options, CancellationToken ct = default)
    {
        var metadata = Inspect(path); // Bound decoded allocation before constructing any bitmap.
        var crop = options.CropX.HasValue || options.CropY.HasValue || options.CropWidth.HasValue || options.CropHeight.HasValue;
        var x = options.CropX ?? 0; var y = options.CropY ?? 0;
        var width = options.CropWidth ?? metadata.Width; var height = options.CropHeight ?? metadata.Height;
        if (crop && (options.CropX is null || options.CropY is null || options.CropWidth is null || options.CropHeight is null)
            || x < 0 || y < 0 || width < 1 || height < 1 || (long)x + width > metadata.Width || (long)y + height > metadata.Height)
            throw Error(VisionErrorCodes.SourceInvalid, "Provide a complete crop rectangle within the upright source dimensions.");
        if (options.Rotation % 90 != 0 || !double.IsFinite(options.Scale) || options.Scale <= 0 || options.Scale > 8
            || options.MaxEdge is < 1 or > MaxOutputEdge || options.Quality is < 1 or > 100)
            throw Error(VisionErrorCodes.SourceInvalid, "rotation must be a multiple of 90; scale in (0,8]; max_edge in [1,8192]; quality in [1,100].");
        var degrees = (options.Rotation % 360 + 360) % 360;
        var rotatedW = degrees is 90 or 270 ? height : width;
        var rotatedH = degrees is 90 or 270 ? width : height;
        var ratio = options.Scale;
        if (options.MaxEdge is { } maxEdge) ratio = Math.Min(ratio, (double)maxEdge / Math.Max(rotatedW, rotatedH));
        var outW = Math.Max(1, (int)Math.Round(rotatedW * ratio));
        var outH = Math.Max(1, (int)Math.Round(rotatedH * ratio));
        if (outW > MaxOutputEdge || outH > MaxOutputEdge || (long)outW * outH > MaxOutputPixels)
            throw Error(VisionErrorCodes.RequestLimitExceeded, "Output is too large; set max_edge or select a smaller crop.");
        var format = (options.Format ?? (metadata.MimeType == "image/jpeg" ? "jpeg" : "png")).ToLowerInvariant();
        var encoding = format switch
        {
            "jpeg" or "jpg" => SKEncodedImageFormat.Jpeg, "png" => SKEncodedImageFormat.Png,
            "webp" => SKEncodedImageFormat.Webp,
            _ => throw Error(VisionErrorCodes.SourceInvalid, "format must be jpeg, png or webp."),
        };
        ct.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(path);
        using var codec = SKCodec.Create(stream)
            ?? throw Error(VisionErrorCodes.MediaInvalid, "Image could not be decoded.");
        using var bitmap = new SKBitmap(new SKImageInfo(metadata.EncodedWidth, metadata.EncodedHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
            throw Error(VisionErrorCodes.MediaInvalid, "Image pixels are incomplete or invalid.");
        using var upright = SKSurface.Create(new SKImageInfo(metadata.Width, metadata.Height))
            ?? throw Error(VisionErrorCodes.MediaInvalid, "Unable to allocate image canvas.");
        Orient(upright.Canvas, codec.EncodedOrigin, metadata.Width, metadata.Height);
        upright.Canvas.DrawBitmap(bitmap, 0, 0);
        using var uprightImage = upright.Snapshot();
        using var surface = SKSurface.Create(new SKImageInfo(outW, outH))
            ?? throw Error(VisionErrorCodes.MediaInvalid, "Unable to allocate output canvas.");
        surface.Canvas.Clear(encoding == SKEncodedImageFormat.Jpeg ? SKColors.White : SKColors.Transparent);
        surface.Canvas.Scale((float)outW / rotatedW, (float)outH / rotatedH);
        switch (degrees)
        {
            case 90: surface.Canvas.Translate(height, 0); surface.Canvas.RotateDegrees(90); break;
            case 180: surface.Canvas.Translate(width, height); surface.Canvas.RotateDegrees(180); break;
            case 270: surface.Canvas.Translate(0, width); surface.Canvas.RotateDegrees(270); break;
        }
        using var paint = new SKPaint { IsAntialias = true };
        using var gray = options.Grayscale ? SKColorFilter.CreateColorMatrix([
            .2126f,.7152f,.0722f,0,0, .2126f,.7152f,.0722f,0,0,
            .2126f,.7152f,.0722f,0,0, 0,0,0,1,0]) : null;
        using var blur = options.Denoise ? SKImageFilter.CreateBlur(1.2f, 1.2f) : null;
        paint.ColorFilter = gray; paint.ImageFilter = blur;
        surface.Canvas.DrawImage(uprightImage, new SKRect(x,y,x+width,y+height), new SKRect(0,0,width,height),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        ct.ThrowIfCancellationRequested();
        using var image = surface.Snapshot();
        using var encoded = image.Encode(encoding, options.Quality)
            ?? throw Error(VisionErrorCodes.MediaInvalid, "Image encoding failed.");
        return encoded.ToArray();
    }

    private static void Orient(SKCanvas canvas, SKEncodedOrigin origin, int width, int height)
    {
        switch ((int)origin)
        {
            case 2: canvas.Translate(width,0); canvas.Scale(-1,1); break;
            case 3: canvas.Translate(width,height); canvas.RotateDegrees(180); break;
            case 4: canvas.Translate(0,height); canvas.Scale(1,-1); break;
            case 5: canvas.RotateDegrees(90); canvas.Scale(1,-1); break;
            case 6: canvas.Translate(width,0); canvas.RotateDegrees(90); break;
            case 7: canvas.Translate(width,height); canvas.RotateDegrees(90); canvas.Scale(-1,1); break;
            case 8: canvas.Translate(0,height); canvas.RotateDegrees(270); break;
        }
    }

    private static VisionPipelineException Error(string code, string message) => new(code, message);
}

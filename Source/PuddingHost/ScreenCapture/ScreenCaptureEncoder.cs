using SkiaSharp;

namespace PuddingHost.ScreenCapture;

/// <summary>
/// 把 BGRA 像素编码成 PNG/JPEG。复用宿主机已引用的 SkiaSharp，
/// **不引入新的原生依赖**（这也是没有用 System.Drawing 的原因：那需要额外包，
/// 而本机出网受限、恢复不可靠）。
/// </summary>
public static class ScreenCaptureEncoder
{
    public static readonly string[] SupportedFormats = ["png", "jpeg"];

    /// <summary>编码；<paramref name="format"/> 只接受 <see cref="SupportedFormats"/> 中的值。</summary>
    public static byte[] Encode(CapturedScreenImage image, string format)
    {
        ArgumentNullException.ThrowIfNull(image);

        var normalized = string.IsNullOrWhiteSpace(format) ? "png" : format.Trim().ToLowerInvariant();
        var encodedFormat = normalized switch
        {
            "png" => SKEncodedImageFormat.Png,
            "jpeg" or "jpg" => SKEncodedImageFormat.Jpeg,
            _ => throw new ArgumentException(
                $"format '{format}' is not supported (png, jpeg)", nameof(format)),
        };

        // 屏幕像素的 alpha 恒为 0（32bpp BI_RGB 不填 alpha）；声明 Opaque 才不会把整幅图当全透明。
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var bitmap = new SKBitmap(info);

        var target = bitmap.GetPixels();
        var sourceStride = image.Stride;
        var targetStride = bitmap.RowBytes;

        if (sourceStride == targetStride && sourceStride == image.Width * 4)
        {
            System.Runtime.InteropServices.Marshal.Copy(image.Bgra, 0, target, image.Bgra.Length);
        }
        else
        {
            // 行跨距不一致时必须逐行拷：一次性 Marshal.Copy 会把行尾填充当成像素写进去（画面斜切）。
            for (var y = 0; y < image.Height; y++)
            {
                var sourceOffset = y * sourceStride;
                var targetOffset = y * targetStride;
                System.Runtime.InteropServices.Marshal.Copy(
                    image.Bgra, sourceOffset, target + targetOffset, Math.Min(sourceStride, targetStride));
            }
        }

        using var snapshot = SKImage.FromBitmap(bitmap);
        using var data = snapshot.Encode(encodedFormat, normalized == "png" ? 100 : 85)
            ?? throw new InvalidOperationException($"SkiaSharp failed to encode the image as {normalized}");

        return data.ToArray();
    }
}

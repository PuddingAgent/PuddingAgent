using SkiaSharp;

namespace PuddingPlatformTests.Services;

/// <summary>ADR-077 存储合同测试用的最小合法图片字节（magic bytes/尺寸嗅探可通过）。</summary>
public static class VisionTestImages
{
    /// <summary>1×1 透明 PNG（67 字节，IHDR/IDAT/IEND 完整）。</summary>
    public static byte[] MinimalPng()
    {
        var base64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";
        return Convert.FromBase64String(base64);
    }

    /// <summary>构造指定尺寸的真实 PNG 图像（IHDR/IDAT/IEND 完整，SKCodec 可完整解码）。
    /// 产品侧 ImagePreprocessing.Inspect 以 SKCodec 真解码做嗅探，仅拼头部的伪 PNG 无法通过。</summary>
    public static byte[] PngImage(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}

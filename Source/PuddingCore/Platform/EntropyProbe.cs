using System.IO.Compression;
using System.Text;

namespace PuddingCode.Platform;

/// <summary>
/// 上下文熵探针：通过 gzip 压缩比近似文本的信息密度。
/// ratio = 原始 UTF-8 字节数 / gzip 压缩后字节数。下限 1.0。
/// 高度重复文本 ratio &gt;&gt; 1，高熵随机文本 ratio ≈ 1。
/// </summary>
public static class EntropyProbe
{
    /// <summary>
    /// 计算文本的 gzip 压缩比。
    /// 空文本或 null 返回 1.0，不抛出异常。
    /// </summary>
    public static double ComputeGzipRatio(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 1.0;

        var originalBytes = Encoding.UTF8.GetBytes(text);
        if (originalBytes.Length == 0)
            return 1.0;

        using var outputStream = new MemoryStream();
        using (var gzipStream = new GZipStream(outputStream, CompressionLevel.Fastest))
        {
            gzipStream.Write(originalBytes, 0, originalBytes.Length);
        }

        var compressedBytes = outputStream.ToArray();
        if (compressedBytes.Length == 0)
            return 1.0;

        var ratio = (double)originalBytes.Length / compressedBytes.Length;
        return Math.Max(1.0, ratio);
    }
}
